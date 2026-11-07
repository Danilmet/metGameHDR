using System.IO;
using System.Text;

namespace MetGameHUB.Steam;

public sealed class VdfObject : List<KeyValuePair<string, object>>
{
    public object? this[string key]
    {
        get
        {
            foreach (var p in this)
                if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            for (var i = 0; i < Count; i++)
                if (string.Equals(this[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    this[i] = new(this[i].Key, value);
                    return;
                }
            Add(new(key, value));
        }
    }
}

public static class BinaryVdf
{
    const byte TObject = 0, TString = 1, TInt = 2, TEnd = 8;

    public static VdfObject Read(byte[] data)
    {
        var pos = 0;
        var root = ReadObject(data, ref pos);
        if (pos != data.Length) throw new InvalidDataException("Trailing data after root object.");
        return root;
    }

    static VdfObject ReadObject(byte[] d, ref int pos)
    {
        var obj = new VdfObject();
        while (true)
        {
            if (pos >= d.Length) throw new InvalidDataException("Unexpected end of data.");
            var type = d[pos++];
            if (type == TEnd) return obj;
            var key = ReadString(d, ref pos);
            switch (type)
            {
                case TObject: obj.Add(new(key, ReadObject(d, ref pos))); break;
                case TString: obj.Add(new(key, ReadString(d, ref pos))); break;
                case TInt:
                    if (pos + 4 > d.Length) throw new InvalidDataException("Unexpected end of data.");
                    obj.Add(new(key, BitConverter.ToInt32(d, pos)));
                    pos += 4;
                    break;
                default: throw new InvalidDataException($"Unknown VDF type 0x{type:X2}.");
            }
        }
    }

    static string ReadString(byte[] d, ref int pos)
    {
        var end = Array.IndexOf(d, (byte)0, pos);
        if (end < 0) throw new InvalidDataException("Unterminated string.");
        var s = Encoding.UTF8.GetString(d, pos, end - pos);
        pos = end + 1;
        return s;
    }

    public static byte[] Write(VdfObject root)
    {
        using var ms = new MemoryStream();
        WriteObject(ms, root);
        return ms.ToArray();
    }

    static void WriteObject(MemoryStream ms, VdfObject obj)
    {
        foreach (var (key, value) in obj)
        {
            ms.WriteByte(value switch { VdfObject => TObject, string => TString, int => TInt,
                _ => throw new InvalidDataException($"Unsupported value type {value.GetType()}.") });
            WriteString(ms, key);
            switch (value)
            {
                case VdfObject o: WriteObject(ms, o); break;
                case string s: WriteString(ms, s); break;
                case int i: ms.Write(BitConverter.GetBytes(i)); break;
            }
        }
        ms.WriteByte(TEnd);
    }

    static void WriteString(MemoryStream ms, string s)
    {
        ms.Write(Encoding.UTF8.GetBytes(s));
        ms.WriteByte(0);
    }
}
