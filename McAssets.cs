using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;

namespace McServerTray;

/// Textures and the bitmap font, read straight out of the Minecraft client jar Prism has downloaded.
/// Nothing from Mojang is shipped with this app; without a jar we fall back to plain GDI drawing.
sealed class McAssets
{
    readonly Dictionary<string, Bitmap> _textures = new();
    public McFont Font { get; }
    public bool FromJar { get; }

    McAssets(McFont font, bool fromJar)
    {
        Font = font;
        FromJar = fromJar;
    }

    public Bitmap? Texture(string path) => _textures.GetValueOrDefault(path);

    static readonly string[] TexturePaths =
    [
        "gui/menu_background.png",
        "gui/menu_list_background.png",
        "gui/header_separator.png",
        "gui/footer_separator.png",
        "gui/sprites/widget/button.png",
        "gui/sprites/widget/button_highlighted.png",
        "gui/sprites/tooltip/background.png",
        "gui/sprites/tooltip/frame.png",
        "gui/sprites/server_list/ping_1.png",
        "gui/sprites/server_list/ping_2.png",
        "gui/sprites/server_list/ping_3.png",
        "gui/sprites/server_list/ping_4.png",
        "gui/sprites/server_list/ping_5.png",
        "gui/sprites/server_list/pinging_1.png",
        "gui/sprites/server_list/pinging_2.png",
        "gui/sprites/server_list/pinging_3.png",
        "gui/sprites/server_list/pinging_4.png",
        "gui/sprites/server_list/pinging_5.png",
        "gui/sprites/server_list/unreachable.png",
        "misc/unknown_server.png",
        "block/grass_block_side.png",
    ];

    public static McAssets Load(string prismRoot)
    {
        string? jar = FindClientJar(prismRoot);
        if (jar == null) return new McAssets(McFont.Fallback(), false);

        try
        {
            using var zip = ZipFile.OpenRead(jar);
            var assets = new McAssets(LoadFont(zip), true);
            foreach (string path in TexturePaths)
                if (ReadBitmap(zip, "assets/minecraft/textures/" + path) is { } bmp)
                    assets._textures[path] = bmp;
            return assets;
        }
        catch
        {
            return new McAssets(McFont.Fallback(), false);
        }
    }

    /// Newest minecraft-*-client.jar in Prism's library cache.
    static string? FindClientJar(string prismRoot)
    {
        string dir = Path.Combine(prismRoot, @"libraries\com\mojang\minecraft");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "minecraft-*-client.jar", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    static McFont LoadFont(ZipArchive zip)
    {
        var entry = zip.GetEntry("assets/minecraft/font/include/default.json");
        if (entry == null) return McFont.Fallback();

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var providers = new List<McFont.BitmapProvider>();
        foreach (var p in doc.RootElement.GetProperty("providers").EnumerateArray())
        {
            if (p.GetProperty("type").GetString() != "bitmap") continue;
            string file = p.GetProperty("file").GetString()!.Replace("minecraft:", "");
            var bmp = ReadBitmap(zip, "assets/minecraft/textures/" + file);
            if (bmp == null) continue;
            providers.Add(new McFont.BitmapProvider(
                bmp,
                p.TryGetProperty("height", out var h) ? h.GetInt32() : 8,
                p.GetProperty("ascent").GetInt32(),
                p.GetProperty("chars").EnumerateArray().Select(c => c.GetString()!).ToArray()));
        }
        return new McFont(providers);
    }

    static Bitmap? ReadBitmap(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry == null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        using var img = Image.FromStream(ms);
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
        return bmp;
    }
}
