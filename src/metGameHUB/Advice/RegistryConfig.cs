using System.Globalization;
using System.Runtime.InteropServices;
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

    public static string Dump(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key));
        if (k == null) return "";
        var lines = new List<string>();
        foreach (var name in k.GetValueNames())
        {
            var text = Format(k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(name));
            if (text != null) lines.Add(name + "=" + text);
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

    // All-or-nothing: every edit is resolved and converted before the first SetValue.
    public static bool Apply(string key, IReadOnlyList<ConfigEdit> edits)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey(key), writable: true);
        if (k == null) return false;
        var names = k.GetValueNames();
        var writes = new List<(string Name, object Data, RegistryValueKind Kind)>();
        foreach (var e in edits)
        {
            var name = ResolveName(names, e.Key);
            if (name == null) return false;
            var kind = k.GetValueKind(name);
            var converted = Convert(e.Value, Classify(k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), kind));
            if (converted == null) return false;
            writes.Add((name, converted.Value.Data, converted.Value.Kind));
        }
        foreach (var w in writes) k.SetValue(w.Name, w.Data, w.Kind);
        return true;
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
            case ValueKind.Float when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f):
                return (BitConverter.SingleToInt32Bits(f), RegistryValueKind.DWord);
            case ValueKind.BinaryString:
                return (Encoding.UTF8.GetBytes(value + "\0"), RegistryValueKind.Binary);
            case ValueKind.Sz:
                return (value, RegistryValueKind.String);
            default:
                return null;
        }
    }

    internal enum ValueKind { None, Int, Float, BinaryString, Sz }

    // Unity writes floats as raw IEEE-754 bits: small ints are denormal-sized as floats, so only large finite patterns count as Float.
    internal static ValueKind Classify(object? data, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord when data is int i:
                return unchecked((uint)i) > 1_000_000 && float.IsFinite(BitConverter.Int32BitsToSingle(i)) ? ValueKind.Float : ValueKind.Int;
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
        ValueKind.Float => BitConverter.Int32BitsToSingle((int)data!).ToString("R", CultureInfo.InvariantCulture),
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
