namespace McServerTray;

/// A block drawn as an isometric cube, like the launchers' profile icons. Each face lists texture
/// candidates (first one the active packs have wins); tints colour the grey grass/leaf textures.
sealed record IconCube(IReadOnlyList<string> Top, IReadOnlyList<string> Front, IReadOnlyList<string> Side, int? TopTint = null, int? SideTint = null)
{
    public static IconCube Block(string name, int? topTint = null, int? sideTint = null) => new(
        [$"block/{name}_top.png", $"block/{name}.png"],
        [$"block/{name}_front.png", $"block/{name}_side.png", $"block/{name}.png"],
        [$"block/{name}_side.png", $"block/{name}.png"],
        topTint, sideTint);
}

/// Where a profile's picture comes from: an image of its own, a block cube or a flat item texture
/// (looked up in the active resource packs, so they match the rest of the look).
sealed record ProfileIcon(string? File, string? DataUri, IconCube? Cube, string? Flat)
{
    /// Plains biome colours, which the game applies to the grey grass and leaf textures.
    const int GrassTint = 0x91BD59;
    const int FoliageTint = 0x77AB2F;

    static readonly IconCube Grass = IconCube.Block("grass_block", topTint: GrassTint);
    public static readonly ProfileIcon Default = new(null, null, Grass, null);

    static ProfileIcon OfBlock(IconCube cube) => new(null, null, cube, null);
    static ProfileIcon OfBlock(string block) => OfBlock(IconCube.Block(block));
    static ProfileIcon Item(string item) => new(null, null, Grass, $"item/{item}.png");

    /// Prism: a custom icon in its icons folder, else one of its built-in icons mapped to a similar block or item.
    public static ProfileIcon Prism(string prismRoot, string? key)
    {
        if (string.IsNullOrEmpty(key)) return Default;
        string icons = Path.Combine(prismRoot, "icons");
        if (Directory.Exists(icons))
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico" })
                if (System.IO.File.Exists(Path.Combine(icons, key + ext)))
                    return new ProfileIcon(Path.Combine(icons, key + ext), null, Grass, null);

        // Built-in keys sometimes carry a "_legacy" suffix for the older artwork.
        return key.Replace("_legacy", "") switch
        {
            "bee" => OfBlock("bee_nest"),
            "brick" => OfBlock("bricks"),
            "dirt" => OfBlock("dirt"),
            "planks" => OfBlock("oak_planks"),
            "stone" => OfBlock("stone"),
            "tnt" => OfBlock("tnt"),
            "diamond" => Item("diamond"),
            "enderpearl" => Item("ender_pearl"),
            "flame" => Item("blaze_powder"),
            "gold" => Item("gold_ingot"),
            "iron" => Item("iron_ingot"),
            "meat" => Item("cooked_beef"),
            "netherstar" => Item("nether_star"),
            "chicken" => Item("chicken"),
            "skeleton" => Item("bone"),
            "creeper" or "squarecreeper" => Item("gunpowder"),
            _ => Default,
        };
    }

    /// Official launcher: a data: URI, or a block name like "Crafting_Table" or "Leaves_Oak".
    public static ProfileIcon Vanilla(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return Default;
        if (icon.StartsWith("data:")) return new ProfileIcon(null, icon, Grass, null);

        string name = icon.ToLowerInvariant();
        return name switch
        {
            "grass" => Default,
            "brick" => OfBlock("bricks"),
            "dirt_podzol" => OfBlock(IconCube.Block("podzol")),
            // "Leaves_Oak" -> oak_leaves (grey, tinted like the game), "Log_Birch" -> birch_log, "Planks_Spruce" -> spruce_planks.
            _ when name.Split('_') is ["leaves", var wood] => OfBlock(IconCube.Block($"{wood}_leaves", FoliageTint, FoliageTint)),
            _ when name.Split('_') is [var kind and ("log" or "planks"), var wood] => OfBlock($"{wood}_{kind}"),
            _ => OfBlock(name),
        };
    }
}
