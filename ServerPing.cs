using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace McServerTray;

record ServerStatus(
    bool Online,
    string? Version,
    int PlayersOnline,
    int PlayersMax,
    IReadOnlyList<string> PlayerNames,
    string? DescriptionJson,
    string? FaviconBase64,
    long? PingMs,
    string? Error,
    int? Protocol = null);

/// Minecraft Java "Server List Ping" (the same query the multiplayer screen uses).
static class ServerPing
{
    /// clientProtocol is sent in the handshake like the game does (some servers answer per version).
    public static async Task<ServerStatus> QueryAsync(string address, TimeSpan timeout, int? clientProtocol = null)
    {
        var (host, port) = ParseAddress(address);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, cts.Token);
            var stream = client.GetStream();

            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, clientProtocol ?? 767);
            byte[] hostBytes = Encoding.UTF8.GetBytes(host);
            WriteVarInt(handshake, hostBytes.Length);
            handshake.Write(hostBytes);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)port);
            WriteVarInt(handshake, 1);

            var packet = new MemoryStream();
            WriteVarInt(packet, (int)handshake.Length);
            handshake.WriteTo(packet);
            packet.Write([0x01, 0x00]);
            await stream.WriteAsync(packet.ToArray(), cts.Token);

            await ReadVarIntAsync(stream, cts.Token);
            await ReadVarIntAsync(stream, cts.Token);
            int len = await ReadVarIntAsync(stream, cts.Token);
            byte[] buf = new byte[len];
            await stream.ReadExactlyAsync(buf, cts.Token);

            long? ping = await MeasurePingAsync(stream, cts.Token);

            using var doc = JsonDocument.Parse(buf);
            var root = doc.RootElement;
            string? version = root.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var vn) ? vn.GetString() : null;
            int online = 0, max = 0;
            var names = new List<string>();
            if (root.TryGetProperty("players", out var players))
            {
                if (players.TryGetProperty("online", out var o)) online = o.GetInt32();
                if (players.TryGetProperty("max", out var m)) max = m.GetInt32();
                if (players.TryGetProperty("sample", out var sample) && sample.ValueKind == JsonValueKind.Array)
                    foreach (var p in sample.EnumerateArray())
                        if (p.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                            names.Add(name);
            }
            string? description = root.TryGetProperty("description", out var d) ? d.GetRawText() : null;
            string? favicon = root.TryGetProperty("favicon", out var f) ? f.GetString() : null;
            int? protocol = root.TryGetProperty("version", out var pv) && pv.TryGetProperty("protocol", out var pp) && pp.TryGetInt32(out int pn) ? pn : null;
            return new ServerStatus(true, version, online, max, names, description, favicon, ping, null, protocol);
        }
        catch (OperationCanceledException)
        {
            return Offline("timed out");
        }
        catch (Exception e)
        {
            return Offline(e is SocketException se ? se.SocketErrorCode.ToString() : e.Message);
        }
    }

    static ServerStatus Offline(string error) => new(false, null, 0, 0, [], null, null, null, error);

    /// Round trip of the status-phase ping packet, like the latency the game shows.
    static async Task<long?> MeasurePingAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            byte[] packet = new byte[10];
            packet[0] = 9;
            packet[1] = 0x01;
            BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(2), Environment.TickCount64);
            var sw = Stopwatch.StartNew();
            await stream.WriteAsync(packet, ct);
            int len = await ReadVarIntAsync(stream, ct);
            await stream.ReadExactlyAsync(new byte[len], ct);
            return sw.ElapsedMilliseconds;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    static (string host, int port) ParseAddress(string address)
    {
        int colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out int port))
            return (address[..colon], port);
        return (address, 25565);
    }

    static void WriteVarInt(Stream s, int value)
    {
        uint v = (uint)value;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            s.WriteByte(b);
        } while (v != 0);
    }

    static async Task<int> ReadVarIntAsync(Stream s, CancellationToken ct)
    {
        int result = 0, shift = 0;
        byte[] one = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(one, ct);
            result |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("VarInt too long");
        }
    }
}
