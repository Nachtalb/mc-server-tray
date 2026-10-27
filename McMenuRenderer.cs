using static McServerTray.McDraw;

namespace McServerTray;

/// Draws the tray menu like a Minecraft tooltip: the pack's tooltip background and frame,
/// the bitmap font with its shadow, and pixel-sharp item images.
/// An item whose Tag is a string shows it as a grey second line under its text.
sealed class McMenuRenderer(Func<McAssets> assets) : ToolStripRenderer
{
    const int ItemHeight = 18;
    const int SubtitledHeight = 24;
    const int ImageSize = 16;

    static string? Subtitle(ToolStripItem item) => item.Tag as string;

    /// GUI scale for the strip's monitor (2 at 100%, like the popup).
    static int Scale(ToolStrip strip) => Math.Max(1, (int)Math.Round(strip.DeviceDpi / 96.0 * 2));

    /// Sizes every item from the Minecraft font, since WinForms would measure with the Windows font.
    public void Apply(ToolStripDropDownMenu menu)
    {
        int s = Scale(menu);
        var font = assets().Font;
        menu.Renderer = this;
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = true;
        menu.ImageScalingSize = new Size(ImageSize * s, ImageSize * s);
        menu.Padding = new Padding(3 * s, 4 * s, 3 * s, 4 * s);

        var items = menu.Items.OfType<ToolStripMenuItem>().ToList();
        float textWidth = items.Count == 0 ? 0 : items.Max(i => Math.Max(
            font.Width(i.Text ?? "") + (i.Font.Bold ? (i.Text ?? "").Length : 0),
            font.Width(Subtitle(i) ?? "")));
        int width = (int)((ImageSize + 10 + textWidth + 16) * s);
        int height = 0;
        foreach (ToolStripItem item in menu.Items)
        {
            item.AutoSize = false;
            int h = item is ToolStripSeparator ? 5 : Subtitle(item) != null ? SubtitledHeight : ItemHeight;
            item.Size = new Size(width, h * s);
            height += item.Height;
            if (item is ToolStripMenuItem { HasDropDownItems: true, DropDown: ToolStripDropDownMenu sub })
                Apply(sub);
        }

        // The dropdown would size itself with the Windows font; size it from our items instead.
        menu.AutoSize = false;
        menu.Size = new Size(width + menu.Padding.Horizontal + 2, height + menu.Padding.Vertical + 2);
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
        using var hover = new SolidBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        e.Graphics.FillRectangle(hover, new Rectangle(s, 0, e.Item.Width - 2 * s, e.Item.Height));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        int s = Scale(e.ToolStrip!);
        var item = e.Item;
        int color = !item.Enabled ? 0x808080 : item is ToolStripMenuItem { Checked: true } ? 0xFFFF55 : 0xFFFFFF;
        var style = new McStyle(color, Bold: item.Font.Bold);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        var font = assets().Font;
        float x = (float)e.TextRectangle.X / s;
        if (Subtitle(item) is { } subtitle)
        {
            font.Draw(g, item.Text ?? "", style, x, 3);
            font.Draw(g, subtitle, McStyle.Gray, x, 13);
        }
        else font.Draw(g, item.Text ?? "", style, x, (item.Height / s - 8) / 2f);
        g.Restore(state);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is not Bitmap img) return;
        int s = Scale(e.ToolStrip!);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        var r = new RectangleF(5, (e.Item.Height / s - ImageSize) / 2f, ImageSize, ImageSize);
        if (e.Item is ToolStripMenuItem { Checked: true })
        {
            // Selected profile: a white frame around its icon, like a selected list entry.
            Fill(g, Rectangle.Round(RectangleF.Inflate(r, 1, 1)), 0xFFFFFF);
            Fill(g, Rectangle.Round(r), 0x000000);
        }
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
        // Items with an image show the check as a frame (above); plain ones get a check mark.
        if (e.Item.Image != null) return;
        int s = Scale(e.ToolStrip!);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        assets().Font.Draw(g, "✔", new McStyle(0x55FF55), 6, (e.Item.Height / s - 8) / 2f);
        g.Restore(state);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int s = Scale(e.ToolStrip!);
        using var line = new SolidBrush(Color.FromArgb(0x50, 0x28, 0x00, 0x7F));
        e.Graphics.FillRectangle(line, new Rectangle(4 * s, e.Item.Height / 2 - s / 2, e.Item.Width - 8 * s, s));
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        int s = Scale(e.Item!.Owner!);
        var g = e.Graphics;
        var state = g.Save();
        PixelArt(g, s);
        assets().Font.Draw(g, ">", McStyle.White, (float)e.ArrowRectangle.X / s, (e.Item!.Height / s - 8) / 2f);
        g.Restore(state);
    }
}
