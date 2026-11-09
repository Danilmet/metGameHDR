using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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
