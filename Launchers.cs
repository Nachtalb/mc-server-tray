using System.Diagnostics;
using Microsoft.Win32;

namespace McServerTray;

/// Finding, opening and picturing the supported launchers.
static class Launchers
{
    public const string Prism = "Prism Launcher";
    public const string Vanilla = "Minecraft Launcher";
    public const string Modrinth = "Modrinth App";

    const string VanillaPackage = "Microsoft.4297127D64EC6_8wekyb3d8bbwe";

    static readonly Dictionary<string, Bitmap?> IconCache = new();

    public static string? PrismExecutable()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            Path.Combine(home, @"scoop\apps\prismlauncher\current\prismlauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\PrismLauncher\prismlauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PrismLauncher\prismlauncher.exe"),
            Path.Combine(McProfile.PrismRoot(), "prismlauncher.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    static string? ModrinthExecutable() =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Modrinth App", "Modrinth App.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Modrinth App", "Modrinth App.exe"),
        }.FirstOrDefault(File.Exists);

    /// The Store launcher's install folder, from the per-user package repository.
    static string? VanillaPackageFolder()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        string? name = packages?.GetSubKeyNames().FirstOrDefault(n => n.StartsWith("Microsoft.4297127D64EC6_"));
        if (name == null) return null;
        using var key = packages!.OpenSubKey(name);
        return key?.GetValue("PackageRootFolder") as string;
    }

    public static bool IsInstalled(string launcher) => launcher switch
    {
        Prism => PrismExecutable() != null,
        Modrinth => ModrinthExecutable() != null,
        Vanilla => VanillaPackageFolder() != null,
        _ => false,
    };

    /// Opens the launcher itself (no profile, no server).
    public static bool Open(string launcher)
    {
        switch (launcher)
        {
            case Prism when PrismExecutable() is { } prism:
                Process.Start(prism);
                return true;
            case Modrinth when ModrinthExecutable() is { } modrinth:
                Process.Start(modrinth);
                return true;
            case Vanilla when VanillaPackageFolder() != null:
                Process.Start("explorer.exe", $@"shell:AppsFolder\{VanillaPackage}!Minecraft");
                return true;
            default:
                return false;
        }
    }

    /// The launcher's own app icon: from its exe, or the Store package's unplated logo.
    public static Bitmap? Icon(string launcher)
    {
        if (IconCache.TryGetValue(launcher, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            switch (launcher)
            {
                case Prism when PrismExecutable() is { } exe:
                    bmp = System.Drawing.Icon.ExtractIcon(exe, 0, 64)?.ToBitmap();
                    break;
                case Modrinth when ModrinthExecutable() is { } exe:
                    bmp = System.Drawing.Icon.ExtractIcon(exe, 0, 64)?.ToBitmap();
                    break;
                case Vanilla when VanillaPackageFolder() is { } folder:
                    string logo = new[] { "SmallLogo.altform-unplated_targetsize-256.png", "SmallLogo.scale-200.png", "SmallLogo.png" }
                        .Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists) ?? "";
                    if (logo != "")
                        using (var img = Image.FromFile(logo))
                            bmp = new Bitmap(img);
                    break;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
        {
            // No icon; the menu entry just has none.
        }
        return IconCache[launcher] = bmp;
    }
}
