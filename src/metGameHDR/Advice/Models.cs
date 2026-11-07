using System.Text.Json.Serialization;

namespace MetGameHDR.Advice;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Priority { Quality, Balanced, Responsive }

public sealed record GraphicsGoal(int Fps, Priority Priority);
public sealed record SettingAdvice(string Name, string Value, string Reason);
public sealed record ConfigEdit(string? Section, string Key, string Value);
public sealed record ConfigAdvice(string Path, List<ConfigEdit> Edits);
public sealed record AdviceResult(string Summary, List<SettingAdvice> Settings, ConfigAdvice? Config, string? Notes);
public sealed record Advice(AdviceResult Result, DateTime At);
