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
    public static List<McProfile> Discover() => [.. PrismInstances(), .. VanillaProfiles()];

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

    /// Profiles of the official Minecraft Launcher (launcher_profiles.json in .minecraft).
    static IEnumerable<McProfile> VanillaProfiles()
    {
        string mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        string file = Path.Combine(mc, "launcher_profiles.json");
        if (!File.Exists(file)) yield break;

        List<(string key, JsonElement p)> profiles;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            profiles = doc.RootElement.GetProperty("profiles").EnumerateObject().Select(p => (p.Name, p.Value.Clone())).ToList();
        }
        catch
        {
            yield break;
        }

        string assets = Path.Combine(mc, "assets");
        foreach (var (key, p) in profiles)
        {
            string type = p.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            string? name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name))
                name = type switch { "latest-release" => "Latest release", "latest-snapshot" => "Latest snapshot", _ => key };
            string gameDir = p.TryGetProperty("gameDir", out var gd) && gd.GetString() is { Length: > 0 } dir ? dir : mc;
            if (!Directory.Exists(gameDir)) continue;

            long lastUsed = p.TryGetProperty("lastUsed", out var lu) && lu.TryGetDateTimeOffset(out var when) ? when.ToUnixTimeMilliseconds() : 0;
            string? versionId = p.TryGetProperty("lastVersionId", out var v) ? v.GetString() : null;
            yield return new McProfile("vanilla:" + key, "Minecraft Launcher", name, gameDir,
                VanillaJar(mc, versionId), Directory.Exists(assets) ? assets : null,
                ReadResourcePacks(gameDir), lastUsed, null);
        }
    }

    /// The jar for a launcher version id, following "latest-*" and loader inheritsFrom chains to the vanilla jar.
    static string? VanillaJar(string mc, string? versionId)
    {
        string versions = Path.Combine(mc, "versions");
        if (!Directory.Exists(versions)) return null;

        if (versionId is "latest-release" or "latest-snapshot")
        {
            string wanted = versionId == "latest-release" ? "release" : "snapshot";
            versionId = Directory.EnumerateDirectories(versions)
                .Select(d => (id: Path.GetFileName(d), json: ReadVersionJson(versions, Path.GetFileName(d))))
                .Where(x => x.json is { } j && j.type == wanted && j.inheritsFrom == null && File.Exists(Path.Combine(versions, x.id, x.id + ".jar")))
                .OrderByDescending(x => x.json!.Value.releaseTime)
                .Select(x => x.id)
                .FirstOrDefault();
        }

        for (int depth = 0; versionId != null && depth < 5; depth++)
        {
            var json = ReadVersionJson(versions, versionId);
            if (json?.inheritsFrom is { } parent)
            {
                versionId = parent;
                continue;
            }
            string jar = Path.Combine(versions, versionId, versionId + ".jar");
            return File.Exists(jar) ? jar : null;
        }
        return null;
    }

    static (string? type, string? inheritsFrom, string releaseTime)? ReadVersionJson(string versions, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(versions, id, id + ".json")));
            var r = doc.RootElement;
            return (r.TryGetProperty("type", out var t) ? t.GetString() : null,
                r.TryGetProperty("inheritsFrom", out var i) ? i.GetString() : null,
                r.TryGetProperty("releaseTime", out var rt) ? rt.GetString() ?? "" : "");
        }
        catch
        {
            return null;
        }
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
