using MetGameHUB.Advice;

namespace MetGameHUB.Ui;

public sealed record AdviceRowVm(string Name, string Current, string Recommended, string Reason, bool IsConfig);

public static class AdviceTable
{
    public static List<AdviceRowVm> Build(Advice.Advice? advice, IReadOnlyList<CurrentSetting>? current)
    {
        if (advice == null)
            return current?.Select(s => new AdviceRowVm(s.Name, s.Current ?? "—", "—", "", s.Key != null)).ToList() ?? [];
        return advice.Result.Settings.Select(s => new AdviceRowVm(s.Name, Match(s, current) ?? s.Current ?? "—", s.Recommended, s.Reason, s.Key != null)).ToList();
    }

    static string? Match(SettingAdvice s, IReadOnlyList<CurrentSetting>? current) =>
        s.Key == null || current == null ? null
            : current.FirstOrDefault(c => c.Key != null
                && string.Equals(c.Key, s.Key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Section, s.Section, StringComparison.OrdinalIgnoreCase))?.Current;
}
