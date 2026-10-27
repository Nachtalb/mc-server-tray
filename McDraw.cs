using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace McServerTray;

/// Pixel-art drawing helpers shared by the popup and the tray menu. Coordinates are whatever the
/// Graphics transform says (GUI pixels in both places).
static class McDraw
{
    /// Clamps sampling to the source rectangle so nearest-neighbour edges don't bleed.
    public static readonly ImageAttributes Clamp = CreateClamp();

    static ImageAttributes CreateClamp()
    {
        var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        return attrs;
    }

    /// Sets a Graphics up for crisp pixel art at the given GUI scale.
    public static void PixelArt(Graphics g, float scale)
    {
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        g.ScaleTransform(scale, scale);
    }

    public static void TileImage(Graphics g, Bitmap img, Rectangle area, float tileW, float tileH, ImageAttributes attrs)
    {
        var state = g.Save();
        g.SetClip(area);
        for (float ty = area.Top; ty < area.Bottom; ty += tileH)
            for (float tx = area.Left; tx < area.Right; tx += tileW)
                g.DrawImage(img, [new PointF(tx, ty), new PointF(tx + tileW, ty), new PointF(tx, ty + tileH)],
                    new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, attrs);
        g.Restore(state);
    }

    /// Draws a GUI sprite into dest, honouring its nine-slice .mcmeta (tiling or stretching the inside).
    public static void DrawSprite(Graphics g, Sprite sprite, RectangleF dest)
    {
        var img = sprite.Image;
        if (sprite.Slice is not { } s)
        {
            Blit(g, img, dest);
            return;
        }

        // Texels per GUI pixel; resource packs often ship sprites at 2x or 4x.
        float kx = (float)img.Width / s.Width, ky = (float)img.Height / s.Height;
        float l = Math.Min(s.Left, dest.Width / 2), r = Math.Min(s.Right, dest.Width / 2);
        float t = Math.Min(s.Top, dest.Height / 2), b = Math.Min(s.Bottom, dest.Height / 2);

        float[] gx = [0, s.Left, s.Width - s.Right, s.Width];
        float[] gy = [0, s.Top, s.Height - s.Bottom, s.Height];
        float[] dx = [dest.Left, dest.Left + l, dest.Right - r, dest.Right];
        float[] dy = [dest.Top, dest.Top + t, dest.Bottom - b, dest.Bottom];

        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                var d = new RectangleF(dx[i], dy[j], dx[i + 1] - dx[i], dy[j + 1] - dy[j]);
                var src = new RectangleF(gx[i] * kx, gy[j] * ky, (gx[i + 1] - gx[i]) * kx, (gy[j + 1] - gy[j]) * ky);
                if (d.Width <= 0 || d.Height <= 0 || src.Width <= 0 || src.Height <= 0) continue;

                bool corner = i != 1 && j != 1;
                if (corner || s.StretchInner)
                {
                    Blit(g, img, d, src);
                    continue;
                }

                // Edges and centre repeat at their native size, cut off at the end.
                float tw = i == 1 ? gx[2] - gx[1] : d.Width, th = j == 1 ? gy[2] - gy[1] : d.Height;
                var state = g.Save();
                g.SetClip(d, CombineMode.Intersect);
                for (float ty = d.Top; ty < d.Bottom; ty += th)
                    for (float tx = d.Left; tx < d.Right; tx += tw)
                        Blit(g, img, new RectangleF(tx, ty, tw, th), src);
                g.Restore(state);
            }
        }
    }

    public static void Blit(Graphics g, Bitmap tex, RectangleF dest) => Blit(g, tex, dest, new RectangleF(0, 0, tex.Width, tex.Height));

    public static void Blit(Graphics g, Bitmap tex, RectangleF dest, RectangleF src) =>
        g.DrawImage(tex, [dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom)], src, GraphicsUnit.Pixel, Clamp);

    /// An isometric block like the launchers draw: lit top, front face on the left, side on the right,
    /// each face shaded and optionally tinted (grass and leaves are grey in the texture).
    public static Bitmap IsoCube(Bitmap top, Bitmap front, Bitmap side, int? topTint, int? sideTint, int size = 64)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        float s = size, q = size / 4f, h = size / 2f;

        void Face(Bitmap tex, PointF topLeft, PointF topRight, PointF bottomLeft, float shade, int? tint)
        {
            float r = shade, gr = shade, b = shade;
            if (tint is int t)
            {
                r *= (t >> 16 & 0xFF) / 255f;
                gr *= (t >> 8 & 0xFF) / 255f;
                b *= (t & 0xFF) / 255f;
            }
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            attrs.SetColorMatrix(new ColorMatrix(
            [
                [r, 0, 0, 0, 0],
                [0, gr, 0, 0, 0],
                [0, 0, b, 0, 0],
                [0, 0, 0, 1, 0],
                [0, 0, 0, 0, 1],
            ]));
            g.DrawImage(tex, [topLeft, topRight, bottomLeft], new RectangleF(0, 0, tex.Width, tex.Height), GraphicsUnit.Pixel, attrs);
        }

        Face(top, new PointF(0, q), new PointF(h, 0), new PointF(h, h), 1f, topTint);
        Face(front, new PointF(0, q), new PointF(h, h), new PointF(0, s - q), 0.8f, sideTint);
        Face(side, new PointF(h, h), new PointF(s, q), new PointF(h, s), 0.6f, sideTint);
        return bmp;
    }

    public static void Fill(Graphics g, Rectangle r, int rgb)
    {
        using var b = new SolidBrush(Color.FromArgb(255, Color.FromArgb(rgb)));
        g.FillRectangle(b, r);
    }
}
