using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;

namespace McServerTray;

/// How a GUI sprite scales, from its .png.mcmeta. Sizes are in GUI pixels, whatever the texture resolution.
sealed record NineSlice(int Width, int Height, int Left, int Top, int Right, int Bottom, bool StretchInner);

sealed record Sprite(Bitmap Image, NineSlice? Slice);

/// One place assets can come from: a resource pack (zip or folder) or the client jar.
sealed class AssetSource : IDisposable
{
    readonly ZipArchive? _zip;
    readonly string? _dir;

    AssetSource(ZipArchive? zip, string? dir)
    {
        _zip = zip;
        _dir = dir;
    }

    public static AssetSource? Open(string path)
    {
        try
        {
            if (Directory.Exists(path)) return new AssetSource(null, path);
            if (File.Exists(path)) return new AssetSource(ZipFile.OpenRead(path), null);
        }
        catch
        {
            // Unreadable pack; skip it like the game would.
        }
        return null;
    }

    public byte[]? Read(string path)
    {
        try
        {
            if (_dir != null)
            {
                string file = Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            var entry = _zip!.GetEntry(path);
            if (entry == null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _zip?.Dispose();
}

/// Textures, sounds and the bitmap font, resolved like the game does: enabled resource packs first
/// (highest priority on top), then the client jar Prism downloaded. Nothing from Mojang is shipped
/// with this app; without a jar we fall back to plain GDI drawing.
sealed class McAssets : IDisposable
{
    readonly List<AssetSource> _sources;
    readonly string? _assetsDir;
    readonly Dictionary<string, Sprite?> _sprites = new();
    readonly Dictionary<string, byte[]?> _sounds = new();

    public McFont Font { get; }
    public bool HasGameAssets => _sources.Count > 0;

    McAssets(List<AssetSource> sources, string? assetsDir)
    {
        _sources = sources;
        _assetsDir = assetsDir;
        Font = LoadFont();
    }

    public static McAssets Load(McProfile? profile)
    {
        var sources = new List<AssetSource>();
        if (profile != null)
        {
            // options.txt lists packs lowest priority first; "vanilla" is the jar itself.
            foreach (string pack in profile.ResourcePacks.Reverse())
            {
                if (!pack.StartsWith("file/")) continue;
                if (AssetSource.Open(Path.Combine(profile.GameDir, "resourcepacks", pack["file/".Length..])) is { } src)
                    sources.Add(src);
            }
        }
        if (profile?.ClientJar is { } jar && AssetSource.Open(jar) is { } jarSource)
            sources.Add(jarSource);

        return new McAssets(sources, profile?.AssetsDir);
    }

    /// Reads assets/minecraft/<path> from the highest-priority source that has it.
    (byte[] data, AssetSource source)? Find(string path)
    {
        foreach (var src in _sources)
            if (src.Read("assets/minecraft/" + path) is { } data)
                return (data, src);
        return null;
    }

    /// A texture by its path under textures/, e.g. "gui/sprites/widget/button.png".
    public Sprite? Sprite(string path)
    {
        if (_sprites.TryGetValue(path, out var cached)) return cached;
        Sprite? sprite = null;
        if (Find("textures/" + path) is ({ } data, { } src) && DecodePng(data) is { } bmp)
        {
            // The .mcmeta must come from the same pack as the image.
            byte[]? meta = src.Read("assets/minecraft/textures/" + path + ".mcmeta");
            sprite = new Sprite(bmp, meta != null ? ParseNineSlice(meta) : null);
        }
        return _sprites[path] = sprite;
    }

    static NineSlice? ParseNineSlice(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("gui", out var gui) || !gui.TryGetProperty("scaling", out var s) ||
                s.GetProperty("type").GetString() != "nine_slice")
                return null;
            int w = s.GetProperty("width").GetInt32(), h = s.GetProperty("height").GetInt32();
            var b = s.GetProperty("border");
            bool stretch = s.TryGetProperty("stretch_inner", out var si) && si.ValueKind == JsonValueKind.True;
            if (b.ValueKind == JsonValueKind.Number)
            {
                int all = b.GetInt32();
                return new NineSlice(w, h, all, all, all, all, stretch);
            }
            return new NineSlice(w, h, b.GetProperty("left").GetInt32(), b.GetProperty("top").GetInt32(),
                b.GetProperty("right").GetInt32(), b.GetProperty("bottom").GetInt32(), stretch);
        }
        catch
        {
            return null;
        }
    }

    /// A sound event's .ogg (e.g. "random/click"): from a resource pack, else Prism's asset store.
    public byte[]? Sound(string name)
    {
        if (_sounds.TryGetValue(name, out var cached)) return cached;
        string path = $"sounds/{name}.ogg";
        byte[]? data = Find(path)?.data ?? FromAssetIndex("minecraft/" + path);
        return _sounds[name] = data;
    }

    byte[]? FromAssetIndex(string name)
    {
        if (_assetsDir == null) return null;
        string indexes = Path.Combine(_assetsDir, "indexes");
        if (!Directory.Exists(indexes)) return null;
        foreach (string index in Directory.EnumerateFiles(indexes, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(index));
                if (!doc.RootElement.GetProperty("objects").TryGetProperty(name, out var obj)) continue;
                string hash = obj.GetProperty("hash").GetString()!;
                string file = Path.Combine(_assetsDir, "objects", hash[..2], hash);
                if (File.Exists(file)) return File.ReadAllBytes(file);
            }
            catch
            {
                // Try the next index.
            }
        }
        return null;
    }

    McFont LoadFont()
    {
        if (Find("font/include/default.json") is not ({ } json, _)) return McFont.Fallback();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var providers = new List<McFont.BitmapProvider>();
            foreach (var p in doc.RootElement.GetProperty("providers").EnumerateArray())
            {
                if (p.GetProperty("type").GetString() != "bitmap") continue;
                string file = p.GetProperty("file").GetString()!.Replace("minecraft:", "");
                if (Find("textures/" + file) is not ({ } png, _) || DecodePng(png) is not { } bmp) continue;
                providers.Add(new McFont.BitmapProvider(
                    bmp,
                    p.TryGetProperty("height", out var h) ? h.GetInt32() : 8,
                    p.GetProperty("ascent").GetInt32(),
                    p.GetProperty("chars").EnumerateArray().Select(c => c.GetString()!).ToArray()));
            }
            return new McFont(providers);
        }
        catch
        {
            return McFont.Fallback();
        }
    }

    static Bitmap? DecodePng(byte[] data)
    {
        try
        {
            using var img = Image.FromStream(new MemoryStream(data));
            var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
    }
}
