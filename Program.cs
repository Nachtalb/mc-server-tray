using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using static McServerTray.McDraw;

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
            // Optional: --mouse X,Y (GUI pixels) and --select ROW to check hover and selection,
            // or --menu to render the tray menu instead.
            string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            var mouse = Arg("--mouse")?.Split(',').Select(int.Parse).ToArray() is [int mx, int my] ? new Point(mx, my) : new Point(-1, -1);
            int select = int.TryParse(Arg("--select"), out int s) ? s : -1;
            using var shot = new TrayApp(showTray: false);
            shot.SnapshotAsync(args[snap + 1], mouse, select, args.Contains("--menu")).ContinueWith(_ => Application.Exit(), TaskScheduler.FromCurrentSynchronizationContext());
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

/// A saved server, with the profiles that have it in their list.
record ServerEntry(string Name, string Address, string? IconBase64, List<McProfile> Profiles);

sealed class TrayApp : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "McServerTray";

    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly ServerListForm _popup;
    McAssets _assets;
    McSound _click;
    readonly McMenuRenderer _menuRenderer;
    McProfile? _profile;
    string? _lookSignature;
    Icon _iconIdle;
    Icon _iconActive;

    List<ServerEntry> _servers = [];
    readonly Dictionary<string, ServerStatus> _status = new();
    readonly Dictionary<string, Bitmap?> _iconCache = new();
    readonly Dictionary<IconCube, Bitmap?> _cubeCache = new();
    Task? _poll;

    public async Task SnapshotAsync(string path, Point mouse, int selectedRow, bool menu)
    {
        await PollAsync();
        if (menu)
        {
            // The tray menu and its Profile submenu, opened off-screen and drawn side by side.
            BuildMenu();
            _menu.Show(-10000, -10000);
            var profiles = _menu.Items.OfType<ToolStripMenuItem>().First(i => i.HasDropDownItems);
            profiles.ShowDropDown();
            // --select N highlights the Nth submenu row (default: the first launcher heading).
            var rows = profiles.DropDown.Items.OfType<ToolStripMenuItem>().ToList();
            (selectedRow >= 0 && selectedRow < rows.Count ? rows[selectedRow] : rows.FirstOrDefault(i => i.Tag is MenuTag { Header: true }))?.Select();
            Application.DoEvents();
            var sub = profiles.DropDown;
            using var shot = new Bitmap(_menu.Width + sub.Width + 8, Math.Max(_menu.Height, sub.Height));
            using (var g = Graphics.FromImage(shot)) g.Clear(Color.FromArgb(32, 32, 32));
            _menu.DrawToBitmap(shot, new Rectangle(0, 0, _menu.Width, _menu.Height));
            sub.DrawToBitmap(shot, new Rectangle(_menu.Width + 8, 0, sub.Width, sub.Height));
            shot.Save(path, ImageFormat.Png);
            _menu.Close();
            return;
        }
        _popup.Relayout();
        _popup.Simulate(mouse, selectedRow);
        using var bmp = new Bitmap(_popup.Width, _popup.Height);
        _popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, _popup.Size));
        bmp.Save(path, ImageFormat.Png);
    }

    public TrayApp(bool showTray = true)
    {
        (_profile, _assets) = LoadLook();
        _click = new McSound(_assets.Sound("random/click"), 0.25f);
        _menuRenderer = new McMenuRenderer(() => _assets);
        (_iconIdle, _iconActive) = MakeTrayIcons(_assets.Sprite("block/grass_block_side.png")?.Image);
        _servers = LoadServers(_profile is { } p ? [p] : []);

        _popup = new ServerListForm(_assets, Views, PollAsync, Join);
        // ShowPopup marshals onto the popup, which needs a handle even while it's hidden.
        _ = _popup.Handle;

        _tray.Icon = _iconIdle;
        _tray.Text = "Minecraft servers";
        _tray.ContextMenuStrip = _menu;
        _tray.Visible = showTray;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        };
        _tray.BalloonTipClicked += (_, _) => TogglePopup();
        _menu.Opening += (_, _) => BuildMenu();

        _timer.Interval = (int)PollInterval.TotalMilliseconds;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    /// The profile picked in the tray menu, or the one played last when set to automatic.
    static McProfile? DefaultProfile(List<McProfile> profiles) =>
        profiles.FirstOrDefault(p => p.Id == Settings.Load().DefaultProfile) ?? profiles.MaxBy(p => p.LastLaunch);

    /// Textures come from the default profile: its game version and enabled resource packs.
    (McProfile? profile, McAssets assets) LoadLook()
    {
        var profile = DefaultProfile(McProfile.Discover());
        _lookSignature = profile?.Signature();
        return (profile, McAssets.Load(profile));
    }

    /// Picks up resource pack changes (or a different default profile) without a restart.
    void ReloadLookIfChanged()
    {
        var latest = DefaultProfile(McProfile.Discover());
        if (latest?.Signature() == _lookSignature) return;

        var old = _assets;
        (_profile, _assets) = LoadLook();
        _popup.SetAssets(_assets);
        _cubeCache.Clear();
        _click.Dispose();
        _click = new McSound(_assets.Sound("random/click"), 0.25f);
        (_iconIdle, _iconActive) = MakeTrayIcons(_assets.Sprite("block/grass_block_side.png")?.Image);
        UpdateTray();
        old.Dispose();
    }

    /// Opening the list checks the resource packs first, so pack changes show up immediately.
    void TogglePopup()
    {
        if (!_popup.Visible) ReloadLookIfChanged();
        _popup.Toggle();
    }

    /// Starts a Prism instance that has the server straight into it, preferring the profile we take the look from.
    /// Other launchers can't be told to join a server, so for those the address goes to the clipboard.
    void Join(ServerView view)
    {
        var profiles = view.Entry.Profiles;
        string? instance = (profiles.FirstOrDefault(p => p.Id == _profile?.Id && p.PrismInstance != null)
            ?? profiles.FirstOrDefault(p => p.PrismInstance != null))?.PrismInstance;
        string? exe = Launchers.PrismExecutable();
        if (instance == null || exe == null)
        {
            Clipboard.SetText(view.Entry.Address);
            _tray.ShowBalloonTip(5000, view.Entry.Name,
                $"Start {profiles[0].Name} in the {profiles[0].Launcher} and join - the address {view.Entry.Address} is on your clipboard.", ToolTipIcon.Info);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
        {
            ArgumentList = { "--launch", instance, "--server", view.Entry.Address },
            UseShellExecute = false,
        });
    }

    /// Starts the default profile without joining anything. Only Prism can start a given profile;
    /// the other launchers are just opened.
    void LaunchGame()
    {
        if (_profile == null) return;
        try
        {
            if (_profile.PrismInstance is { } instance && Launchers.PrismExecutable() is { } prism)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(prism) { ArgumentList = { "--launch", instance } });
            else
                OpenLauncher(_profile.Launcher);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _tray.ShowBalloonTip(5000, "Can't launch", e.Message, ToolTipIcon.Warning);
        }
    }

    void OpenLauncher(string launcher)
    {
        try
        {
            if (!Launchers.Open(launcher))
                _tray.ShowBalloonTip(5000, "Can't launch", $"Couldn't find the {launcher}.", ToolTipIcon.Warning);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _tray.ShowBalloonTip(5000, "Can't launch", e.Message, ToolTipIcon.Warning);
        }
    }

    public void ShowPopup() => _popup.BeginInvoke(() =>
    {
        if (!_popup.Visible) TogglePopup();
    });

    IReadOnlyList<ServerView> Views() =>
        _servers.Select(s =>
        {
            var status = _status.GetValueOrDefault(s.Address);
            return new ServerView(s, status, DecodeIcon(status?.FaviconBase64 ?? s.IconBase64));
        }).ToList();

    /// A profile's picture: its own image if it has one, else the first texture the active packs provide.
    Bitmap? ProfileImage(McProfile profile)
    {
        var icon = profile.Icon;
        if (icon.File != null && _iconCache.TryGetValue("file:" + icon.File, out var cached)) return cached;
        if (icon.File != null)
        {
            try
            {
                using var img = Image.FromFile(icon.File);
                return _iconCache["file:" + icon.File] = new Bitmap(img);
            }
            catch (Exception e) when (e is IOException or OutOfMemoryException)
            {
                _iconCache["file:" + icon.File] = null;
            }
        }
        if (DecodeIcon(icon.DataUri) is { } fromUri) return fromUri;
        if (icon.Flat != null && _assets.Sprite(icon.Flat) is { } flat) return FirstFrame(flat.Image);
        if (icon.Cube is not { } cube) return null;

        if (_cubeCache.TryGetValue(cube, out var built)) return built;
        Bitmap? Face(IReadOnlyList<string> candidates) =>
            candidates.Select(_assets.Sprite).OfType<Sprite>().Select(s => FirstFrame(s.Image)).FirstOrDefault();
        var (top, front, side) = (Face(cube.Top), Face(cube.Front), Face(cube.Side));
        return _cubeCache[cube] = top != null && front != null && side != null
            ? IsoCube(top, front, side, cube.TopTint, cube.SideTint)
            : null;
    }

    /// Animated textures are a vertical strip of square frames; keep the first one.
    static Bitmap FirstFrame(Bitmap img) =>
        img.Height <= img.Width ? img : img.Clone(new Rectangle(0, 0, img.Width, img.Width), img.PixelFormat);

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

    /// Union of the server lists of every profile, de-duplicated by address.
    static List<ServerEntry> LoadServers(IEnumerable<McProfile> profiles)
    {
        var result = new List<ServerEntry>();
        var seen = new Dictionary<string, ServerEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            string file = Path.Combine(profile.GameDir, "servers.dat");
            if (!File.Exists(file)) continue;
            try
            {
                if (Nbt.ReadFile(file) is not Dictionary<string, object?> root ||
                    !root.TryGetValue("servers", out var list) || list is not List<object?> servers)
                    continue;
                foreach (var s in servers.OfType<Dictionary<string, object?>>())
                {
                    if (s.GetValueOrDefault("ip") is not string ip || string.IsNullOrWhiteSpace(ip)) continue;
                    if (seen.TryGetValue(ip, out var existing))
                    {
                        if (!existing.Profiles.Contains(profile)) existing.Profiles.Add(profile);
                        continue;
                    }
                    string name = s.GetValueOrDefault("name") as string ?? ip;
                    var entry = new ServerEntry(name, ip, s.GetValueOrDefault("icon") as string, [profile]);
                    seen[ip] = entry;
                    result.Add(entry);
                }
            }
            catch
            {
                // The game may be writing the file right now; we'll pick it up next poll.
            }
        }
        return result;
    }

    /// Callers that arrive while a poll is running share it instead of starting another.
    Task PollAsync() => _poll is { IsCompleted: false } ? _poll : _poll = PollOnceAsync();

    async Task PollOnceAsync()
    {
        ReloadLookIfChanged();
        // Like the game, each profile has its own server list; we watch the default one's.
        var servers = LoadServers(_profile is { } p ? [p] : []);
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
        if (_profile != null)
            _menu.Items.Add(new ToolStripMenuItem("Launch Game", ProfileImage(_profile), (_, _) => LaunchGame())
            {
                Font = new Font(_menu.Font, FontStyle.Bold),
                Tag = new MenuTag(Subtitle: _profile.Name),
            });
        _menu.Items.Add("Show servers", null, (_, _) => TogglePopup());
        _menu.Items.Add(BuildProfileMenu());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        startup.Click += (_, _) => SetStartup(!startup.Checked);
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _menuRenderer.Apply(_menu);
        HookClickSound(_menu);
    }

    /// Menu entries click like the game's buttons.
    void HookClickSound(ToolStripDropDown menu)
    {
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
        {
            if (item.HasDropDownItems) HookClickSound(item.DropDown);
            else if (item.Enabled) item.Click += (_, _) => _click.Play();
        }
    }

    /// "Profile" submenu: automatic (last played) or a fixed profile, grouped by launcher.
    ToolStripMenuItem BuildProfileMenu()
    {
        var profiles = McProfile.Discover();
        string? chosen = Settings.Load().DefaultProfile;
        if (chosen != null && profiles.All(p => p.Id != chosen)) chosen = null;

        var menu = new ToolStripMenuItem("Profile");
        string lastPlayed = profiles.MaxBy(p => p.LastLaunch)?.Name ?? "none found";
        menu.DropDownItems.Add(new ToolStripMenuItem("Automatic", null, (_, _) => SetDefaultProfile(null))
        {
            Checked = chosen == null,
            Tag = new MenuTag(Subtitle: $"Last played: {lastPlayed}"),
        });

        foreach (var group in profiles.GroupBy(p => p.Launcher))
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            string launcher = group.Key;
            // The launcher's heading opens the launcher itself (the play arrow on its right says so).
            menu.DropDownItems.Add(new ToolStripMenuItem(launcher, Launchers.Icon(launcher), (_, _) => OpenLauncher(launcher))
            {
                Tag = new MenuTag(Header: true),
                Enabled = Launchers.IsInstalled(launcher),
            });
            foreach (var profile in group.OrderByDescending(p => p.LastLaunch))
                menu.DropDownItems.Add(new ToolStripMenuItem(profile.Name, ProfileImage(profile), (_, _) => SetDefaultProfile(profile.Id))
                {
                    Checked = profile.Id == chosen,
                    ToolTipText = profile.GameDir,
                });
        }
        return menu;
    }

    void SetDefaultProfile(string? id)
    {
        (Settings.Load() with { DefaultProfile = id }).Save();
        ReloadLookIfChanged();
        _servers = LoadServers(_profile is { } p ? [p] : []);
        if (_popup.Visible) _popup.Relayout();
        _ = PollAsync();
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
