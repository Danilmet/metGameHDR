using System.Text;

namespace MetGameHDR.Advice;

public static class IniEditor
{
    private const string NonKeyPrefixes = "+-.!;#";

    public static string Apply(string text, IReadOnlyList<ConfigEdit> edits)
    {
        var nl = DetectNewline(text);
        var lines = Split(text);
        foreach (var edit in edits) ApplyOne(lines, edit, nl);
        return string.Concat(lines);
    }

    public static byte[] ApplyBytes(byte[] file, IReadOnlyList<ConfigEdit> edits)
    {
        Encoding enc;
        int bom;
        if (file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF) { enc = new UTF8Encoding(false); bom = 3; }
        else if (file.Length >= 2 && file[0] == 0xFF && file[1] == 0xFE) { enc = new UnicodeEncoding(false, false); bom = 2; }
        else if (file.Length >= 2 && file[0] == 0xFE && file[1] == 0xFF) { enc = new UnicodeEncoding(true, false); bom = 2; }
        else { enc = new UTF8Encoding(false); bom = 0; }

        var body = enc.GetBytes(Apply(enc.GetString(file, bom, file.Length - bom), edits));
        return file.Take(bom).Concat(body).ToArray();
    }

    public static string? CurrentValue(string text, string? section, string key)
    {
        var lines = Split(text);
        var i = Find(lines, section, key, out _, out _);
        if (i < 0) return null;
        var content = Content(lines[i]);
        return content[ValueStart(content)..].TrimEnd();
    }

    private static void ApplyOne(List<string> lines, ConfigEdit edit, string nl)
    {
        var i = Find(lines, edit.Section, edit.Key, out var sectionFound, out var lastNonBlank);
        if (i >= 0)
        {
            var content = Content(lines[i]);
            lines[i] = content[..ValueStart(content)] + edit.Value + lines[i][content.Length..];
            return;
        }

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
        sectionFound = section is null;
        lastNonBlank = -1;
        string? current = null;
        var found = -1;
        for (var i = 0; i < lines.Count; i++)
        {
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
            if (found < 0 && !isHeader && KeyOf(trimmed) is { } k
                && string.Equals(k, key.Trim(), StringComparison.OrdinalIgnoreCase)) found = i;
        }
        return found;
    }

    private static string? KeyOf(string trimmed)
    {
        if (trimmed.Length == 0 || NonKeyPrefixes.Contains(trimmed[0])) return null;
        var sep = trimmed.IndexOf('=');
        if (sep < 0) sep = trimmed.AsSpan().IndexOfAny(" \t");
        return (sep < 0 ? trimmed : trimmed[..sep]).Trim();
    }

    // Start of the replaceable remainder: after the separator and the whitespace following it.
    private static int ValueStart(string content)
    {
        var start = content.Length - content.TrimStart().Length;
        var eq = content.IndexOf('=', start);
        var ws = content.AsSpan(start).IndexOfAny(" \t");
        int i;
        if (eq >= 0 && (ws < 0 || eq <= start + ws)) i = eq + 1;
        else if (ws >= 0)
        {
            i = start + ws;
            while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
            if (i < content.Length && content[i] == '=') i++;
        }
        else return content.Length;
        while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
        return i;
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
