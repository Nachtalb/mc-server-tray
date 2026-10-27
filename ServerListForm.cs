using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

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
    const int ButtonWidth = 100;
    const int ButtonHeight = 20;

    readonly McAssets _assets;
    readonly Func<IReadOnlyList<ServerView>> _views;
    readonly Func<Task> _refresh;
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 100 };
    readonly ImageAttributes _clamp = new();
    int _scale = 2;
    Point _mouse = new(-1, -1);
    long _hiddenAt;

    public ServerListForm(McAssets assets, Func<IReadOnlyList<ServerView>> views, Func<Task> refresh)
    {
        _assets = assets;
        _views = views;
        _refresh = refresh;
        _clamp.SetWrapMode(WrapMode.TileFlipXY);

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
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HidePopup(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

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
        int rows = Math.Max(1, _views().Count);
        var size = new Size(Width0 * _scale, (HeaderHeight + rows * RowHeight + 6 + FooterHeight) * _scale);
        var area = screen.WorkingArea;
        Bounds = new Rectangle(area.Right - size.Width - 12, area.Bottom - size.Height - 12, size.Width, size.Height);
        Invalidate();
    }

    int ListTop => HeaderHeight;
    int ListBottom => ClientSize.Height / _scale - FooterHeight;
    int RowLeft => (Width0 - RowWidth) / 2;
    Rectangle RefreshButton => new(Width0 / 2 - ButtonWidth - 2, ListBottom + 5, ButtonWidth, ButtonHeight);
    Rectangle DoneButton => new(Width0 / 2 + 2, ListBottom + 5, ButtonWidth, ButtonHeight);
    Point GuiMouse => new(_mouse.X < 0 ? -1 : _mouse.X / _scale, _mouse.Y < 0 ? -1 : _mouse.Y / _scale);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override async void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var p = GuiMouse;
        if (DoneButton.Contains(p)) HidePopup();
        else if (RefreshButton.Contains(p)) await _refresh();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        g.ScaleTransform(_scale, _scale);

        int height = ClientSize.Height / _scale;
        var font = _assets.Font;
        var mouse = GuiMouse;
        var views = _views();
        List<StyledChar>? tooltip = null;

        // The game's menu textures are translucent black overlays meant for the world behind the
        // screen; a neutral grey base gives the same two-tone header/list look.
        Fill(g, new Rectangle(0, 0, Width0, height), 0x404040);
        Tile(g, "gui/menu_background.png", new Rectangle(0, 0, Width0, height), Color.FromArgb(40, 40, 40));
        Tile(g, "gui/menu_list_background.png", new Rectangle(0, ListTop, Width0, ListBottom - ListTop), Color.FromArgb(20, 20, 20));
        Tile(g, "gui/header_separator.png", new Rectangle(0, ListTop - 2, Width0, 2), Color.FromArgb(100, 100, 100));
        Tile(g, "gui/footer_separator.png", new Rectangle(0, ListBottom, Width0, 2), Color.FromArgb(100, 100, 100));

        string title = "Play Multiplayer";
        font.Draw(g, title, McStyle.White, (Width0 - font.Width(title)) / 2, 8);

        if (views.Count == 0)
        {
            string none = "No servers in your Prism instances";
            font.Draw(g, none, McStyle.Gray, (Width0 - font.Width(none)) / 2, ListTop + 14);
        }

        for (int i = 0; i < views.Count; i++)
        {
            int top = ListTop + 2 + i * RowHeight;
            var row = new Rectangle(RowLeft - 2, top - 2, RowWidth + 4, RowHeight);
            if (row.Contains(mouse))
            {
                Fill(g, row, 0x808080);
                Fill(g, Rectangle.Inflate(row, -1, -1), 0x000000);
            }
            tooltip = DrawEntry(g, views[i], RowLeft, top, mouse) ?? tooltip;
        }

        DrawButton(g, RefreshButton, "Refresh", mouse);
        DrawButton(g, DoneButton, "Done", mouse);

        if (tooltip != null) DrawTooltip(g, tooltip, mouse, height);
    }

    /// One server row; returns tooltip text if the mouse is over the player count or ping icon.
    List<StyledChar>? DrawEntry(Graphics g, ServerView view, int x, int y, Point mouse)
    {
        var font = _assets.Font;
        var status = view.Status;
        List<StyledChar>? tooltip = null;

        var icon = view.Icon ?? _assets.Texture("misc/unknown_server.png");
        if (icon != null) Blit(g, icon, new RectangleF(x, y, 32, 32), new RectangleF(0, 0, icon.Width, icon.Height));

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
        if (_assets.Texture(sprite) is { } ping) Blit(g, ping, new RectangleF(pingX, y, 10, 8), new RectangleF(0, 0, ping.Width, ping.Height));
        else Fill(g, new Rectangle(pingX, y, 10, 8), status?.Online == true ? 0x55FF55 : 0xAA0000);

        if (new Rectangle(pingX, y, 10, 8).Contains(mouse))
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

    void DrawButton(Graphics g, Rectangle r, string label, Point mouse)
    {
        bool hover = r.Contains(mouse);
        var tex = _assets.Texture(hover ? "gui/sprites/widget/button_highlighted.png" : "gui/sprites/widget/button.png");
        if (tex != null) NineSlice(g, tex, 3, r);
        else
        {
            Fill(g, r, hover ? 0xFFFFFF : 0x000000);
            Fill(g, Rectangle.Inflate(r, -1, -1), 0x6F6F6F);
        }
        var font = _assets.Font;
        font.Draw(g, label, McStyle.White, r.X + (r.Width - font.Width(label)) / 2, r.Y + (r.Height - 8) / 2);
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
        var bg = _assets.Texture("gui/sprites/tooltip/background.png");
        var frame = _assets.Texture("gui/sprites/tooltip/frame.png");
        if (bg != null && frame != null)
        {
            var outer = RectangleF.Inflate(box, 9, 9);
            NineSlice(g, bg, 9, outer);
            NineSlice(g, frame, 10, outer);
        }
        else
        {
            using var b = new SolidBrush(Color.FromArgb(0xF0, 0x10, 0x00, 0x10));
            g.FillRectangle(b, box);
        }

        for (int i = 0; i < lines.Count; i++)
            font.Draw(g, lines[i], x, y + i * 10 + (i > 0 ? 2 : 0));
    }

    void Tile(Graphics g, string texture, Rectangle area, Color fallback)
    {
        var tex = _assets.Texture(texture);
        if (tex == null)
        {
            using var b = new SolidBrush(fallback);
            g.FillRectangle(b, area);
            return;
        }
        // The game maps 16 texels onto 32 GUI pixels for the menu backgrounds.
        int tw = tex.Height <= 2 ? tex.Width : tex.Width * 2, th = tex.Height <= 2 ? tex.Height : tex.Height * 2;
        var state = g.Save();
        g.SetClip(area);
        for (int ty = area.Top; ty < area.Bottom; ty += th)
            for (int tx = area.Left; tx < area.Right; tx += tw)
                Blit(g, tex, new RectangleF(tx, ty, tw, th), new RectangleF(0, 0, tex.Width, tex.Height));
        g.Restore(state);
    }

    void NineSlice(Graphics g, Bitmap tex, int border, RectangleF dest)
    {
        float[] sx = [0, border, tex.Width - border, tex.Width];
        float[] sy = [0, border, tex.Height - border, tex.Height];
        float[] dx = [dest.Left, dest.Left + border, dest.Right - border, dest.Right];
        float[] dy = [dest.Top, dest.Top + border, dest.Bottom - border, dest.Bottom];
        if (dest.Height < tex.Height) { sy = [0, tex.Height / 2f, tex.Height / 2f, tex.Height]; dy = [dest.Top, dest.Top + dest.Height / 2, dest.Top + dest.Height / 2, dest.Bottom]; }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (dx[i + 1] > dx[i] && dy[j + 1] > dy[j])
                    Blit(g, tex,
                        new RectangleF(dx[i], dy[j], dx[i + 1] - dx[i], dy[j + 1] - dy[j]),
                        new RectangleF(sx[i], sy[j], sx[i + 1] - sx[i], sy[j + 1] - sy[j]));
    }

    void Blit(Graphics g, Bitmap tex, RectangleF dest, RectangleF src) =>
        g.DrawImage(tex, [dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom)], src, GraphicsUnit.Pixel, _clamp);

    static void Fill(Graphics g, Rectangle r, int rgb)
    {
        using var b = new SolidBrush(Color.FromArgb(255, Color.FromArgb(rgb)));
        g.FillRectangle(b, r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _clamp.Dispose();
        }
        base.Dispose(disposing);
    }
}
