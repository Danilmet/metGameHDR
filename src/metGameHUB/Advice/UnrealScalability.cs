using System.Globalization;

namespace MetGameHUB.Advice;

public static class UnrealScalability
{
    private const string Section = "ScalabilityGroups";
    private static readonly string[] Levels = ["Low", "Medium", "High", "Epic", "Cinematic"];
    private static readonly (string Key, string Name)[] Table =
    [
        ("sg.ResolutionQuality", "Масштаб разрешения"),
        ("sg.ViewDistanceQuality", "Дальность прорисовки"),
        ("sg.AntiAliasingQuality", "Сглаживание"),
        ("sg.ShadowQuality", "Тени"),
        ("sg.GlobalIlluminationQuality", "Глобальное освещение"),
        ("sg.ReflectionQuality", "Отражения"),
        ("sg.PostProcessQuality", "Постобработка"),
        ("sg.TextureQuality", "Текстуры"),
        ("sg.EffectsQuality", "Эффекты"),
        ("sg.FoliageQuality", "Растительность"),
        ("sg.ShadingQuality", "Затенение"),
    ];

    public static CurrentSetting? Describe(string? section, string key, string rawValue)
    {
        if (!string.Equals(section, Section, StringComparison.OrdinalIgnoreCase)) return null;
        var entry = Table.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        if (entry.Key == null) return null;

        var value = rawValue;
        if (entry.Key == "sg.ResolutionQuality")
        {
            if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                value = $"{(long)Math.Round(pct)}%";
        }
        else if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                 && level is >= 0 and < 5)
        {
            value = Levels[level];
        }
        return new CurrentSetting(entry.Name, value, Section, entry.Key);
    }

    public static List<CurrentSetting> FromFile(string text)
    {
        var rows = new List<CurrentSetting>();
        foreach (var (key, _) in Table)
        {
            var raw = IniEditor.CurrentValue(text, Section, key);
            if (raw != null) rows.Add(Describe(Section, key, raw)!);
        }
        return rows;
    }

    public static List<CurrentSetting> Merge(List<CurrentSetting> fromModel, string fileText)
    {
        var result = new List<CurrentSetting>();
        foreach (var s in fromModel)
        {
            var raw = s.Key == null ? null : IniEditor.CurrentValue(fileText, s.Section, s.Key);
            var local = raw == null ? null : Describe(s.Section, s.Key!, raw);
            result.Add(local == null ? s : s with { Current = local.Current });
        }

        foreach (var f in FromFile(fileText))
        {
            var present = result.Any(r =>
                string.Equals(r.Section, f.Section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Key, f.Key, StringComparison.OrdinalIgnoreCase));
            if (!present) result.Add(f);
        }
        return result;
    }
}
