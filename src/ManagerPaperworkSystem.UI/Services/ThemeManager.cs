using System.Windows;
using System.Windows.Media;

// UI project enables WinForms for the Windows ColorDialog which brings in System.Drawing.Color.
// That makes the short name "Color" ambiguous in WPF code. Use an explicit alias for Media Color.
using MediaColor = System.Windows.Media.Color;

namespace ManagerPaperworkSystem.UI.Services;

public static class ThemeManager
{
    public static readonly IReadOnlyDictionary<string, MediaColor> Accents = new Dictionary<string, MediaColor>(StringComparer.OrdinalIgnoreCase)
    {
        ["NeonGreen"] = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#39FF14"),
        ["Cyan"] = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#00E5FF"),
        ["Purple"] = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#B46CFF"),
        ["Orange"] = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#FFB020"),
        ["Pink"] = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#FF4DCA"),
    };

    public static void ApplyAccent(string accentKey)
    {
        if (string.IsNullOrWhiteSpace(accentKey))
        {
            ApplyColor(Accents["NeonGreen"]);
            return;
        }

        // Persisted custom values:
        //  - "Custom:#AARRGGBB"
        //  - "CustomGradient:#AARRGGBB|#AARRGGBB"
        if (accentKey.StartsWith("CustomGradient:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = accentKey.Substring("CustomGradient:".Length);
            var parts = payload.Split('|');
            if (parts.Length == 2
                && TryParseColor(parts[0], out var c1)
                && TryParseColor(parts[1], out var c2))
            {
                ApplyGradient(c1, c2);
                return;
            }
        }

        if (accentKey.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = accentKey.Substring("Custom:".Length);
            if (TryParseColor(payload, out var cc))
            {
                ApplyColor(cc);
                return;
            }
        }

        if (!Accents.TryGetValue(accentKey, out var c))
            c = Accents["NeonGreen"];

        ApplyColor(c);
    }

    public static void ApplyGradient(MediaColor top, MediaColor bottom)
    {
        // Primary accent (for borders/text highlights) is the average.
        var avg = MediaColor.FromArgb(255,
            (byte)((top.R + bottom.R) / 2),
            (byte)((top.G + bottom.G) / 2),
            (byte)((top.B + bottom.B) / 2));

        ApplyColor(avg);

        // Replace the accent button brush with the chosen gradient.
        var res = System.Windows.Application.Current?.Resources;
        if (res is null) return;
        try
        {
            var g = new LinearGradientBrush(top, bottom, 90);
            g.Freeze();
            res["AccentButtonBrush"] = g;
        }
        catch { }
    }

    private static bool TryParseColor(string s, out MediaColor c)
    {
        try
        {
            if (!s.StartsWith("#")) s = "#" + s;
            c = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(s);
            return true;
        }
        catch
        {
            c = default;
            return false;
        }
    }

    public static void ApplyColor(MediaColor c)
    {
        // IMPORTANT:
        // Most of the UI templates reference NeonBrush/AccentBrush via DynamicResource.
        // To guarantee live theme switching, always REPLACE the brush resources
        // (mutating an existing brush often fails because it can be frozen).
        var res = System.Windows.Application.Current?.Resources;
        if (res is null) return;

        ReplaceBrush(res, "NeonBrush", c);
        ReplaceBrush(res, "AccentBrush", c);

        // Buttons use a 3D gradient; replace the gradient brush too so all green UI adopts the selected accent.
        ReplaceAccentButtonBrush(res, "AccentButtonBrush", c);

        // Input field backgrounds: a very light tint of the accent
        var field = Adjust(c, 0.92);
        ReplaceBrush(res, "FieldBrush", MediaColor.FromArgb(255, field.R, field.G, field.B));

        // Neutral button fills: subtle tint so the theme is visible across all buttons
        ReplaceBrush(res, "ButtonFill", MediaColor.FromArgb(30, c.R, c.G, c.B));
        // Accent button text should contrast with the chosen accent.
        var lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        var btnText = lum > 0.6 ? (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#071018")
                               : (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFFFF");
        ReplaceBrush(res, "AccentButtonTextBrush", btnText);


        // IMPORTANT:
        // Do NOT override WindowText/ControlText system brushes with the accent color.
        // WPF uses those keys for ComboBox/Menu/Calendar text and overriding them makes text unreadable
        // on dark backgrounds (users reported "fonts blending in").
        // Keep the accent only for selection/highlight.
        ReplaceBrush(res, System.Windows.SystemColors.HighlightBrushKey, c);
        ReplaceBrush(res, System.Windows.SystemColors.HighlightTextBrushKey, (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#071018"));
    }

    private static void ReplaceBrush(ResourceDictionary res, object key, MediaColor color)
    {
        try
        {
            // Always replace so DynamicResource updates everywhere.
            var b = new SolidColorBrush(color);
            b.Freeze(); // safe to freeze; we will replace on the next theme change
            res[key] = b;
        }
        catch
        {
            // ignore
        }
    }

    private static void ReplaceAccentButtonBrush(ResourceDictionary res, object key, MediaColor baseColor)
    {
        try
        {
            // Create a simple 3D gradient based on the accent:
            // top = slightly lighter, bottom = slightly darker.
            var top = Adjust(baseColor, 0.18);
            var bottom = Adjust(baseColor, -0.18);

            var g = new LinearGradientBrush(top, bottom, 90);
            g.Freeze();
            res[key] = g;
        }
        catch
        {
            // ignore
        }
    }

    private static MediaColor Adjust(MediaColor c, double delta)
    {
        // delta in [-1,1], positive => lighten
        byte adj(byte v)
        {
            var d = delta >= 0 ? (255 - v) * delta : v * delta;
            var nv = (int)Math.Round(v + d);
            return (byte)Math.Max(0, Math.Min(255, nv));
        }

        return MediaColor.FromArgb(c.A, adj(c.R), adj(c.G), adj(c.B));
    }
}
