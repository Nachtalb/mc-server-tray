using System.Text.Json;

namespace McServerTray;

/// A Minecraft game directory whose look (version jar + enabled resource packs) we borrow.
sealed record McProfile(string Name, string GameDir, string? McVersion, IReadOnlyList<string> ResourcePacks, long LastLaunch)
{
    public string OptionsFile => Path.Combine(GameDir, "options.txt");

    /// Changes whenever the look would: options.txt (pack list/order) or any enabled pack file being replaced.
    public string Signature()
    {
        var parts = new List<string> { GameDir, Stamp(OptionsFile) };
        foreach (string pack in ResourcePacks.Where(p => p.StartsWith("file/")))
            parts.Add(Stamp(Path.Combine(GameDir, "resourcepacks", pack["file/".Length..])));
        return string.Join('|', parts);

        static string Stamp(string path) =>
            File.Exists(path) ? $"{path}@{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}" :
            Directory.Exists(path) ? $"{path}@{Directory.GetLastWriteTimeUtc(path).Ticks}" :
            $"{path}@missing";
    }

    /// Prism instances; the one played last is what we take the textures from.
    public static List<McProfile> PrismInstances(string prismRoot)
    {
        var result = new List<McProfile>();
        string dir = Path.Combine(prismRoot, "instances");
        if (!Directory.Exists(dir)) return result;

        foreach (string instance in Directory.EnumerateDirectories(dir))
        {
            string? gameDir = new[] { "minecraft", ".minecraft" }.Select(s => Path.Combine(instance, s)).FirstOrDefault(Directory.Exists);
            if (gameDir == null) continue;

            var cfg = ReadKeyValues(Path.Combine(instance, "instance.cfg"), '=');
            long.TryParse(cfg.GetValueOrDefault("lastLaunchTime"), out long lastLaunch);
            string name = cfg.GetValueOrDefault("name") ?? Path.GetFileName(instance);
            result.Add(new McProfile(name, gameDir, ReadPrismVersion(instance), ReadResourcePacks(gameDir), lastLaunch));
        }
        return result;
    }

    static string? ReadPrismVersion(string instance)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(instance, "mmc-pack.json")));
            foreach (var c in doc.RootElement.GetProperty("components").EnumerateArray())
                if (c.GetProperty("uid").GetString() == "net.minecraft")
                    return c.GetProperty("version").GetString();
        }
        catch
        {
            // Not a Prism instance or a broken pack file; fall back to the newest jar.
        }
        return null;
    }

    /// The `resourcePacks` list from options.txt, lowest priority first (as the game stores it).
    static List<string> ReadResourcePacks(string gameDir)
    {
        var options = ReadKeyValues(Path.Combine(gameDir, "options.txt"), ':');
        if (options.GetValueOrDefault("resourcePacks") is not { } raw) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    static Dictionary<string, string> ReadKeyValues(string file, char separator)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(file)) return result;
        foreach (string line in File.ReadLines(file))
        {
            int i = line.IndexOf(separator);
            if (i > 0) result[line[..i]] = line[(i + 1)..];
        }
        return result;
    }
}
