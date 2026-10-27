using System.Text.Json;

namespace McServerTray;

/// A Minecraft game directory from some launcher: where its servers.dat and resource packs live,
/// and which client jar / asset store its look comes from.
sealed record McProfile(
    string Id,
    string Launcher,
    string Name,
    string GameDir,
    string? ClientJar,
    string? AssetsDir,
    IReadOnlyList<string> ResourcePacks,
    long LastLaunch,
    string? PrismInstance)
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

    /// Every profile we can find, from all supported launchers.
    public static List<McProfile> Discover() => [.. PrismInstances()];

    public static string PrismRoot()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\persist\prismlauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismLauncher"),
        ];
        return candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "instances"))) ?? candidates[0];
    }

    static IEnumerable<McProfile> PrismInstances()
    {
        string root = PrismRoot();
        string dir = Path.Combine(root, "instances");
        if (!Directory.Exists(dir)) yield break;

        string assets = Path.Combine(root, "assets");
        foreach (string instance in Directory.EnumerateDirectories(dir))
        {
            string? gameDir = new[] { "minecraft", ".minecraft" }.Select(s => Path.Combine(instance, s)).FirstOrDefault(Directory.Exists);
            if (gameDir == null) continue;

            var cfg = ReadKeyValues(Path.Combine(instance, "instance.cfg"), '=');
            long.TryParse(cfg.GetValueOrDefault("lastLaunchTime"), out long lastLaunch);
            string id = Path.GetFileName(instance);
            string name = cfg.GetValueOrDefault("name") ?? id;
            yield return new McProfile("prism:" + id, "Prism Launcher", name, gameDir,
                PrismJar(root, ReadPrismVersion(instance)), Directory.Exists(assets) ? assets : null,
                ReadResourcePacks(gameDir), lastLaunch, id);
        }
    }

    /// The instance's client jar from Prism's library cache, else the newest one there.
    static string? PrismJar(string root, string? version)
    {
        string dir = Path.Combine(root, @"libraries\com\mojang\minecraft");
        if (!Directory.Exists(dir)) return null;
        if (version != null)
        {
            string exact = Path.Combine(dir, version, $"minecraft-{version}-client.jar");
            if (File.Exists(exact)) return exact;
        }
        return Directory.EnumerateFiles(dir, "minecraft-*-client.jar", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
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
