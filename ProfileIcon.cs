namespace McServerTray;

/// Where a profile's picture comes from: an image of its own, or game textures to try in order
/// (looked up in the active resource packs, so they match the rest of the look).
sealed record ProfileIcon(string? File, string? DataUri, IReadOnlyList<string> Textures)
{
    public static readonly ProfileIcon Default = FromTextures("block/grass_block_side.png");

    static ProfileIcon FromTextures(params string[] textures) => new(null, null, textures);

    /// Prism: a custom icon in its icons folder, else one of its built-in icons mapped to a similar texture.
    public static ProfileIcon Prism(string prismRoot, string? key)
    {
        if (string.IsNullOrEmpty(key)) return Default;
        string icons = Path.Combine(prismRoot, "icons");
        if (Directory.Exists(icons))
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico" })
                if (System.IO.File.Exists(Path.Combine(icons, key + ext)))
                    return new ProfileIcon(Path.Combine(icons, key + ext), null, Default.Textures);

        return key switch
        {
            "brick" => FromTextures("block/bricks.png"),
            "diamond" => FromTextures("item/diamond.png"),
            "dirt" => FromTextures("block/dirt.png"),
            "enderpearl" => FromTextures("item/ender_pearl.png"),
            "flame" => FromTextures("item/blaze_powder.png"),
            "gold" => FromTextures("item/gold_ingot.png"),
            "iron" => FromTextures("item/iron_ingot.png"),
            "meat" => FromTextures("item/cooked_beef.png"),
            "netherstar" => FromTextures("item/nether_star.png"),
            "planks" => FromTextures("block/oak_planks.png"),
            "stone" => FromTextures("block/stone.png"),
            "tnt" => FromTextures("block/tnt_side.png"),
            "chicken" => FromTextures("item/chicken.png"),
            "skeleton" => FromTextures("item/bone.png"),
            "creeper" or "squarecreeper" => FromTextures("item/gunpowder.png"),
            _ => Default,
        };
    }

    /// Official launcher: a data: URI, or a block name like "Crafting_Table" or "Leaves_Oak".
    public static ProfileIcon Vanilla(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return Default;
        if (icon.StartsWith("data:")) return new ProfileIcon(null, icon, Default.Textures);

        string name = icon.ToLowerInvariant();
        var candidates = new List<string>();
        switch (name)
        {
            case "grass": candidates.Add("block/grass_block_side.png"); break;
            case "brick": candidates.Add("block/bricks.png"); break;
            case "tnt": candidates.Add("block/tnt_side.png"); break;
            case "dirt_podzol": candidates.Add("block/podzol_side.png"); break;
        }
        // "Leaves_Oak" -> oak_leaves, "Log_Birch" -> birch_log, "Planks_Spruce" -> spruce_planks.
        if (name.Split('_') is [var kind, var wood] && kind is "leaves" or "log" or "planks")
            candidates.Add($"block/{wood}_{kind}.png");
        foreach (string suffix in new[] { "", "_front", "_side", "_top" })
            candidates.Add($"block/{name}{suffix}.png");
        candidates.Add($"item/{name}.png");
        candidates.AddRange(Default.Textures);
        return new ProfileIcon(null, null, candidates);
    }
}
