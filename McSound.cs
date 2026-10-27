using System.Media;
using NVorbis;

namespace McServerTray;

/// Plays the game's .ogg sounds by decoding them once to an in-memory WAV.
sealed class McSound : IDisposable
{
    readonly SoundPlayer? _player;

    public McSound(byte[]? ogg, float volume)
    {
        if (ogg == null) return;
        try
        {
            _player = new SoundPlayer(new MemoryStream(ToWav(ogg, volume)));
            _player.Load();
        }
        catch
        {
            _player = null;
        }
    }

    public void Play() => _player?.Play();

    static byte[] ToWav(byte[] ogg, float volume)
    {
        using var vorbis = new VorbisReader(new MemoryStream(ogg), true);
        int channels = vorbis.Channels, rate = vorbis.SampleRate;
        var samples = new List<short>();
        var buf = new float[rate * channels];
        int read;
        while ((read = vorbis.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++)
                samples.Add((short)Math.Clamp(buf[i] * volume * short.MaxValue, short.MinValue, short.MaxValue));

        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = samples.Count * 2;
        w.Write("RIFF"u8);
        w.Write(36 + bytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(bytes);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose() => _player?.Dispose();
}
