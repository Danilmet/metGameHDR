namespace MetGameHDR.Advice;

// shortcut: duplicated from Tasks 2/3 so this branch compiles alone, delete at merge
public sealed record HardwareInfo(string Gpu, int? VramGb, string Cpu, int RamGb, int Width, int Height, int Hz, string Os);
public sealed record ConfigCandidate(string Path, string Text, bool Truncated);
