using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WukongBenchmark;

enum PrepMode { Inverted, Plain, HardContrast }

static class ScreenTools
{
    static OcrEngine? _engine;
    static OcrEngine Engine => _engine ??= CreateEngine();

    static OcrEngine CreateEngine()
    {
        foreach (var tag in new[] { "ru", "en-US" })
        {
            var lang = new Language(tag);
            if (OcrEngine.IsLanguageSupported(lang))
            {
                var e = OcrEngine.TryCreateFromLanguage(lang);
                if (e != null) return e;
            }
        }
        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Не найден язык для OCR. Добавьте русский или английский язык Windows (Параметры → Время и язык → Язык и регион).");
    }

    public static Bitmap Capture(Native.RECT r)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
        return bmp;
    }

    public static async Task<OcrResult> RecognizeAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        using var ras = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return await Engine.RecognizeAsync(sb);
    }

    static ColorMatrix BuildMatrix(PrepMode mode)
    {
        if (mode == PrepMode.Plain) return new ColorMatrix(); 

        bool inv = mode == PrepMode.Inverted;
        float c = inv ? 1.5f : 3f;  
        float k = inv ? -c : c;    
        float off = inv ? 0.5f + 0.5f * c : 0.5f - 0.5f * c;
        return new ColorMatrix(new float[][]
        {
            new float[] { k * 0.299f, k * 0.299f, k * 0.299f, 0, 0 },
            new float[] { k * 0.587f, k * 0.587f, k * 0.587f, 0, 0 },
            new float[] { k * 0.114f, k * 0.114f, k * 0.114f, 0, 0 },
            new float[] { 0, 0, 0, 1, 0 },
            new float[] { off, off, off, 0, 1 },
        });
    }

    public static Bitmap Preprocess(Bitmap src, RectangleF frac, float scale, PrepMode mode = PrepMode.Inverted)
    {
        int x = (int)(frac.X * src.Width), y = (int)(frac.Y * src.Height);
        int w = (int)(frac.Width * src.Width), h = (int)(frac.Height * src.Height);
        var dst = new Bitmap((int)(w * scale), (int)(h * scale), PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(BuildMatrix(mode));
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), x, y, w, h, GraphicsUnit.Pixel, ia);
        return dst;
    }
}

sealed record Metrics(
    int? Avg, int? Max, int? Min, int? P5,
    string? VramGb, string? Resolution, bool Ok,
    string LeftRaw, string SettingsRaw);

static class ResultParser
{
    static readonly (string Key, string Pattern)[] Labels =
    {
        ("avg",  @"в\s*среднем|averag"),
        ("max",  @"максимум|maximum"),
        ("min",  @"минимум|minimum"),
        ("p5",   @"перцентил|percentile"),
        ("vram", @"видеопамят|video\s*memory|vram"),
    };

    public static async Task<Metrics> ParseAsync(Bitmap shot, string? debugPrefix = null)
    {
        var leftFrac = new RectangleF(0.02f, 0.29f, 0.28f, 0.35f);
        var variants = new (PrepMode Mode, float Scale)[]
        {
            (PrepMode.Inverted, 3f), (PrepMode.Plain, 2f), (PrepMode.HardContrast, 3f), (PrepMode.Inverted, 5f),
        };

        var cAvg = new List<int>(); var cMax = new List<int>(); var cMin = new List<int>(); var cP5 = new List<int>();
        var cVram = new List<string>();
        var raws = new List<string>();

        foreach (var (mode, scale) in variants)
        {
            using var img = ScreenTools.Preprocess(shot, leftFrac, scale, mode);
            if (debugPrefix != null)
                img.Save($"{debugPrefix}_left_{mode}_{scale}.png", ImageFormat.Png);

            var o = await ScreenTools.RecognizeAsync(img);
            raws.Add($"[{mode} x{scale}] {Flat(o.Text)}");

            var p = ParseLabeled(o.Text);
            if (p.Avg is int a) cAvg.Add(a);
            if (p.Max is int mx) cMax.Add(mx);
            if (p.Min is int mn) cMin.Add(mn);
            if (p.P5 is int p5v) cP5.Add(p5v);
            if (p.Vram != null) cVram.Add(p.Vram);
        }

        int? avg = Vote(cAvg), max = Vote(cMax), min = Vote(cMin), p5 = Vote(cP5);
        string? vram = cVram.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        if (vram == null)
            vram = await ReadVramAsync(shot, debugPrefix);


        if (avg == null && max != null && p5 != null)
            avg = await ReadAvgAsync(shot, p5.Value, max.Value, debugPrefix);

        bool ok = avg.HasValue && max.HasValue && min.HasValue && p5.HasValue
                  && min <= p5 && p5 <= avg && avg <= max;

        using var rightImg = ScreenTools.Preprocess(shot, new RectangleF(0.58f, 0.25f, 0.40f, 0.46f), 2.5f);
        var right = await ScreenTools.RecognizeAsync(rightImg);
        var res = Regex.Match(right.Text, @"(\d{3,4})\s*[x×хХ]\s*(\d{3,4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return new Metrics(
            avg, max, min, p5, vram,
            res.Success ? $"{res.Groups[1].Value}x{res.Groups[2].Value}" : null,
            ok,
            string.Join("  ||  ", raws),
            Flat(right.Text));
    }

    static string Flat(string s) => s.Replace("\r", " ").Replace("\n", " | ");

    static int? Vote(List<int> c) =>
        c.Count == 0 ? null : c.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;

    static (int? Avg, int? Max, int? Min, int? P5, string? Vram) ParseLabeled(string raw)
    {
        string text = Regex.Replace(raw, @"5\s*-\s*[йиЙИ]|5\s*th", " ", RegexOptions.IgnoreCase);

        var found = new List<(string Key, int Start, int End)>();
        foreach (var (key, pattern) in Labels)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success) found.Add((key, m.Index, m.Index + m.Length));
        }
        found = found.OrderBy(f => f.Start).ToList();

        var seg = new Dictionary<string, string>();
        for (int i = 0; i < found.Count; i++)
        {
            int end = i + 1 < found.Count ? found[i + 1].Start : text.Length;
            seg[found[i].Key] = text.Substring(found[i].End, Math.Max(0, end - found[i].End));
        }

        int? Num(string key) => seg.TryGetValue(key, out var sgm) ? FirstNumber(sgm) : null;

        string? vram = null;
        if (seg.TryGetValue("vram", out var vs))
        {
            var m = Regex.Match(vs, @"(\d+(?:\s*[.,]\s*\d+)?)");
            if (m.Success)
            {
                string v = Regex.Replace(m.Groups[1].Value, @"\s", "").Replace(',', '.');
                if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 && d <= 64)
                    vram = v;
            }
        }
        if (vram == null)
        {
            var mm = Regex.Match(text, @"(\d+\s*[.,]\s*\d)\s*(?:гб|gb|г6|rб)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (mm.Success) vram = Regex.Replace(mm.Groups[1].Value, @"\s", "").Replace(',', '.');
        }
        return (Num("avg"), Num("max"), Num("min"), Num("p5"), vram);
    }

    static int? FirstNumber(string segment)
    {
        foreach (Match m in Regex.Matches(segment, @"[0-9ЗзОоOo][0-9ЗзОоOo ]*"))
        {
            if (!Regex.IsMatch(m.Value, @"[0-9]")) continue;          // одни буквы — это не число
            string digits = Regex.Replace(NormalizeDigits(m.Value), @"\D", "");
            if (digits.Length is >= 1 and <= 4 && int.TryParse(digits, out int v)) return v;
        }
        return null;
    }

    static string NormalizeDigits(string s) =>
        s.Replace('З', '3').Replace('з', '3').Replace('О', '0').Replace('о', '0').Replace('O', '0').Replace('o', '0')
         .Replace('l', '1').Replace('I', '1').Replace('|', '1');

    static async Task<string?> ReadVramAsync(Bitmap shot, string? debugPrefix)
    {
        var frac = new RectangleF(0.03f, 0.57f, 0.19f, 0.045f);
        var variants = new (PrepMode Mode, float Scale)[]
        {
            (PrepMode.Inverted, 4f), (PrepMode.Plain, 3f), (PrepMode.HardContrast, 4f), (PrepMode.Inverted, 6f),
        };
        foreach (var (mode, scale) in variants)
        {
            using var img = ScreenTools.Preprocess(shot, frac, scale, mode);
            if (debugPrefix != null)
                img.Save($"{debugPrefix}_vram_{mode}_{scale}.png", ImageFormat.Png);

            var o = await ScreenTools.RecognizeAsync(img);
            var m = Regex.Match(o.Text, @"(\d+\s*[.,]\s*\d+)");
            if (!m.Success) continue;
            string v = Regex.Replace(m.Groups[1].Value, @"\s", "").Replace(',', '.');
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 && d <= 64)
                return v;
        }
        return null;
    }

    static async Task<int?> ReadAvgAsync(Bitmap shot, int p5, int max, string? debugPrefix)
    {
        var frac = new RectangleF(0.03f, 0.32f, 0.20f, 0.08f);
        var variants = new (PrepMode Mode, float Scale)[]
        {
            (PrepMode.Inverted, 4f), (PrepMode.Plain, 3f), (PrepMode.HardContrast, 4f),
            (PrepMode.Inverted, 6f), (PrepMode.Plain, 5f), (PrepMode.Inverted, 2.5f),
        };
        foreach (var (mode, scale) in variants)
        {
            using var img = ScreenTools.Preprocess(shot, frac, scale, mode);
            if (debugPrefix != null)
                img.Save($"{debugPrefix}_avg_{mode}_{scale}.png", ImageFormat.Png);

            var o = await ScreenTools.RecognizeAsync(img);
            foreach (Match m in Regex.Matches(NormalizeDigits(o.Text), @"\d{2,4}"))
                if (int.TryParse(m.Value, out int v) && v >= p5 && v <= max)
                    return v;
        }
        return null;
    }
}
