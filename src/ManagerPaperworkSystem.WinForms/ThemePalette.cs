namespace ManagerPaperworkSystem.WinForms;

/// <summary>Named palettes use the same roles as the approved ledger previews.</summary>
internal sealed record ThemePalette(
    string Name, bool IsDark, Color Navigation, Color Background, Color Surface, Color Alternate,
    Color Text, Color Muted, Color Border, Color Accent, Color OnAccent, Color Header,
    Color OnHeader, Color Selection, Color OnSelection, Color Input, Color Positive, Color Negative)
{
    private static Color Hex(string value) => ColorTranslator.FromHtml("#" + value);
    private static ThemePalette Create(string name, bool dark, params string[] colors)
    {
        var c = colors.Select(Hex).ToArray();
        return new(name, dark, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13], c[14], c[15]);
    }
    public static IReadOnlyList<ThemePalette> Named { get; } = Array.AsReadOnly(new[]
    {
        Create("Classic Blue & Orange", false, "163a5f", "ffffff", "ffffff", "f3f6fa", "16324f", "53677c", "ced8e4", "f28c28", "30200b", "163a5f", "ffffff", "e4efff", "16324f", "ffffff", "16733b", "b33030"),
        Create("Graphite & Orange", true, "163a5f", "1e2126", "262b32", "2d333b", "edf0f5", "b4bfce", "485361", "f28c28", "30200b", "303a48", "f2f5fa", "344663", "ffffff", "1e242c", "75d8a0", "ff9797"),
        Create("Midnight & Gold", true, "101e33", "15243a", "1c2e47", "223650", "eef3fb", "b1c2d8", "405570", "e8be6c", "2d230e", "263e5b", "f4f7fc", "364b64", "ffffff", "15243a", "79dcaa", "ffaaa4"),
        Create("Teal & Ivory", false, "164e50", "f6f5ef", "fffefa", "eef3ed", "203d3e", "536c69", "ccd8d1", "16726a", "ffffff", "e2ece5", "264743", "d5e9e3", "203d3e", "fffefa", "17643c", "af3035"),
        Create("Slate & Blue", false, "25364d", "f2f5f9", "ffffff", "f4f7fb", "243449", "53657b", "d0d9e5", "245cba", "ffffff", "e6ecf5", "253b59", "dce8fc", "203e70", "ffffff", "18743c", "b02d36"),
        Create("Charcoal & Emerald", true, "162426", "1c2528", "243135", "2b3a3e", "eef4f3", "b3c5c5", "43585c", "66ceab", "102c22", "304449", "f0f7f5", "35534d", "f5fffb", "1b282b", "89e4ac", "ffaaa9")
    });
}
