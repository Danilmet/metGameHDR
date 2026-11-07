using System.Text;

namespace MetGameHDR.Steam;

public static class TextVdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        var pos = 0;
        var root = ParseBody(text, ref pos, topLevel: true);
        return root;
    }

    static Dictionary<string, object> ParseBody(string s, ref int pos, bool topLevel)
    {
        var dict = new Dictionary<string, object>();
        while (true)
        {
            var tok = Next(s, ref pos, out var isString);
            if (tok == null)
            {
                if (topLevel) return dict;
                throw new FormatException("Unexpected end of VDF: missing '}'");
            }
            if (!isString)
            {
                if (tok == "}" && !topLevel) return dict;
                throw new FormatException($"Unexpected '{tok}' at {pos}");
            }
            var value = Next(s, ref pos, out var valueIsString);
            if (value == null) throw new FormatException($"Missing value for key '{tok}'");
            if (valueIsString) dict[tok] = value;
            else if (value == "{") dict[tok] = ParseBody(s, ref pos, topLevel: false);
            else throw new FormatException($"Unexpected '{value}' after key '{tok}'");
        }
    }

    static string? Next(string s, ref int pos, out bool isString)
    {
        isString = false;
        while (pos < s.Length)
        {
            var c = s[pos];
            if (char.IsWhiteSpace(c)) pos++;
            else if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
            }
            else break;
        }
        if (pos >= s.Length) return null;
        if (s[pos] == '{' || s[pos] == '}') return s[pos++].ToString();
        if (s[pos] != '"') throw new FormatException($"Unexpected character '{s[pos]}' at {pos}");

        isString = true;
        pos++;
        var sb = new StringBuilder();
        while (pos < s.Length && s[pos] != '"')
        {
            if (s[pos] == '\\' && pos + 1 < s.Length && (s[pos + 1] == '\\' || s[pos + 1] == '"')) pos++;
            sb.Append(s[pos++]);
        }
        if (pos >= s.Length) throw new FormatException("Unterminated string");
        pos++;
        return sb.ToString();
    }
}
