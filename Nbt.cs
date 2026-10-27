using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace McServerTray;

/// Minimal NBT reader, just enough to pull the server list out of servers.dat.
static class Nbt
{
    public static object? ReadFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }

        int pos = 0;
        byte type = data[pos++];
        ReadString(data, ref pos);
        return ReadPayload(data, ref pos, type);
    }

    static string ReadString(byte[] d, ref int p)
    {
        int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p));
        p += 2;
        string s = Encoding.UTF8.GetString(d, p, len);
        p += len;
        return s;
    }

    static int ReadInt(byte[] d, ref int p)
    {
        int v = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    static object? ReadPayload(byte[] d, ref int p, byte type)
    {
        switch (type)
        {
            case 1: return (sbyte)d[p++];
            case 2: { var v = BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(p)); p += 2; return v; }
            case 3: return ReadInt(d, ref p);
            case 4: { var v = BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(p)); p += 8; return v; }
            case 5: p += 4; return null;
            case 6: p += 8; return null;
            case 7: p += ReadInt(d, ref p); return null;
            case 8: return ReadString(d, ref p);
            case 9:
            {
                byte elem = d[p++];
                int n = ReadInt(d, ref p);
                var list = new List<object?>(n);
                for (int i = 0; i < n; i++) list.Add(ReadPayload(d, ref p, elem));
                return list;
            }
            case 10:
            {
                var dict = new Dictionary<string, object?>();
                while (true)
                {
                    byte t = d[p++];
                    if (t == 0) return dict;
                    string key = ReadString(d, ref p);
                    dict[key] = ReadPayload(d, ref p, t);
                }
            }
            case 11: p += 4 * ReadInt(d, ref p); return null;
            case 12: p += 8 * ReadInt(d, ref p); return null;
            default: throw new InvalidDataException($"Unknown NBT tag {type}");
        }
    }
}
