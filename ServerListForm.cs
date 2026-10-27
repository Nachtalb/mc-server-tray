using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static McServerTray.McDraw;
using System.Runtime.InteropServices;

namespace McServerTray;

record ServerView(ServerEntry Entry, ServerStatus? Status, Bitmap? Icon);

/// Borderless popup drawn like the in-game Multiplayer screen. All layout is in GUI pixels
/// (same numbers as the game's ServerSelectionList), scaled by the GUI scale.
sealed class ServerListForm : Form
{
    const int Width0 = 320;
    const int RowWidth = 305;
    const int RowHeight = 36;
    const int HeaderHeight = 24;
    const int FooterHeight = 30;
    const int ButtonWidth = 98;
    const int ButtonHeight = 20;
    const int Frame = 1;

    readonly Func<IReadOnlyList<ServerView>> _views;
    readonly Func<Task> _refresh;
    readonly Action<ServerView> _join;
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 100 };
    McAssets _assets;
    McSound _click;
    int _scale = 2;
    Point _mouse = new(-1, -1);
    string? _selected;
    long _hiddenAt;
    int _visibleRows = 1;
    int _scroll;
    bool _dragging;
    int _dragStartY, _dragStartScroll;

    public ServerListForm(McAssets assets, Func<IReadOnlyList<ServerView>> views, Func<Task> refresh, Action<ServerView> join)
    {
        _assets = assets;
        _click = new McSound(assets.Sound("random/click"), 0.25f);
        _views = views;
        _refresh = refresh;
        _join = join;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Minecraft servers";

        _anim.Tick += (_, _) => Invalidate();
        Deactivate += (_, _) => HidePopup();
    }

    public void SetAssets(McAssets assets)
    {
        _assets = assets;
        _click.Dispose();
        _click = new McSound(assets.Sound("random/click"), 0.25f);
        Invalidate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: small rounded corners and the system shadow, like other flyouts.
        int round = 3; // DWMWCP_ROUNDSMALL
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public void Toggle()
    {
        // The click on the tray icon deactivates us first, so don't immediately reopen.
        if (Visible || Environment.TickCount64 - _hiddenAt < 300)
        {
            HidePopup();
            return;
        }
        Relayout();
        Show();
        Activate();
        _anim.Start();
    }

    void HidePopup()
    {
        if (!Visible) return;
        _hiddenAt = Environment.TickCount64;
        _anim.Stop();
        Hide();
    }

    public void Relayout()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        _scale = Math.Max(1, (int)Math.Round(DeviceDpi / 96.0 * 2));
        var area = screen.WorkingArea;
        // As many rows as fit on screen; the rest scroll.
        int fit = (area.Height / _scale - 24 - HeaderHeight - 6 - FooterHeight) / RowHeight;
        _visibleRows = Math.Clamp(_views().Count, 1, Math.Max(1, fit));
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        var size = new Size(Width0 * _scale, GuiHeight * _scale);
        Bounds = new Rectangle(area.Right - size.Width - 12, area.Bottom - size.Height - 12, size.Width, size.Height);
        Invalidate();
    }

    int GuiHeight => HeaderHeight + _visibleRows * RowHeight + 6 + FooterHeight;
    int ListTop => HeaderHeight;
    int ListBottom => GuiHeight - FooterHeight;
    int MaxScroll => Math.Max(0, (_views().Count - _visibleRows) * RowHeight);
    // Rows move left to make room for the scrollbar, like the game's lists.
    int RowLeft => MaxScroll > 0 ? 4 : (Width0 - RowWidth) / 2;
    int RowTop(int i) => ListTop + 4 + i * RowHeight - _scroll;
    Rectangle ScrollTrack => new(Width0 - 8, ListTop, 6, ListBottom - ListTop);

    Rectangle ScrollThumb
    {
        get
        {
            var track = ScrollTrack;
            int h = Math.Clamp(track.Height * track.Height / (track.Height + MaxScroll), 32, track.Height);
            int y = MaxScroll == 0 ? track.Y : track.Y + _scroll * (track.Height - h) / MaxScroll;
            return new Rectangle(track.X, y, track.Width, h);
        }
    }

    void ScrollTo(int scroll)
    {
        _scroll = Math.Clamp(scroll, 0, MaxScroll);
        Invalidate();
    }

    void EnsureVisible(int row)
    {
        int top = row * RowHeight, bottom = top + RowHeight;
        if (top < _scroll) ScrollTo(top);
        else if (bottom > _scroll + _visibleRows * RowHeight) ScrollTo(bottom - _visibleRows * RowHeight);
    }
    int ButtonsLeft => (Width0 - 3 * ButtonWidth - 8) / 2;
    Rectangle JoinButton => new(ButtonsLeft, ListBottom + 5, ButtonWidth, ButtonHeight);
    Rectangle RefreshButton => new(ButtonsLeft + ButtonWidth + 4, ListBottom + 5, ButtonWidth, ButtonHeight);
    Rectangle DoneButton => new(ButtonsLeft + 2 * (ButtonWidth + 4), ListBottom + 5, ButtonWidth, ButtonHeight);
    Point GuiMouse => _mouse.X < 0 ? new Point(-1, -1) : new Point(_mouse.X / _scale, _mouse.Y / _scale);

    ServerView? Selected => _views().FirstOrDefault(v => v.Entry.Address == _selected);

    int RowAt(Point p)
    {
        if (p.X < RowLeft - 2 || p.X >= RowLeft + RowWidth + 2 || p.Y < ListTop || p.Y >= ListBottom) return -1;
        int count = _views().Count;
        for (int i = 0; i < count; i++)
            if (p.Y >= RowTop(i) - 2 && p.Y < RowTop(i) - 2 + RowHeight) return i;
        return -1;
    }

    /// Debug aid for --snapshot: pretend the mouse is at a GUI position and a row is selected.
    public void Simulate(Point guiMouse, int selectedRow)
    {
        _scale = Math.Max(1, (int)Math.Round(DeviceDpi / 96.0 * 2));
        _mouse = new Point(guiMouse.X * _scale + _scale / 2, guiMouse.Y * _scale + _scale / 2);
        var views = _views();
        _selected = selectedRow >= 0 && selectedRow < views.Count ? views[selectedRow].Entry.Address : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;
        if (_dragging)
        {
            var track = ScrollTrack;
            int range = Math.Max(1, track.Height - ScrollThumb.Height);
            ScrollTo(_dragStartScroll + (GuiMouse.Y - _dragStartY) * MaxScroll / range);
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) => _dragging = false;

    protected override void OnMouseWheel(MouseEventArgs e) => ScrollTo(_scroll - e.Delta / 120 * (RowHeight / 2));

    protected override void OnMouseLeave(EventArgs e)
    {
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override async void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var p = GuiMouse;
        var views = _views();

        if (MaxScroll > 0 && ScrollTrack.Contains(p))
        {
            // Clicking the track jumps the thumb there; either way, start dragging it.
            var thumb = ScrollThumb;
            if (!thumb.Contains(p))
                ScrollTo((p.Y - ScrollTrack.Y - thumb.Height / 2) * MaxScroll / Math.Max(1, ScrollTrack.Height - thumb.Height));
            _dragging = true;
            _dragStartY = p.Y;
            _dragStartScroll = _scroll;
            return;
        }

        int row = RowAt(p);
        if (row >= 0)
        {
            var view = views[row];
            // Like the game: clicking the play arrow over the icon joins straight away.
            if (p.X - RowLeft < 32 && view.Status != null)
            {
                _selected = view.Entry.Address;
                _click.Play();
                Join(view);
                return;
            }
            // Clicking the selected server again deselects it.
            _selected = _selected == view.Entry.Address ? null : view.Entry.Address;
            Invalidate();
            return;
        }

        if (JoinButton.Contains(p) && Selected is { } sel)
        {
            _click.Play();
            Join(sel);
        }
        else if (RefreshButton.Contains(p))
        {
            _click.Play();
            await _refresh();
        }
        else if (DoneButton.Contains(p))
        {
            _click.Play();
            HidePopup();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        int row = RowAt(GuiMouse);
        if (e.Button == MouseButtons.Left && row >= 0) Join(_views()[row]);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var views = _views();
        int current = views.ToList().FindIndex(v => v.Entry.Address == _selected);
        switch (e.KeyCode)
        {
            case Keys.Escape:
                HidePopup();
                break;
            case Keys.Up when views.Count > 0:
                _selected = views[Math.Max(0, current - 1)].Entry.Address;
                EnsureVisible(Math.Max(0, current - 1));
                break;
            case Keys.Down when views.Count > 0:
                _selected = views[Math.Min(views.Count - 1, current + 1)].Entry.Address;
                EnsureVisible(Math.Min(views.Count - 1, current + 1));
                break;
            case Keys.Enter when Selected is { } sel:
                Join(sel);
                break;
        }
        Invalidate();
    }

    void Join(ServerView view)
    {
        HidePopup();
        _join(view);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PixelArt(g, _scale);

        int height = GuiHeight;
        var font = _assets.Font;
        var mouse = GuiMouse;
        var views = _views();
        List<StyledChar>? tooltip = null;

        // Classic dirt: header and footer at 25% brightness, the list behind it darker.
        Dirt(g, new Rectangle(0, 0, Width0, height), 0.25f);
        Dirt(g, new Rectangle(0, ListTop, Width0, ListBottom - ListTop), 0.125f);
        Tile(g, "gui/header_separator.png", new Rectangle(0, ListTop - 2, Width0, 2));
        Tile(g, "gui/footer_separator.png", new Rectangle(0, ListBottom, Width0, 2));

        string title = "Play Multiplayer";
        font.Draw(g, title, McStyle.White, (Width0 - font.Width(title)) / 2, 8);

        if (views.Count == 0)
        {
            string none = "No servers in your Prism instances";
            font.Draw(g, none, McStyle.Gray, (Width0 - font.Width(none)) / 2, ListTop + 14);
        }

        int hoveredRow = _dragging ? -1 : RowAt(mouse);
        var listState = g.Save();
        g.SetClip(new Rectangle(0, ListTop, Width0, ListBottom - ListTop));
        for (int i = 0; i < views.Count; i++)
        {
            int top = RowTop(i);
            if (top + RowHeight < ListTop || top - 2 > ListBottom) continue;
            bool selected = views[i].Entry.Address == _selected;
            if (selected)
            {
                var box = new Rectangle(RowLeft - 2, top - 2, RowWidth + 4, RowHeight - 4 + 4);
                Fill(g, box, 0xFFFFFF);
                Fill(g, Rectangle.Inflate(box, -1, -1), 0x000000);
            }
            tooltip = DrawEntry(g, views[i], RowLeft, top, mouse, hoveredRow == i || selected) ?? tooltip;
        }
        g.Restore(listState);

        if (MaxScroll > 0)
        {
            if (_assets.Sprite("gui/sprites/widget/scroller_background.png") is { } track) DrawSprite(g, track, ScrollTrack);
            else Fill(g, ScrollTrack, 0x000000);
            if (_assets.Sprite("gui/sprites/widget/scroller.png") is { } thumb) DrawSprite(g, thumb, ScrollThumb);
            else Fill(g, ScrollThumb, 0x808080);
        }

        DrawButton(g, JoinButton, "Join Server", mouse, enabled: Selected is { Status: not null });
        DrawButton(g, RefreshButton, "Refresh", mouse, enabled: true);
        DrawButton(g, DoneButton, "Done", mouse, enabled: true);

        // A thin frame so the popup sits on the desktop like a window rather than a loose rectangle.
        Fill(g, new Rectangle(0, 0, Width0, Frame), 0x000000);
        Fill(g, new Rectangle(0, height - Frame, Width0, Frame), 0x000000);
        Fill(g, new Rectangle(0, 0, Frame, height), 0x000000);
        Fill(g, new Rectangle(Width0 - Frame, 0, Frame, height), 0x000000);

        if (tooltip != null) DrawTooltip(g, tooltip, mouse, height);
    }

    /// One server row; returns tooltip text if the mouse is over the player count or ping icon.
    List<StyledChar>? DrawEntry(Graphics g, ServerView view, int x, int y, Point mouse, bool showJoin)
    {
        var font = _assets.Font;
        var status = view.Status;
        List<StyledChar>? tooltip = null;

        var iconRect = new RectangleF(x, y, 32, 32);
        if (view.Icon is { } icon) Blit(g, icon, iconRect);
        else if (_assets.Sprite("misc/unknown_server.png") is { } unknown) Blit(g, unknown.Image, iconRect);

        // The play arrow over a dimmed icon, highlighted while the mouse is on its right half.
        if (showJoin && status != null)
        {
            using (var dim = new SolidBrush(Color.FromArgb(0xA0, 0x90, 0x90, 0x90)))
                g.FillRectangle(dim, iconRect);
            int rel = mouse.X - x;
            bool hot = mouse.Y >= y && mouse.Y < y + 32 && rel > 16 && rel < 32;
            if (_assets.Sprite(hot ? "gui/sprites/server_list/join_highlighted.png" : "gui/sprites/server_list/join.png") is { } join)
                Blit(g, join.Image, iconRect);
        }

        font.Draw(g, view.Entry.Name, McStyle.White, x + 35, y + 1);

        List<StyledChar> motd = status switch
        {
            null => McText.Plain("Pinging...", new McStyle(McText.DarkGray)),
            { Online: false } => McText.Plain("Can't connect to server", new McStyle(McText.DarkRed)),
            _ => McText.FromJson(status.DescriptionJson, McStyle.Gray),
        };
        var lines = font.Wrap(motd, RowWidth - 32 - 2);
        for (int i = 0; i < Math.Min(2, lines.Count); i++)
            font.Draw(g, lines[i], x + 35, y + 12 + McFont.LineHeight * i);

        int pingX = x + RowWidth - 15;
        string sprite;
        if (status == null)
        {
            long frame = Environment.TickCount64 / 100 % 8;
            sprite = $"gui/sprites/server_list/pinging_{(frame > 4 ? 8 - frame : frame) + 1}.png";
        }
        else if (!status.Online) sprite = "gui/sprites/server_list/unreachable.png";
        else
        {
            long ms = status.PingMs ?? 0;
            int bars = ms < 150 ? 5 : ms < 300 ? 4 : ms < 600 ? 3 : ms < 1000 ? 2 : 1;
            sprite = $"gui/sprites/server_list/ping_{bars}.png";
        }
        var pingRect = new Rectangle(pingX, y, 10, 8);
        if (_assets.Sprite(sprite) is { } ping) Blit(g, ping.Image, pingRect);
        else Fill(g, pingRect, status?.Online == true ? 0x55FF55 : 0xAA0000);

        if (pingRect.Contains(mouse))
        {
            tooltip = status switch
            {
                null => McText.Plain("Pinging...", McStyle.White),
                { Online: false } => McText.Plain($"Can't connect: {status.Error}", new McStyle(0xFF5555)),
                _ => McText.Plain(status.PingMs is { } ms ? $"{ms} ms" : "(no ping)", McStyle.White),
            };
            if (status is { Online: true, Version: { } version })
                tooltip.AddRange(McText.Plain("\n" + version, McStyle.Gray));
        }

        if (status is { Online: true })
        {
            var count = McText.Plain(status.PlayersOnline.ToString(), McStyle.Gray);
            count.AddRange(McText.Plain("/", new McStyle(McText.DarkGray)));
            count.AddRange(McText.Plain(status.PlayersMax.ToString(), McStyle.Gray));
            float w = font.Width(count);
            float countX = pingX - 2 - w;
            font.Draw(g, count, countX, y + 1);

            if (new RectangleF(countX, y, w, 9).Contains(mouse) && status.PlayersOnline > 0)
            {
                tooltip = [];
                foreach (string name in status.PlayerNames)
                {
                    if (tooltip.Count > 0) tooltip.Add(new StyledChar('\n', McStyle.White));
                    tooltip.AddRange(McText.FromJson(System.Text.Json.JsonSerializer.Serialize(name), McStyle.Gray));
                }
                int more = status.PlayersOnline - status.PlayerNames.Count;
                if (more > 0)
                {
                    if (tooltip.Count > 0) tooltip.Add(new StyledChar('\n', McStyle.White));
                    tooltip.AddRange(McText.Plain($"... and {more} more ...", McStyle.Gray));
                }
            }
        }

        return tooltip;
    }

    void DrawButton(Graphics g, Rectangle r, string label, Point mouse, bool enabled)
    {
        bool hover = enabled && r.Contains(mouse);
        string sprite = !enabled ? "gui/sprites/widget/button_disabled.png"
            : hover ? "gui/sprites/widget/button_highlighted.png"
            : "gui/sprites/widget/button.png";
        if (_assets.Sprite(sprite) is { } tex) DrawSprite(g, tex, r);
        else
        {
            Fill(g, r, hover ? 0xFFFFFF : 0x000000);
            Fill(g, Rectangle.Inflate(r, -1, -1), enabled ? 0x6F6F6F : 0x2F2F2F);
        }
        var font = _assets.Font;
        var style = new McStyle(enabled ? 0xFFFFFF : 0xA0A0A0);
        font.Draw(g, label, style, r.X + (r.Width - font.Width(label)) / 2, r.Y + (r.Height - 8) / 2);
    }

    void DrawTooltip(Graphics g, List<StyledChar> text, Point mouse, int height)
    {
        var font = _assets.Font;
        var lines = font.Wrap(text, 200);
        float w = lines.Max(l => font.Width(l));
        int h = lines.Count == 1 ? 8 : 10 + (lines.Count - 1) * 10;

        float x = mouse.X + 12, y = mouse.Y - 12;
        if (x + w + 4 > Width0) x = Math.Max(4, mouse.X - 16 - w);
        y = Math.Clamp(y, 4, Math.Max(4, height - h - 4));

        var box = new RectangleF(x - 3, y - 3, w + 6, h + 6);
        var bg = _assets.Sprite("gui/sprites/tooltip/background.png");
        var frame = _assets.Sprite("gui/sprites/tooltip/frame.png");
        if (bg != null && frame != null)
        {
            var outer = RectangleF.Inflate(box, 9, 9);
            DrawSprite(g, bg, outer);
            DrawSprite(g, frame, outer);
        }
        else
        {
            using var b = new SolidBrush(Color.FromArgb(0xF0, 0x10, 0x00, 0x10));
            g.FillRectangle(b, box);
        }

        for (int i = 0; i < lines.Count; i++)
            font.Draw(g, lines[i], x, y + i * 10 + (i > 0 ? 2 : 0));
    }

    /// The dirt block tiled at 32 GUI pixels per block (2 per texel at 16x), darkened like the old menus.
    void Dirt(Graphics g, Rectangle area, float brightness)
    {
        var dirt = _assets.Sprite("block/dirt.png");
        if (dirt == null)
        {
            int v = (int)(brightness * 0x80);
            Fill(g, area, v << 16 | v << 8 | v);
            return;
        }
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        attrs.SetColorMatrix(new ColorMatrix(
        [
            [brightness, 0, 0, 0, 0],
            [0, brightness, 0, 0, 0],
            [0, 0, brightness, 0, 0],
            [0, 0, 0, 1, 0],
            [0, 0, 0, 0, 1],
        ]));
        TileImage(g, dirt.Image, area, 32, 32, attrs);
    }

    void Tile(Graphics g, string texture, Rectangle area)
    {
        if (_assets.Sprite(texture) is not { } tex) return;
        // Separators are 32x2 GUI pixels per tile regardless of the pack's resolution.
        TileImage(g, tex.Image, area, 32, 2, Clamp);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _click.Dispose();
        }
        base.Dispose(disposing);
    }
}
