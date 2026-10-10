using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WukongBenchmark;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(300);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); 
        Thread.Sleep(80);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); 
    }
}

sealed record SystemSpecs(string Cpu, string Gpu, string Ram, string Os, string Display)
{
    public string ToText() => $"CPU:   {Cpu}\nGPU:   {Gpu}\nRAM:   {Ram}\nОС:    {Os}\nЭкран: {Display}";
}

static class SystemInfo
{
    public static SystemSpecs Collect()
    {
        string cpu = string.Join("; ", Get("Win32_Processor").Select(o =>
            $"{o["Name"]?.ToString()?.Trim()} ({o["NumberOfCores"]} ядер / {o["NumberOfLogicalProcessors"]} потоков)"));

        string gpu = string.Join("; ", Get("Win32_VideoController").Select(o =>
            $"{o["Name"]?.ToString()?.Trim()} (драйвер {o["DriverVersion"]})"));

        var mods = Get("Win32_PhysicalMemory");
        double gb = mods.Sum(m => Convert.ToDouble(m["Capacity"] ?? 0)) / (1024.0 * 1024 * 1024);
        string speed = mods.Count > 0 ? (mods[0]["ConfiguredClockSpeed"] ?? mods[0]["Speed"])?.ToString() ?? "?" : "?";
        string ram = $"{gb:0.#} ГБ ({mods.Count} модул., {speed} МГц)";

        string os = string.Join("; ", Get("Win32_OperatingSystem").Select(o =>
            $"{o["Caption"]?.ToString()?.Trim()} (сборка {o["BuildNumber"]})"));

        string display = $"{Native.GetSystemMetrics(0)}x{Native.GetSystemMetrics(1)}";
        return new SystemSpecs(cpu, gpu, ram, os, display);
    }

    static List<ManagementBaseObject> Get(string wmiClass)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM " + wmiClass);
            foreach (ManagementBaseObject o in searcher.Get()) list.Add(o);
        }
        catch { /* WMI недоступен — вернём пустой список */ }
        return list;
    }
}

static class IniEditor
{
    public static void Apply(string path, Preset p)
    {
        var lines = File.ReadAllLines(path).ToList();

        int idx = lines.FindIndex(l => l.StartsWith("UISettingData=", StringComparison.Ordinal));
        if (idx < 0)
            throw new InvalidOperationException("В GameUserSettings.ini нет UISettingData. Запустите бенчмарк вручную один раз и закройте его.");

        string s = lines[idx];
        foreach (var (key, value) in p.Ui)
        {
            var rx = new Regex("\\(\"" + Regex.Escape(key) + "\",\\s*\"[^\"]*\"\\)");
            string tuple = $"(\"{key}\", \"{value}\")";
            s = rx.IsMatch(s) ? rx.Replace(s, tuple) : s[..^1] + "," + tuple + ")";
        }
        lines[idx] = s;

        foreach (var (key, value) in p.Sg) SetKey(lines, "ScalabilityGroups", "sg." + key, value);
        SetKey(lines, "RayTracing", "r.RayTracing.EnableInGame", p.RayTracing ? "True" : "False");
        SetKey(lines, "GSRenderSetting", "GSStreamingPoolSize", p.StreamingPool);

        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    static void SetKey(List<string> lines, string section, string key, string value)
    {
        string header = "[" + section + "]";
        int h = lines.FindIndex(l => l.Trim() == header);
        if (h < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim() != "") lines.Add("");
            lines.Add(header);
            lines.Add($"{key}={value}");
            return;
        }

        int end = h + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;

        for (int i = h + 1; i < end; i++)
        {
            if (lines[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }
        lines.Insert(h + 1, $"{key}={value}");
    }
}
