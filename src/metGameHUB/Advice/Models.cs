using System.Text.Json.Serialization;

namespace MetGameHUB.Advice;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoalStep { MaxQuality, Pretty, Balanced, Smooth, MaxFps }

public sealed record SettingAdvice(string Name, string? Current, string Recommended, string? Section, string? Key, string? Value, string Reason);
public sealed record ConfigEdit(string? Section, string Key, string Value);
public sealed record AdviceResult(string Summary, List<SettingAdvice> Settings, string? Notes);
// Format: "ini" | "json" | "none"
public sealed record ConfigLocation(string? Path, string Format, string? Note);
public sealed record ConfigStatus(string? Path, bool Exists, string Format, string? Note);
public sealed record Advice(AdviceResult Result, DateTime At);
