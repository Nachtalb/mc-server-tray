using static McServerTray.McDraw;

namespace McServerTray;

/// Extra look for a menu item, via its Tag: a grey second line, or a group heading
/// (grey text with a play arrow on the right, for headings that also launch something).
sealed record MenuTag(string? Subtitle = null, bool Header = false);

/// Draws the tray menu like a Minecraft tooltip: the pack's tooltip background and frame,
/// the bitmap font with its shadow, and pixel-sharp item images.
sealed class McMenuRenderer(Func<McAssets> assets) : ToolStripRenderer
{
    const int ItemHeight = 18;
    const int SubtitledHeight = 24;
    const int ImageSize = 16;
    const int PlaySize = 12;
    const int ArrowWidth = 6;

    // Row layout in GUI pixels, measured from the dropdown's edges (not WinForms' text/arrow
    // rectangles, which come from the Windows font): the frame takes Inset on each side.
    const int Inset = 3;
    const int Gap = 4;
    const int IconX = Inset + 2;
    const int TextX = IconX + ImageSize + Gap;

    static string? Subtitle(ToolStripItem item) => (item.Tag as MenuTag)?.Subtitle;
    static bool IsHeader(ToolStripItem item) => item.Tag is MenuTag { Header: true };

    /// GUI scale for the strip's monitor (2 at 100%, like the popup).
    static int Scale(ToolStrip strip) => Math.Max(1, (int)Math.Round(strip.DeviceDpi / 96.0 * 2));

    /// Offsets (GUI px) from the item's left edge to the dropdown's left and right edges.
    static (float left, float right) Edges(ToolStripItem item, int s) =>
        (-(float)item.Bounds.X / s, (float)(item.Owner!.Width - item.Bounds.X) / s);

    /// Sizes the dropdown to its widest row in the Minecraft font, since WinForms would measure with the Windows font.
    public void Apply(ToolStripDropDownMenu menu)
    {
        int s = Scale(menu);
        var font = assets().Font;
        menu.Renderer = this;
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = true;
        menu.ImageScalingSize = new Size(ImageSize * s, ImageSize * s);
        // The dropdown ignores its own top/bottom padding, so the first and last rows carry the
        // space to the frame as margins instead.
        menu.Padding = Padding.Empty;

        float RowWidth(ToolStripMenuItem i)
        {
            float text = Math.Max(font.Width(i.Text ?? "") + (i.Font.Bold ? (i.Text ?? "").Length : 0), font.Width(Subtitle(i) ?? ""));
            float right = i.HasDropDownItems ? Gap + ArrowWidth : IsHeader(i) ? Gap + PlaySize : 0;
            return TextX + text + right + Gap + Inset;
        }
        var rows = menu.Items.OfType<ToolStripMenuItem>().ToList();
        int width = (int)Math.Ceiling((rows.Count == 0 ? 40 : rows.Max(RowWidth)) * s);

        int height = 0;
        var all = menu.Items.Cast<ToolStripItem>().ToList();
        foreach (var item in all)
        {
            item.AutoSize = false;
            int h = item is ToolStripSeparator ? 5 : Subtitle(item) != null ? SubtitledHeight : ItemHeight;
            item.Size = new Size(width, h * s);
            // Leaves the same gap between the frame and a highlighted first/last row as between a
            // highlighted row and a separator line. The dropdown already adds 1 GUI px above the
            // first row but nothing below the last, hence the uneven margins.
            item.Margin = new Padding(0, item == all[0] ? Inset * s : 0, 0, item == all[^1] ? (Inset + 1) * s : 0);
            height += item.Height + item.Margin.Vertical;
            if (item is ToolStripMenuItem { HasDropDownItems: true, DropDown: ToolStripDropDownMenu sub })
                Apply(sub);
        }

        menu.AutoSize = false;
        menu.Size = new Size(width, height);

        // Same rounded window corners as the server list popup.
        if (menu.IsHandleCreated) Native.RoundCorners(menu.Handle);
        else menu.HandleCreated += (_, _) => Native.RoundCorners(menu.Handle);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        int s = Scale(e.ToolStrip);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        var box = new RectangleF(0, 0, (float)e.ToolStrip.Width / s, (float)e.ToolStrip.Height / s);
        var a = assets();
        if (a.Sprite("gui/sprites/tooltip/background.png") is { } bg && a.Sprite("gui/sprites/tooltip/frame.png") is { } frame)
        {
            // The game draws these 9 GUI pixels outside the tooltip's box (the sprites have a
            // transparent margin); 8 puts the frame one pixel inside our window edge.
            g.Clear(Color.FromArgb(0x10, 0x00, 0x10));
            var outer = RectangleF.Inflate(box, 8, 8);
            DrawSprite(g, bg, outer);
            DrawSprite(g, frame, outer);
        }
        else
        {
            g.Clear(Color.FromArgb(0x10, 0x00, 0x10));
            using var pen = new Pen(Color.FromArgb(0x50, 0x00, 0xFF), 1);
            g.DrawRectangle(pen, 1, 1, box.Width - 3, box.Height - 3);
        }
        g.Restore(state);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        int s = Scale(e.ToolStrip!);
        // The whole row, clipped to the inside of the frame on every side (WinForms may place
        // the first and last rows closer to the edge than the padding asks for).
        var (left, right) = Edges(e.Item, s);
        // Vertically the frame line ends 2 GUI px in, plus the 2 px gap a separator also leaves.
        float top = Math.Max(0, (Inset + 1) * s - e.Item.Bounds.Y);
        float bottom = Math.Min(e.Item.Height, e.Item.Owner!.Height - (Inset + 1) * s - e.Item.Bounds.Y);
        using var hover = new SolidBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        e.Graphics.FillRectangle(hover, RectangleF.FromLTRB((left + Inset) * s, top, (right - Inset) * s, bottom));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        int s = Scale(e.ToolStrip!);
        var item = e.Item;
        int color = !item.Enabled ? 0x808080
            : IsHeader(item) ? (item.Selected ? 0xFFFFFF : 0xAAAAAA)
            : item is ToolStripMenuItem { Checked: true } ? 0xFFFF55 : 0xFFFFFF;
        var style = new McStyle(color, Bold: item.Font.Bold);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        var font = assets().Font;
        var (left, right) = Edges(item, s);
        float x = left + TextX;
        if (Subtitle(item) is { } subtitle)
        {
            font.Draw(g, item.Text ?? "", style, x, 3);
            font.Draw(g, subtitle, McStyle.Gray, x, 13);
        }
        else font.Draw(g, item.Text ?? "", style, x, (item.Height / s - 8) / 2f);

        if (IsHeader(item) && item.Enabled)
        {
            // The server list's play arrow, lit while hovered.
            string sprite = item.Selected ? "gui/sprites/server_list/join_highlighted.png" : "gui/sprites/server_list/join.png";
            float px = right - Inset - Gap - PlaySize, py = (item.Height / s - PlaySize) / 2f;
            if (assets().Sprite(sprite) is { } play)
            {
                // The arrow sits in the right half of the 32x32 sprite.
                var img = play.Image;
                Blit(g, img, new RectangleF(px, py, PlaySize, PlaySize), new RectangleF(img.Width / 2f, img.Height / 4f, img.Width / 2f, img.Height / 2f));
            }
            else font.Draw(g, ">", McStyle.White, px + 4, py + 2);
        }
        g.Restore(state);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is not Bitmap img) return;
        int s = Scale(e.ToolStrip!);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        var r = new RectangleF(Edges(e.Item, s).left + IconX, (e.Item.Height / s - ImageSize) / 2f, ImageSize, ImageSize);
        if (img.Width > ImageSize * s)
        {
            // Rendered cubes and launcher icons are bigger than the slot: shrink smoothly, not pixel-dropping.
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(img, r);
        }
        else Blit(g, img, r);
        g.Restore(state);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Items with an image show being checked by their yellow text only; plain ones get a check mark.
        if (e.Item.Image != null) return;
        int s = Scale(e.ToolStrip!);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        float x = Edges(e.Item, s).left + IconX + (ImageSize - 8) / 2f;
        assets().Font.Draw(g, "✔", new McStyle(0x55FF55), x, (e.Item.Height / s - 8) / 2f);
        g.Restore(state);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int s = Scale(e.ToolStrip!);
        var (left, right) = Edges(e.Item, s);
        using var line = new SolidBrush(Color.FromArgb(0x50, 0x28, 0x00, 0x7F));
        e.Graphics.FillRectangle(line, RectangleF.FromLTRB((left + Inset + 1) * s, e.Item.Height / 2 - s / 2, (right - Inset - 1) * s, e.Item.Height / 2 - s / 2 + s));
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        int s = Scale(e.Item!.Owner!);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        float x = Edges(e.Item, s).right - Inset - Gap - ArrowWidth;
        assets().Font.Draw(g, ">", McStyle.White, x, (e.Item.Height / s - 8) / 2f);
        g.Restore(state);
    }
}
