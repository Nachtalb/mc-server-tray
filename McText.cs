using System.Globalization;
using System.Text;
using System.Text.Json;

namespace McServerTray;

readonly record struct McStyle(int Color, bool Bold = false, bool Italic = false, bool Underline = false, bool Strikethrough = false, bool Obfuscated = false)
{
    public static McStyle White => new(0xFFFFFF);
    public static McStyle Gray => new(0x808080);
}

readonly record struct StyledChar(int CodePoint, McStyle Style);

/// Turns chat components (JSON or legacy §-codes) into styled characters.
static class McText
{
    static readonly int[] Palette =
    [
        0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xFFAA00, 0xAAAAAA,
        0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
    ];

    static readonly string[] ColorNames =
    [
        "black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple", "gold", "gray",
        "dark_gray", "blue", "green", "aqua", "red", "light_purple", "yellow", "white",
    ];

    public const int DarkRed = 0xAA0000;
    public const int DarkGray = 0x555555;

    public static List<StyledChar> Plain(string text, McStyle style) =>
        text.EnumerateRunes().Select(r => new StyledChar(r.Value, style)).ToList();

    /// Parses a status "description" (raw JSON: a string or a component object/array).
    public static List<StyledChar> FromJson(string? rawJson, McStyle baseStyle)
    {
        var result = new List<StyledChar>();
        if (string.IsNullOrEmpty(rawJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            Append(doc.RootElement, baseStyle, baseStyle, result);
        }
        catch (JsonException)
        {
            result.AddRange(Legacy(rawJson, baseStyle, baseStyle));
        }
        return result;
    }

    static void Append(JsonElement e, McStyle style, McStyle root, List<StyledChar> output)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                output.AddRange(Legacy(e.GetString()!, style, root));
                break;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                output.AddRange(Legacy(e.GetRawText(), style, root));
                break;
            case JsonValueKind.Array:
                foreach (var child in e.EnumerateArray()) Append(child, style, root, output);
                break;
            case JsonValueKind.Object:
                style = ApplyStyle(e, style);
                string text =
                    e.TryGetProperty("text", out var t) ? t.ToString() :
                    e.TryGetProperty("fallback", out var f) ? f.ToString() :
                    e.TryGetProperty("translate", out var tr) ? tr.ToString() : "";
                output.AddRange(Legacy(text, style, root));
                if (e.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                    foreach (var child in extra.EnumerateArray()) Append(child, style, root, output);
                break;
        }
    }

    static McStyle ApplyStyle(JsonElement e, McStyle s)
    {
        if (e.TryGetProperty("color", out var c) && c.GetString() is { } color && ParseColor(color) is { } rgb)
            s = s with { Color = rgb };
        bool Flag(string name, bool current) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True : current;
        return s with
        {
            Bold = Flag("bold", s.Bold),
            Italic = Flag("italic", s.Italic),
            Underline = Flag("underlined", s.Underline),
            Strikethrough = Flag("strikethrough", s.Strikethrough),
            Obfuscated = Flag("obfuscated", s.Obfuscated),
        };
    }

    static int? ParseColor(string color)
    {
        if (color.StartsWith('#') && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, null, out int hex))
            return hex & 0xFFFFFF;
        int i = Array.IndexOf(ColorNames, color);
        return i >= 0 ? Palette[i] : null;
    }

    /// Legacy formatting: §0-§f set a colour (and clear styles), §k-§o add a style, §r resets.
    static IEnumerable<StyledChar> Legacy(string text, McStyle style, McStyle root)
    {
        var runes = text.EnumerateRunes().ToList();
        for (int i = 0; i < runes.Count; i++)
        {
            if (runes[i].Value == '§' && i + 1 < runes.Count)
            {
                char code = char.ToLowerInvariant((char)runes[++i].Value);
                int idx = "0123456789abcdef".IndexOf(code);
                style = idx >= 0 ? root with { Color = Palette[idx], Bold = false, Italic = false, Underline = false, Strikethrough = false, Obfuscated = false } : code switch
                {
                    'k' => style with { Obfuscated = true },
                    'l' => style with { Bold = true },
                    'm' => style with { Strikethrough = true },
                    'n' => style with { Underline = true },
                    'o' => style with { Italic = true },
                    'r' => root,
                    _ => style,
                };
                continue;
            }
            yield return new StyledChar(runes[i].Value, style);
        }
    }

    public static string StripCodes(IEnumerable<StyledChar> text)
    {
        var sb = new StringBuilder();
        foreach (var c in text) sb.Append(char.ConvertFromUtf32(c.CodePoint));
        return sb.ToString();
    }
}
