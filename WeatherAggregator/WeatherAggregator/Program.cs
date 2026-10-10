using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeatherAggregator;

public sealed class WeatherData
{
    public required string City { get; init; }
    public required string Country { get; init; }
    public required int CurrentTemperature { get; init; }
}

internal static class Program
{
    private const int MaxParallel = 4; 
    private const int MaxAttempts = 2; 
    private const int RequestTimeoutSeconds = 15;
    private const string ControlCity = "Moscow"; 

    private sealed record FetchResult(string City, WeatherData? Data, string? Error);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        List<string> cities;
        try
        {
            cities = await LoadCitiesAsync(args.FirstOrDefault(), cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось загрузить список городов: {ex.Message}");
            return 1;
        }
        if (cities.Count == 0)
        {
            Console.Error.WriteLine("Список городов пуст.");
            return 1;
        }
        Console.Error.WriteLine($"Уникальных городов в списке: {cities.Count}. Запрашиваю данные wttr.in...");

        FetchResult[] results;
        try
        {
            results = await FetchAllAsync(cities, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Отменено пользователем.");
            return 130;
        }

        var weather = results.Where(r => r.Data != null).Select(r => r.Data!).ToList();
        var failed = results.Where(r => r.Data == null).ToList();

        Console.WriteLine();
        foreach (var w in weather)
            Console.WriteLine($"{w.City}, {w.Country} {FormatTemp(w.CurrentTemperature)} °C");

        Console.WriteLine();
        var byCountry = weather
            .GroupBy(w => w.Country, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Country = g.Key,
                Count = g.Count(),
                Avg = (int)Math.Round(g.Average(w => w.CurrentTemperature), MidpointRounding.AwayFromZero),
                Min = g.Min(w => w.CurrentTemperature),
                Max = g.Max(w => w.CurrentTemperature),
            })
            .OrderBy(g => g.Country, StringComparer.OrdinalIgnoreCase);

        foreach (var g in byCountry)
        {
            string noun = g.Count == 1 ? "city" : "cities";
            Console.WriteLine($"{g.Country} — {g.Count} {noun}, avg: {FormatTemp(g.Avg)} °C, " +
                              $"min: {FormatTemp(g.Min)} °C, max: {FormatTemp(g.Max)} °C");
        }

        if (failed.Count > 0)
        {
            Console.WriteLine("\nНе удалось получить данные:");
            foreach (var f in failed) Console.WriteLine($"  {f.City}: {f.Error}");
        }

        if (weather.Count == 0)
            await DiagnoseAsync(cts.Token);

        return weather.Count == 0 ? 2 : failed.Count > 0 ? 3 : 0;
    }


    private static async Task<List<string>> LoadCitiesAsync(string? source, CancellationToken ct)
    {
        string text;
        if (source != null && Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            text = await Http.GetStringAsync(uri, ct);
        else
            text = await File.ReadAllTextAsync(source ?? FindDefaultCitiesFile(), ct);

        return ParseCities(text);
    }

    private static string FindDefaultCitiesFile()
    {
        foreach (string dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string path = Path.Combine(dir, "cities.txt");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Файл cities.txt не найден. Положите его рядом с программой или передайте путь/URL первым аргументом.");
    }

    internal static List<string> ParseCities(string text) =>
        text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('"', '\'', '[', ']', '\uFEFF', ' '))
            .Where(s => s.Length > 0 && !s.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();


    private static string BuildUrl(string city) => $"https://wttr.in/{Uri.EscapeDataString(city)}?format=j1";

    private static async Task<FetchResult[]> FetchAllAsync(IReadOnlyList<string> cities, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxParallel);
        int done = 0;

        var tasks = cities.Select(async city =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await FetchOneAsync(city, ct);
                int n = Interlocked.Increment(ref done);
                Console.Error.WriteLine($"  [{n}/{cities.Count}] {city}: {(result.Data != null ? "ok" : result.Error)}");
                return result;
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task<FetchResult> FetchOneAsync(string city, CancellationToken ct)
    {
        string url = BuildUrl(city);
        string error = "неизвестная ошибка";

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    return new FetchResult(city, Parse(city, doc.RootElement), null);
                }

                error = $"HTTP {(int)response.StatusCode}{await BodySnippetAsync(response, ct)}";
                bool transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!transient) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                error = $"таймаут ({RequestTimeoutSeconds} с)";
            }
            catch (HttpRequestException ex)
            {
                error = Describe(ex);
                if (ex.InnerException is AuthenticationException) break; 
            }
            catch (JsonException)
            {
                return new FetchResult(city, null, "ответ не в формате JSON (город не найден?)");
            }
            catch (FormatException ex)
            {
                return new FetchResult(city, null, $"неожиданный ответ API: {ex.Message}");
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
        }
        return new FetchResult(city, null, error);
    }

    private static async Task<string> BodySnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string body = Regex.Replace(await response.Content.ReadAsStringAsync(ct), @"\s+", " ").Trim();
            if (body.Length == 0) return "";
            return ": " + (body.Length > 160 ? body[..160] + "…" : body);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return "";
        }
    }

    private static string Describe(HttpRequestException ex)
    {
        if (ex.InnerException is AuthenticationException)
            return $"ошибка TLS/сертификата ({ex.InnerException.Message})";
        if (ex.StatusCode is { } code)
            return $"HTTP {(int)code}";
        return ex.InnerException is null
            ? ex.Message
            : $"{ex.Message} [{ex.InnerException.GetType().Name}: {ex.InnerException.Message}]";
    }

    private static async Task DiagnoseAsync(CancellationToken ct)
    {
        Console.Error.WriteLine($"\nДиагностика: контрольный запрос для {ControlCity}...");
        var control = await FetchOneAsync(ControlCity, ct);

        if (control.Data != null)
        {
            Console.Error.WriteLine("  Контрольный город получен. Сервис работает — проблема в названиях городов из файла (проверьте написание).");
            return;
        }

        Console.Error.WriteLine($"  {ControlCity}: {control.Error}");
        Console.Error.WriteLine(control.Error!.StartsWith("HTTP", StringComparison.Ordinal)
            ? "  Соединение работает, но сервис wttr.in отвечает ошибкой даже на контрольный запрос: он временно недоступен " +
              "или ограничивает запросы с вашего адреса. Подождите несколько минут и повторите."
            : "  Сервис не отвечает: проверьте сеть (например, curl.exe -v https://wttr.in/Moscow?format=j1).");
    }


    private static WeatherData Parse(string city, JsonElement root)
    {
        string tempText = StringOf(FirstOf(root, "current_condition"), "temp_C");
        if (!int.TryParse(tempText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int temp))
            throw new FormatException($"некорректная температура '{tempText}'");

        string country = StringOf(FirstOf(FirstOf(root, "nearest_area"), "country"), "value").Trim();
        if (country.Length == 0)
            throw new FormatException("в ответе пустое название страны");

        return new WeatherData { City = city, Country = country, CurrentTemperature = temp };
    }

    private static JsonElement FirstOf(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var arr)
        && arr.ValueKind == JsonValueKind.Array
        && arr.GetArrayLength() > 0
            ? arr[0]
            : throw new FormatException($"нет поля '{name}'");

    private static string StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"нет поля '{name}'");

    private static string FormatTemp(int t) =>
        t > 0 ? "+" + t.ToString(CultureInfo.InvariantCulture) : t.ToString(CultureInfo.InvariantCulture);
}
