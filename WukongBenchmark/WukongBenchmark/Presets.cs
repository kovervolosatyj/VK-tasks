namespace WukongBenchmark;

sealed class Preset
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Rationale { get; init; } = "";
    public string ExpectedResolution { get; init; } = ""; 
    public bool RayTracing { get; init; }
    public string StreamingPool { get; init; } = "";

    public Dictionary<string, string> Ui { get; } = new();
    public Dictionary<string, string> Sg { get; } = new();
    public List<(string Name, string Value)> Human { get; } = new();
}


static class Presets
{
    public static Preset Cpu(string nativeResolution) => new Preset
    {
        Id = "cpu",
        Title = "CPU-тест (минимальная нагрузка на GPU)",
        ExpectedResolution = Half(nativeResolution),
        RayTracing = false,
        StreamingPool = "384",
        Rationale =
            "Все, что нагружает видеокарту, сведено к минимуму: разрешение вдвое ниже родного (1280×720 на мониторе 2560×1440), render scale 100% (без апскейла и без генерации кадров), " +
            "текстуры, тени, GI, отражения, сглаживание, постобработка, эффекты и материалы — «Низкие», трассировка и размытие выключены. " +
            "Дальность прорисовки и растительность оставлены «Высокими»: они увеличивают число объектов, draw call'ов, работу culling'а и стриминга, " +
            "то есть нагружают именно процессор, а видеокарте почти ничего не стоят. В итоге кадр упирается в CPU, и FPS отражает его производительность.",
        Human =
        {
            ("Разрешение", Half(nativeResolution).Replace('x', '×') + " (50% от родного)"),
            ("Render scale", "100%"),
            ("Апскейлер", "FSR (при 100% апскейла нет)"),
            ("Генерация кадров", "Выкл."),
            ("Трассировка лучей", "Выкл."),
            ("Текстуры / тени / GI / отражения / сглаживание / постобработка / эффекты / материалы", "Низкое"),
            ("Дальность прорисовки", "Высокая"),
            ("Растительность", "Высокая"),
            ("Размытие при движении", "Выкл."),
        }
    }.WithCpuValues(nativeResolution);

    public static Preset Gpu(string nativeResolution) => new Preset
    {
        Id = "gpu",
        Title = "GPU-тест (максимальная нагрузка на GPU)",
        ExpectedResolution = nativeResolution,
        RayTracing = true,
        StreamingPool = "640",
        Rationale =
            "Все, что нагружает видеокарту, выкручено на максимум: родное разрешение монитора, render scale 100% (рендер без апскейла), " +
            "все параметры качества «Ультра», трассировка лучей включена на максимальном уровне. Генерация кадров выключена: она искусственно " +
            "увеличивает/ограничивает FPS и маскирует реальную загрузку GPU. Процессору в таком режиме остаётся заметный запас, " +
            "поэтому кадр упирается в видеокарту, и FPS отражает её производительность.",
        Human =
        {
            ("Разрешение", $"{nativeResolution} (родное)"),
            ("Render scale", "100%"),
            ("Апскейлер", "FSR (при 100% апскейла нет)"),
            ("Генерация кадров", "Выкл."),
            ("Трассировка лучей", "Вкл., макс. уровень"),
            ("Все параметры качества", "Ультра"),
            ("Размытие при движении", "Выкл."),
        }
    }.WithGpuValues(nativeResolution);

    static string Half(string res)
    {
        var p = res.Split('x');
        return $"{int.Parse(p[0]) / 2}x{int.Parse(p[1]) / 2}";
    }

    static Preset WithCpuValues(this Preset p, string nativeResolution)
    {
        var ui = new Dictionary<string, string>
        {
            ["ScreenResolution"] = "3",
            ["WindowFullImageQuality"] = "500000",
            ["MotionBlur"] = "0",
            ["ImageQuality"] = Half(nativeResolution).Split('x')[1],  
            ["SuperResolutionSampling"] = "0",
            ["InsertFrame"] = "0",
            ["Rtx"] = "0",
            ["RtxLevel"] = "2",
            ["QualityLevel"] = "6", 
            ["ViewDistance"] = "3",
            ["VegetationQuality"] = "3",
            ["AntiAliasing"] = "1",
            ["PostProcessing"] = "1",
            ["ShadowQuality"] = "1",
            ["TextureQuality"] = "1",
            ["FxQuality"] = "1",
            ["MaterialQuality"] = "1",
            ["GlobalIllumination"] = "1",
            ["ReflectionQuality"] = "1",
        };
        foreach (var kv in ui) p.Ui[kv.Key] = kv.Value;

        var sg = new Dictionary<string, string>
        {
            ["ResolutionQuality"] = "100",
            ["ViewDistanceQuality"] = "2",
            ["AntiAliasingQuality"] = "0",
            ["ShadowQuality"] = "0",
            ["GlobalIlluminationQuality"] = "0",
            ["RayTracingQuality"] = "0",
            ["ReflectionQuality"] = "0",
            ["PostProcessQuality"] = "0",
            ["TextureQuality"] = "0",
            ["EffectsQuality"] = "0",
            ["FoliageQuality"] = "2",
            ["ShadingQuality"] = "0",
        };
        foreach (var kv in sg) p.Sg[kv.Key] = kv.Value;
        return p;
    }

    static Preset WithGpuValues(this Preset p, string nativeResolution)
    {
        var ui = new Dictionary<string, string>
        {
            ["ScreenResolution"] = "0", 
            ["ImageQuality"] = nativeResolution.Split('x')[1],
            ["WindowFullImageQuality"] = "1000000",
            ["MotionBlur"] = "0",
            ["SuperResolutionSampling"] = "0",
            ["InsertFrame"] = "0",
            ["Rtx"] = "1",
            ["RtxLevel"] = "3",
            ["QualityLevel"] = "4", 
            ["ViewDistance"] = "4",
            ["VegetationQuality"] = "4",
            ["AntiAliasing"] = "4",
            ["PostProcessing"] = "4",
            ["ShadowQuality"] = "4",
            ["TextureQuality"] = "4",
            ["FxQuality"] = "4",
            ["MaterialQuality"] = "4",
            ["GlobalIllumination"] = "4",
            ["ReflectionQuality"] = "4",
        };
        foreach (var kv in ui) p.Ui[kv.Key] = kv.Value;

        var sg = new Dictionary<string, string>
        {
            ["ResolutionQuality"] = "100",
            ["ViewDistanceQuality"] = "3",
            ["AntiAliasingQuality"] = "3",
            ["ShadowQuality"] = "3",
            ["GlobalIlluminationQuality"] = "3",
            ["RayTracingQuality"] = "0",
            ["ReflectionQuality"] = "3",
            ["PostProcessQuality"] = "3",
            ["TextureQuality"] = "3",
            ["EffectsQuality"] = "3",
            ["FoliageQuality"] = "3",
            ["ShadingQuality"] = "3",
        };
        foreach (var kv in sg) p.Sg[kv.Key] = kv.Value;
        return p;
    }
}
