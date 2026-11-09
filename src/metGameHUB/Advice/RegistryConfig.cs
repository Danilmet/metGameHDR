using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MetGameHUB.Advice;

// Unity PlayerPrefs live in HKCU\Software\<Company>\<Product>; the key string doubles as the config "path".
public static class RegistryConfig
{
    private const string Hkcu = @"HKCU\";
    private static readonly string[] Denied = ["Microsoft", "Classes", "Policies", "Wow6432Node"];

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryInfoKeyW(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegSetValueExW(SafeRegistryHandle hKey, string lpValueName, int Reserved, int dwType, byte[] lpData, int cbData);

    // Unity keeps float prefs as a REG_DWORD with 8 bytes of double; RegistryKey.SetValue cannot write that shape.
    internal static void WriteDouble(RegistryKey k, string name, double d)
    {
        var code = RegSetValueExW(k.Handle, name, 0, 4, BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(d)), 8);
        if (code != 0) throw new IOException($"RegSetValueEx: {code}");
    }

    public static string Normalize(string key)
    {
        var k = key.Trim().Replace('/', '\\').TrimEnd('\\');
        const string longForm = @"HKEY_CURRENT_USER\";
        return k.StartsWith(longForm, StringComparison.OrdinalIgnoreCase) ? Hkcu + k[longForm.Length..] : k;
    }

    public static bool IsAllowedKey(string key)
    {
        var k = Normalize(key);
        const string prefix = Hkcu + @"Software\";
        if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = k[prefix.Length..].Split('\\');
        return parts.Length >= 2 && parts.All(p => p.Length > 0) && !Denied.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
    }

    public static string SubKey(string key) => Normalize(key)[Hkcu.Length..];

    public static bool Exists(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key));
        return k != null;
    }

    public static DateTime Stamp(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key));
        return k != null && RegQueryInfoKeyW(k.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var ft) == 0
            ? DateTime.FromFileTimeUtc(ft) : default;
    }

    private const int MaxDumpValue = 256;

    public static string Dump(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key));
        if (k == null) return "";
        var lines = new List<string>();
        foreach (var name in k.GetValueNames())
        {
            var text = Format(k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(name));
            // Blobs and multi-line strings break name=value lines and eat the request budget.
            if (text == null || text.Length > MaxDumpValue || text.Contains('\r') || text.Contains('\n')) continue;
            lines.Add(name + "=" + (ConfigFinder.IsSecretKey(name) ? "***" : text));
        }
        lines.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", lines);
    }

    public static string? CurrentValue(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key));
        var real = k == null ? null : ResolveName(k.GetValueNames(), name);
        return real == null ? null : Format(k!.GetValue(real, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(real));
    }

    public static string BackupFileName(string key)
    {
        var norm = Normalize(key);
        var parts = norm.Split('\\').TakeLast(2).Select(p => string.Concat(p.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)));
        var safe = string.Join("_", parts);
        if (safe.Length > 60) safe = safe[..60];
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(norm.ToUpperInvariant()));
        return safe + "-" + System.Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public static void Backup(string key, string file) => RunReg("export", @"HKEY_CURRENT_USER\" + SubKey(key), file, "/y");

    public static void Restore(string file) => RunReg("import", file);

    private static void RunReg(string verb, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(verb);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException($"reg {verb}: failed to start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new IOException($"reg {verb}: {(string.IsNullOrWhiteSpace(stderr) ? stdout.Result : stderr).Trim()}");
    }

    // All-or-nothing: Prepare resolves and converts every edit; null means nothing may be written.
    public static bool Apply(string key, IReadOnlyList<ConfigEdit> edits)
    {
        if (Prepare(key, edits) is not { } writes) return false;
        Write(key, writes);
        return true;
    }

    internal static List<(string Name, object Data, RegistryValueKind Kind)>? Prepare(string key, IReadOnlyList<ConfigEdit> edits)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key));
        if (k == null) return null;
        var names = k.GetValueNames();
        var writes = new List<(string Name, object Data, RegistryValueKind Kind)>();
        foreach (var e in edits)
        {
            var name = ResolveName(names, e.Key);
            if (name == null) return null;
            var kind = k.GetValueKind(name);
            var converted = Convert(e.Value, Classify(k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), kind));
            if (converted == null) return null;
            writes.Add((name, converted.Value.Data, converted.Value.Kind));
        }
        return writes;
    }

    internal static void Write(string key, List<(string Name, object Data, RegistryValueKind Kind)> writes)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key), writable: true) ?? throw new IOException($"Registry key is gone: {key}");
        foreach (var w in writes)
            if (w.Data is double d) WriteDouble(k, w.Name, d);
            else k.SetValue(w.Name, w.Data, w.Kind);
    }

    internal static string? ResolveName(IReadOnlyList<string> names, string key)
    {
        var exact = names.FirstOrDefault(n => n.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var hits = names.Where(n => Regex.Replace(n, @"_h\d+$", "").Equals(key, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    internal static (object Data, RegistryValueKind Kind)? Convert(string value, ValueKind kind)
    {
        switch (kind)
        {
            case ValueKind.Int when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i):
                return (i, RegistryValueKind.DWord);
            case ValueKind.Double when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d):
                return (d, RegistryValueKind.DWord);
            case ValueKind.BinaryString:
                return (Encoding.UTF8.GetBytes(value + "\0"), RegistryValueKind.Binary);
            case ValueKind.Sz:
                return (value, RegistryValueKind.String);
            default:
                return null;
        }
    }

    internal enum ValueKind { None, Int, Double, BinaryString, Sz }

    // An 8-byte REG_DWORD (Unity float pref) comes back from GetValue as long holding double bits.
    internal static ValueKind Classify(object? data, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord when data is int:
                return ValueKind.Int;
            case RegistryValueKind.DWord when data is long l:
                return double.IsFinite(BitConverter.Int64BitsToDouble(l)) ? ValueKind.Double : ValueKind.None;
            case RegistryValueKind.Binary when data is byte[] b:
                return Utf8(b) != null ? ValueKind.BinaryString : ValueKind.None;
            case RegistryValueKind.String when data is string:
                return ValueKind.Sz;
            default:
                return ValueKind.None;
        }
    }

    internal static string? Format(object? data, RegistryValueKind kind) => Classify(data, kind) switch
    {
        ValueKind.Int => ((int)data!).ToString(CultureInfo.InvariantCulture),
        ValueKind.Double => BitConverter.Int64BitsToDouble((long)data!).ToString("R", CultureInfo.InvariantCulture),
        ValueKind.BinaryString => Utf8((byte[])data!),
        ValueKind.Sz => (string)data!,
        _ => null,
    };

    private static string? Utf8(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { return null; }
    }
}
