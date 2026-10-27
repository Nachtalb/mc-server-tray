using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace McServerTray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        int snap = Array.IndexOf(args, "--snapshot");
        if (snap >= 0 && snap + 1 < args.Length)
        {
            // Debug aid: render the server list to a PNG once the first ping is in, then exit.
            using var shot = new TrayApp(showTray: false);
            shot.SnapshotAsync(args[snap + 1]).ContinueWith(_ => Application.Exit(), TaskScheduler.FromCurrentSynchronizationContext());
            Application.Run();
            return;
        }

        // Launching the exe again just opens the server list of the running instance.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\McServerTray.Show");
        using var mutex = new Mutex(true, @"Local\McServerTray", out bool first);
        if (!first)
        {
            show.Set();
            return;
        }

        using var app = new TrayApp();
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => app.ShowPopup(), null, Timeout.Infinite, false);
        if (args.Contains("--show")) app.ShowPopup();
        Application.Run();
    }
}

record ServerEntry(string Name, string Address, string? IconBase64);

sealed class TrayApp : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "McServerTray";

    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly string _prismRoot = PrismRoot();
    readonly ServerListForm _popup;
    McAssets _assets;
    McProfile? _profile;
    DateTime _optionsStamp;
    Icon _iconIdle;
    Icon _iconActive;

    List<ServerEntry> _servers = [];
    readonly Dictionary<string, ServerStatus> _status = new();
    readonly Dictionary<string, Bitmap?> _iconCache = new();
    Task? _poll;

    public async Task SnapshotAsync(string path)
    {
        await PollAsync();
        _popup.Relayout();
        using var bmp = new Bitmap(_popup.Width, _popup.Height);
        _popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, _popup.Size));
        bmp.Save(path, ImageFormat.Png);
    }

    public TrayApp(bool showTray = true)
    {
        (_profile, _assets) = LoadLook();
        (_iconIdle, _iconActive) = MakeTrayIcons(_assets.Sprite("block/grass_block_side.png")?.Image);
        _servers = LoadServers(_prismRoot);

        _popup = new ServerListForm(_assets, Views, PollAsync);
        // ShowPopup marshals onto the popup, which needs a handle even while it's hidden.
        _ = _popup.Handle;

        _tray.Icon = _iconIdle;
        _tray.Text = "Minecraft servers";
        _tray.ContextMenuStrip = _menu;
        _tray.Visible = showTray;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _popup.Toggle();
        };
        _tray.BalloonTipClicked += (_, _) => _popup.Toggle();
        _menu.Opening += (_, _) => BuildMenu();

        _timer.Interval = (int)PollInterval.TotalMilliseconds;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    /// Textures come from the Prism instance played last: its game version and enabled resource packs.
    (McProfile? profile, McAssets assets) LoadLook()
    {
        var profile = McProfile.PrismInstances(_prismRoot).MaxBy(p => p.LastLaunch);
        _optionsStamp = profile != null && File.Exists(profile.OptionsFile) ? File.GetLastWriteTimeUtc(profile.OptionsFile) : default;
        return (profile, McAssets.Load(_prismRoot, profile));
    }

    /// Picks up resource pack changes (or a different instance being played) without a restart.
    void ReloadLookIfChanged()
    {
        var latest = McProfile.PrismInstances(_prismRoot).MaxBy(p => p.LastLaunch);
        var stamp = latest != null && File.Exists(latest.OptionsFile) ? File.GetLastWriteTimeUtc(latest.OptionsFile) : default;
        if (latest?.GameDir == _profile?.GameDir && stamp == _optionsStamp) return;

        var old = _assets;
        (_profile, _assets) = LoadLook();
        _popup.SetAssets(_assets);
        (_iconIdle, _iconActive) = MakeTrayIcons(_assets.Sprite("block/grass_block_side.png")?.Image);
        old.Dispose();
    }

    public void ShowPopup() => _popup.BeginInvoke(() =>
    {
        if (!_popup.Visible) _popup.Toggle();
    });

    IReadOnlyList<ServerView> Views() =>
        _servers.Select(s =>
        {
            var status = _status.GetValueOrDefault(s.Address);
            return new ServerView(s, status, DecodeIcon(status?.FaviconBase64 ?? s.IconBase64));
        }).ToList();

    Bitmap? DecodeIcon(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        if (_iconCache.TryGetValue(base64, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            int comma = base64.IndexOf(',');
            byte[] png = Convert.FromBase64String(base64.StartsWith("data:") ? base64[(comma + 1)..] : base64);
            using var img = Image.FromStream(new MemoryStream(png));
            bmp = new Bitmap(img);
        }
        catch
        {
            // Broken favicon; show the default icon instead.
        }
        return _iconCache[base64] = bmp;
    }

    static string PrismRoot()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\persist\prismlauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismLauncher"),
        ];
        return candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "instances"))) ?? candidates[0];
    }

    /// Union of the server lists of every Prism instance, de-duplicated by address.
    static List<ServerEntry> LoadServers(string prismRoot)
    {
        var result = new List<ServerEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.Combine(prismRoot, "instances");
        if (!Directory.Exists(dir)) return result;

        foreach (string instance in Directory.EnumerateDirectories(dir))
        {
            foreach (string sub in new[] { "minecraft", ".minecraft" })
            {
                string file = Path.Combine(instance, sub, "servers.dat");
                if (!File.Exists(file)) continue;
                try
                {
                    if (Nbt.ReadFile(file) is not Dictionary<string, object?> root ||
                        !root.TryGetValue("servers", out var list) || list is not List<object?> servers)
                        continue;
                    foreach (var s in servers.OfType<Dictionary<string, object?>>())
                    {
                        if (s.GetValueOrDefault("ip") is not string ip || string.IsNullOrWhiteSpace(ip)) continue;
                        if (!seen.Add(ip)) continue;
                        string name = s.GetValueOrDefault("name") as string ?? ip;
                        result.Add(new ServerEntry(name, ip, s.GetValueOrDefault("icon") as string));
                    }
                }
                catch
                {
                    // Prism may be writing the file right now; we'll pick it up next poll.
                }
            }
        }
        return result;
    }

    /// Callers that arrive while a poll is running share it instead of starting another.
    Task PollAsync() => _poll is { IsCompleted: false } ? _poll : _poll = PollOnceAsync();

    async Task PollOnceAsync()
    {
        ReloadLookIfChanged();
        var servers = LoadServers(_prismRoot);
        bool listChanged = !servers.Select(s => s.Address).SequenceEqual(_servers.Select(s => s.Address));
        _servers = servers;
        if (listChanged && _popup.Visible) _popup.Relayout();

        var results = await Task.WhenAll(_servers.Select(s => ServerPing.QueryAsync(s.Address, PingTimeout)));

        var joined = new List<string>();
        for (int i = 0; i < _servers.Count; i++)
        {
            var server = _servers[i];
            var now = results[i];
            _status.TryGetValue(server.Address, out var before);
            joined.AddRange(DescribeJoins(server, before, now));
            _status[server.Address] = now;
        }

        if (joined.Count > 0)
            _tray.ShowBalloonTip(5000, "Someone's online", string.Join("\n", joined), ToolTipIcon.None);

        UpdateTray();
        _popup.Invalidate();
    }

    static IEnumerable<string> DescribeJoins(ServerEntry server, ServerStatus? before, ServerStatus now)
    {
        if (!now.Online || now.PlayersOnline == 0) yield break;

        int prevCount = before is { Online: true } ? before.PlayersOnline : 0;
        var prevNames = before is { Online: true } ? before.PlayerNames.ToHashSet() : [];
        var newNames = now.PlayerNames.Where(n => !prevNames.Contains(n)).ToList();

        if (newNames.Count > 0)
            yield return $"{string.Join(", ", newNames)} joined {server.Name}";
        else if (now.PlayersOnline > prevCount)
            // Server hides its player list; all we can report is the count.
            yield return $"{server.Name}: {now.PlayersOnline} player(s) online";
    }

    void UpdateTray()
    {
        int total = _servers.Sum(s => _status.TryGetValue(s.Address, out var st) && st.Online ? st.PlayersOnline : 0);
        _tray.Icon = total > 0 ? _iconActive : _iconIdle;

        var lines = _servers.Select(s =>
            _status.TryGetValue(s.Address, out var st) && st.Online
                ? $"{s.Name}: {st.PlayersOnline}/{st.PlayersMax}"
                : $"{s.Name}: offline");
        string text = string.Join("\n", lines);
        if (text.Length == 0) text = "No servers configured";
        // NotifyIcon tooltips are capped at 127 characters.
        _tray.Text = text.Length > 127 ? text[..124] + "..." : text;
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("Show servers", null, (_, _) => _popup.Toggle());
        _menu.Items.Add("Refresh now", null, async (_, _) => await PollAsync());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        startup.Click += (_, _) => SetStartup(!startup.Checked);
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Application.Exit());
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }

    /// The grass block side from the game (greyed out while nobody is on), or a drawn stand-in.
    static (Icon idle, Icon active) MakeTrayIcons(Bitmap? grass)
    {
        Icon Render(bool active)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (grass != null)
                {
                    using var attrs = new ImageAttributes();
                    if (!active)
                        attrs.SetColorMatrix(new ColorMatrix(
                        [
                            [0.3f, 0.3f, 0.3f, 0, 0],
                            [0.59f, 0.59f, 0.59f, 0, 0],
                            [0.11f, 0.11f, 0.11f, 0, 0],
                            [0, 0, 0, 1, 0],
                            [0, 0, 0, 0, 1],
                        ]));
                    g.DrawImage(grass, new Rectangle(0, 0, 32, 32), 0, 0, grass.Width, grass.Height, GraphicsUnit.Pixel, attrs);
                }
                else
                {
                    using var dirt = new SolidBrush(Color.FromArgb(121, 85, 58));
                    using var top = new SolidBrush(active ? Color.FromArgb(76, 175, 80) : Color.FromArgb(120, 120, 120));
                    g.FillRectangle(dirt, 2, 2, 28, 28);
                    g.FillRectangle(top, 2, 2, 28, 10);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        return (Render(false), Render(true));
    }

    public void Dispose()
    {
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _popup.Dispose();
    }
}
