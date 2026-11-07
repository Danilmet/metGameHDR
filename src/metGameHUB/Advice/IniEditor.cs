using System.Text;
using System.Text.RegularExpressions;

namespace MetGameHUB.Advice;

public static class IniEditor
{
    private const string NonKeyPrefixes = "+-.!;#/";

    // Flat one-key-per-line JSON (Remedy renderer.ini): groups are indent, key, separator, value, trailing comma.
    private static readonly Regex JsonLine = new("^(\\s*)\"([^\"]+)\"(\\s*:\\s*)(.*?)(,?)\\s*$");

    public static string Apply(string text, IReadOnlyList<ConfigEdit> edits)
    {
        var nl = DetectNewline(text);
        var lines = Split(text);
        var json = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{');
        foreach (var edit in edits) ApplyOne(lines, edit, nl, json);
        return string.Concat(lines);
    }

    public static byte[] ApplyBytes(byte[] file, IReadOnlyList<ConfigEdit> edits)
    {
        Encoding enc;
        int bom;
        if (file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF) { enc = new UTF8Encoding(false); bom = 3; }
        else if (file.Length >= 2 && file[0] == 0xFF && file[1] == 0xFE) { enc = new UnicodeEncoding(false, false); bom = 2; }
        else if (file.Length >= 2 && file[0] == 0xFE && file[1] == 0xFF) { enc = new UnicodeEncoding(true, false); bom = 2; }
        else
        {
            bom = 0;
            enc = new UTF8Encoding(false, throwOnInvalidBytes: true);
            // Latin1 maps every byte to a char, so ANSI files round-trip untouched.
            try { enc.GetString(file); }
            catch (DecoderFallbackException) { enc = Encoding.Latin1; }
        }

        var body = enc.GetBytes(Apply(enc.GetString(file, bom, file.Length - bom), edits));
        return file.Take(bom).Concat(body).ToArray();
    }

    public static string? CurrentValue(string text, string? section, string key)
    {
        var lines = Split(text);
        var i = Find(lines, section, key, out _, out _);
        if (i < 0) return null;
        var content = Content(lines[i]);
        if (JsonLine.Match(content) is { Success: true } j) return j.Groups[4].Value;
        return content[ValueStart(content)..].TrimEnd();
    }

    private static void ApplyOne(List<string> lines, ConfigEdit edit, string nl, bool json)
    {
        var i = Find(lines, edit.Section, edit.Key, out var sectionFound, out var lastNonBlank);
        if (i >= 0)
        {
            var content = Content(lines[i]);
            if (JsonLine.Match(content) is { Success: true } j)
            {
                var v = j.Groups[4];
                var value = v.Value.StartsWith('"') && !edit.Value.StartsWith('"') ? $"\"{edit.Value}\"" : edit.Value;
                lines[i] = content[..v.Index] + value + content[(v.Index + v.Length)..] + lines[i][content.Length..];
                return;
            }
            lines[i] = content[..ValueStart(content)] + edit.Value + lines[i][content.Length..];
            return;
        }

        if (json) return;
        var newLine = $"{edit.Key}={edit.Value}";
        if (!sectionFound)
        {
            if (lines.Count > 0)
            {
                if (!EndsWithNewline(lines[^1])) lines[^1] += nl;
                if (Content(lines[^1]).Trim().Length > 0) lines.Add(nl);
            }
            lines.Add($"[{edit.Section}]{nl}");
            lines.Add(newLine + nl);
            return;
        }

        var at = lastNonBlank + 1;
        if (at > 0 && !EndsWithNewline(lines[at - 1]))
        {
            lines[at - 1] += nl;
            lines.Insert(at, newLine);
        }
        else lines.Insert(at, newLine + nl);
    }

    private static int Find(List<string> lines, string? section, string key, out bool sectionFound, out int lastNonBlank)
    {
        if (string.IsNullOrWhiteSpace(section)) section = null;
        sectionFound = section is null;
        lastNonBlank = -1;
        string? current = null;
        var found = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var jm = JsonLine.Match(Content(lines[i]));
            if (jm.Success)
            {
                if (found < 0 && string.Equals(jm.Groups[2].Value, key.Trim(), StringComparison.OrdinalIgnoreCase)) found = i;
                continue;
            }
            var trimmed = Content(lines[i]).TrimStart();
            var isHeader = trimmed.StartsWith('[');
            if (isHeader)
            {
                var end = trimmed.IndexOf(']');
                current = (end > 0 ? trimmed[1..end] : trimmed[1..]).Trim();
            }
            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            sectionFound = true;
            if (trimmed.Length > 0) lastNonBlank = i;
            if (found < 0 && !isHeader && KeyMatches(trimmed, key)) found = i;
        }
        return found;
    }

    // With '=': key is the text before it. Without: the value is the last token (a quoted string
    // is one token) and the key part is everything before it.
    private static bool Parse(string content, out string key, out int valueStart, out bool hasEq)
    {
        key = "";
        valueStart = content.Length;
        hasEq = false;
        var start = content.Length - content.TrimStart().Length;
        if (start == content.Length || NonKeyPrefixes.Contains(content[start])) return false;

        var quote = content.IndexOf('"', start);
        var eq = content.IndexOf('=', start, (quote < 0 ? content.Length : quote) - start);
        if (eq >= 0)
        {
            hasEq = true;
            key = content[start..eq].Trim();
            valueStart = eq + 1;
            while (valueStart < content.Length && char.IsWhiteSpace(content[valueStart])) valueStart++;
            return true;
        }

        var end = content.TrimEnd().Length;
        var tokenStart = end - 1;
        var open = content[tokenStart] == '"' && tokenStart > start ? content.LastIndexOf('"', tokenStart - 1) : -1;
        if (open > start) tokenStart = open;
        else while (tokenStart > start && !char.IsWhiteSpace(content[tokenStart - 1])) tokenStart--;

        if (tokenStart <= start) { key = content[start..end]; return true; }
        key = content[start..tokenStart].Trim();
        valueStart = tokenStart;
        return true;
    }

    private static bool KeyMatches(string content, string key)
    {
        if (!Parse(content, out var k, out _, out var hasEq)) return false;
        key = key.Trim();
        if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
        if (hasEq) return false;
        var lastWord = k[(k.LastIndexOfAny([' ', '\t']) + 1)..];
        return string.Equals(lastWord, key, StringComparison.OrdinalIgnoreCase);
    }

    private static int ValueStart(string content)
    {
        Parse(content, out _, out var v, out _);
        return v;
    }

    private static string Content(string line) => line.TrimEnd('\r', '\n');

    private static bool EndsWithNewline(string line) => line.EndsWith('\n');

    private static string DetectNewline(string text)
    {
        var i = text.IndexOf('\n');
        return i >= 0 && (i == 0 || text[i - 1] != '\r') ? "\n" : "\r\n";
    }

    private static List<string> Split(string text)
    {
        var lines = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var i = text.IndexOf('\n', start);
            var end = i < 0 ? text.Length : i + 1;
            lines.Add(text[start..end]);
            start = end;
        }
        return lines;
    }
}
