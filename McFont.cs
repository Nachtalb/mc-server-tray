using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace McServerTray;

/// Renders text the way Minecraft does: the vanilla bitmap glyphs, 1px drop shadow,
/// and the bold/italic/underline/strikethrough/obfuscated styles. Coordinates are GUI pixels;
/// the caller scales the Graphics to the GUI scale.
sealed class McFont
{
    public sealed record BitmapProvider(Bitmap Image, int Height, int Ascent, string[] Rows);

    sealed class Glyph(RectangleF[] pixels, float advance)
    {
        public RectangleF[] Pixels { get; } = pixels;
        public float Advance { get; } = advance;
    }

    public const int LineHeight = 9;

    readonly Dictionary<int, Glyph> _glyphs = new();
    readonly Dictionary<float, Glyph[]> _byAdvance = new();
    static readonly Font GdiFont = new("Segoe UI", 8f, GraphicsUnit.Pixel);
    static readonly Graphics Measurer = Graphics.FromImage(new Bitmap(1, 1));
    readonly Random _random = new();

    public McFont(IEnumerable<BitmapProvider> providers)
    {
        foreach (var provider in providers)
            AddProvider(provider);

        // Obfuscated text swaps every glyph for a random one of the same width.
        foreach (var group in _glyphs.Where(kv => kv.Key is > 32 and < 127).GroupBy(kv => kv.Value.Advance))
            _byAdvance[group.Key] = group.Select(kv => kv.Value).ToArray();
    }

    public static McFont Fallback() => new([]);

    void AddProvider(BitmapProvider p)
    {
        int rows = p.Rows.Length;
        int cols = p.Rows.Max(r => r.EnumerateRunes().Count());
        int cellW = p.Image.Width / cols, cellH = p.Image.Height / rows;
        float scale = (float)p.Height / cellH;
        int top = 7 - p.Ascent;

        var data = p.Image.LockBits(new Rectangle(0, 0, p.Image.Width, p.Image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var argb = new int[data.Width * data.Height];
        Marshal.Copy(data.Scan0, argb, 0, argb.Length);
        p.Image.UnlockBits(data);
        bool Opaque(int x, int y) => (argb[y * data.Width + x] >>> 24) > 127;

        for (int row = 0; row < rows; row++)
        {
            int col = 0;
            foreach (var rune in p.Rows[row].EnumerateRunes())
            {
                int cp = rune.Value, cx = col * cellW, cy = row * cellH;
                col++;
                if (cp == 0 || cp == ' ' || _glyphs.ContainsKey(cp)) continue;

                int width = 0;
                for (int x = cellW - 1; x >= 0 && width == 0; x--)
                    for (int y = 0; y < cellH; y++)
                        if (Opaque(cx + x, cy + y)) { width = x + 1; break; }

                var pixels = new List<RectangleF>();
                for (int y = 0; y < cellH; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (!Opaque(cx + x, cy + y)) continue;
                        int start = x;
                        while (x + 1 < width && Opaque(cx + x + 1, cy + y)) x++;
                        pixels.Add(new RectangleF(start * scale, top + y * scale, (x - start + 1) * scale, scale));
                    }
                }
                _glyphs[cp] = new Glyph(pixels.ToArray(), (int)(0.5f + width * scale) + 1);
            }
        }
    }

    float Advance(int cp, McStyle style)
    {
        float adv = cp == ' ' ? 4 : _glyphs.TryGetValue(cp, out var g) ? g.Advance : GdiAdvance(cp);
        return adv + (style.Bold ? 1 : 0);
    }

    static float GdiAdvance(int cp) =>
        MathF.Ceiling(Measurer.MeasureString(char.ConvertFromUtf32(cp), GdiFont, PointF.Empty, StringFormat.GenericTypographic).Width) + 1;

    public float Width(IEnumerable<StyledChar> text) => text.Sum(c => Advance(c.CodePoint, c.Style));

    public float Width(string plain) => Width(McText.Plain(plain, McStyle.White));

    /// Greedy word wrap, like Font.split in the game.
    public List<List<StyledChar>> Wrap(IReadOnlyList<StyledChar> text, float maxWidth)
    {
        var lines = new List<List<StyledChar>>();
        var line = new List<StyledChar>();
        float width = 0;
        int lastSpace = -1;

        foreach (var c in text)
        {
            if (c.CodePoint == '\n')
            {
                lines.Add(line);
                line = [];
                width = 0;
                lastSpace = -1;
                continue;
            }

            float adv = Advance(c.CodePoint, c.Style);
            if (width + adv > maxWidth && line.Count > 0)
            {
                List<StyledChar> rest;
                if (lastSpace >= 0)
                {
                    rest = line.GetRange(lastSpace + 1, line.Count - lastSpace - 1);
                    line.RemoveRange(lastSpace, line.Count - lastSpace);
                }
                else rest = [];
                lines.Add(line);
                line = rest;
                width = Width(line);
                lastSpace = -1;
                if (c.CodePoint == ' ') continue;
            }

            if (c.CodePoint == ' ') lastSpace = line.Count;
            line.Add(c);
            width += adv;
        }
        lines.Add(line);
        return lines;
    }

    public void Draw(Graphics g, IReadOnlyList<StyledChar> text, float x, float y, bool shadow = true)
    {
        // The randomised glyphs must match between the shadow and the main pass.
        var obfuscated = new Glyph?[text.Count];
        for (int i = 0; i < text.Count; i++)
            if (text[i].Style.Obfuscated && _glyphs.TryGetValue(text[i].CodePoint, out var gl) && _byAdvance.TryGetValue(gl.Advance, out var pool))
                obfuscated[i] = pool[_random.Next(pool.Length)];

        if (shadow) DrawPass(g, text, obfuscated, x + 1, y + 1, true);
        DrawPass(g, text, obfuscated, x, y, false);
    }

    public void Draw(Graphics g, string plain, McStyle style, float x, float y, bool shadow = true) =>
        Draw(g, McText.Plain(plain, style), x, y, shadow);

    void DrawPass(Graphics g, IReadOnlyList<StyledChar> text, Glyph?[] obfuscated, float x, float y, bool shadow)
    {
        for (int i = 0; i < text.Count; i++)
        {
            var (cp, style) = (text[i].CodePoint, text[i].Style);
            int rgb = shadow ? (style.Color & 0xFCFCFC) >> 2 : style.Color;
            using var brush = new SolidBrush(Color.FromArgb(255, Color.FromArgb(rgb)));
            float adv = Advance(cp, style);

            if (cp != ' ')
            {
                var glyph = obfuscated[i] ?? _glyphs.GetValueOrDefault(cp);
                if (glyph != null)
                {
                    DrawGlyph(g, brush, glyph, x, y, style.Italic);
                    if (style.Bold) DrawGlyph(g, brush, glyph, x + 1, y, style.Italic);
                }
                else
                {
                    string s = char.ConvertFromUtf32(cp);
                    g.DrawString(s, GdiFont, brush, x, y - 1, StringFormat.GenericTypographic);
                    if (style.Bold) g.DrawString(s, GdiFont, brush, x + 1, y - 1, StringFormat.GenericTypographic);
                }
            }

            if (style.Underline) g.FillRectangle(brush, x - 1, y + 8, adv + 1, 1);
            if (style.Strikethrough) g.FillRectangle(brush, x - 1, y + 3.5f, adv + 1, 1);
            x += adv;
        }
    }

    static void DrawGlyph(Graphics g, Brush brush, Glyph glyph, float x, float y, bool italic)
    {
        foreach (var px in glyph.Pixels)
        {
            float skew = italic ? (7 - px.Y) * 0.25f - 0.5f : 0;
            g.FillRectangle(brush, x + px.X + skew, y + px.Y, px.Width, px.Height);
        }
    }
}
