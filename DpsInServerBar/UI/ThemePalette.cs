using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;

namespace DpsInServerBar.UI;

public enum ThemePreset
{
    Midnight,
    Crimson,
    Emerald,
    Aether,
    Slate,
    Custom,

    /// <summary>Frost: icy blue and lavender (after Custom: themes are saved by number).</summary>
    Frost,
}

/// <summary>
/// The colors a theme is made of. The other shades (hover, borders, muted text...) are derived from them.
/// Saved in the configuration for the custom theme.
/// </summary>
[Serializable]
public sealed class ThemePalette
{
    public Vector4 Background { get; set; }
    public Vector4 Surface { get; set; }
    public Vector4 Accent { get; set; }
    public Vector4 Secondary { get; set; }
    public Vector4 Text { get; set; }
    public Vector4 Good { get; set; }
    public Vector4 Warn { get; set; }
    public Vector4 Bad { get; set; }
    public Vector4 Tank { get; set; }
    public Vector4 Healer { get; set; }
    public Vector4 Dps { get; set; }

    public ThemePalette Copy() => (ThemePalette)MemberwiseClone();

    /// <summary>The editable colors, with their label, for the settings.</summary>
    public IEnumerable<(string Label, Func<Vector4> Get, Action<Vector4> Set)> Entries()
    {
        yield return (Loc.T("Background"), () => Background, v => Background = v);
        yield return (Loc.T("Surface (fields, cards)"), () => Surface, v => Surface = v);
        yield return (Loc.T("Accent"), () => Accent, v => Accent = v);
        yield return (Loc.T("Secondary accent"), () => Secondary, v => Secondary = v);
        yield return (Loc.T("Text"), () => Text, v => Text = v);
        yield return (Loc.T("Good"), () => Good, v => Good = v);
        yield return (Loc.T("Warning"), () => Warn, v => Warn = v);
        yield return (Loc.T("Bad"), () => Bad, v => Bad = v);
        yield return (Loc.T("Tank bars"), () => Tank, v => Tank = v);
        yield return (Loc.T("Healer bars"), () => Healer, v => Healer = v);
        yield return (Loc.T("DPS bars"), () => Dps, v => Dps = v);
    }

    public static ThemePalette For(ThemePreset preset) => preset switch
    {
        ThemePreset.Crimson => new ThemePalette
        {
            Background = Rgb(0x16, 0x0B, 0x0E), Surface = Rgb(0x2A, 0x17, 0x1C), Accent = Rgb(0xE0, 0x9A, 0x86),
            Secondary = Rgb(0xD9, 0x5C, 0x6E), Text = Rgb(0xEE, 0xE2, 0xDF),
        }.WithStatus(),
        ThemePreset.Emerald => new ThemePalette
        {
            Background = Rgb(0x0A, 0x14, 0x11), Surface = Rgb(0x16, 0x26, 0x21), Accent = Rgb(0xC9, 0xB8, 0x6B),
            Secondary = Rgb(0x5F, 0xC9, 0x9B), Text = Rgb(0xE2, 0xE8, 0xDF),
        }.WithStatus(),
        ThemePreset.Aether => new ThemePalette
        {
            Background = Rgb(0x10, 0x0C, 0x1C), Surface = Rgb(0x20, 0x1A, 0x33), Accent = Rgb(0x7F, 0xD8, 0xE8),
            Secondary = Rgb(0xB0, 0x8C, 0xF0), Text = Rgb(0xE6, 0xE2, 0xF2),
        }.WithStatus(),
        ThemePreset.Frost => new ThemePalette
        {
            Background = Rgb(0x10, 0x14, 0x20), Surface = Rgb(0x34, 0x3E, 0x56), Accent = Rgb(0x9C, 0xD6, 0xFF),
            Secondary = Rgb(0xC6, 0xB4, 0xFF), Text = Rgb(0xF3, 0xF6, 0xFB),
        }.WithStatus(),
        ThemePreset.Slate => new ThemePalette
        {
            Background = Rgb(0x14, 0x15, 0x18), Surface = Rgb(0x24, 0x26, 0x2B), Accent = Rgb(0x6F, 0xA8, 0xF0),
            Secondary = Rgb(0xA0, 0xA8, 0xB8), Text = Rgb(0xE4, 0xE6, 0xEA),
        }.WithStatus(),
        _ => new ThemePalette
        {
            Background = Rgb(0x0B, 0x0F, 0x18), Surface = Rgb(0x1B, 0x23, 0x33), Accent = Rgb(0xD6, 0xB0, 0x68),
            Secondary = Rgb(0x6F, 0xB8, 0xE0), Text = Rgb(0xE7, 0xE1, 0xD3),
        }.WithStatus(),
    };

    public static string Name(ThemePreset preset) => preset switch
    {
        ThemePreset.Midnight => Loc.T("Midnight (blue & gold)"),
        ThemePreset.Crimson => Loc.T("Crimson (wine & rose gold)"),
        ThemePreset.Emerald => Loc.T("Emerald (forest & gold)"),
        ThemePreset.Aether => Loc.T("Aether (violet & cyan)"),
        ThemePreset.Slate => Loc.T("Slate (grey & blue)"),
        ThemePreset.Frost => Loc.T("Frost (icy blue & lavender)"),
        _ => Loc.T("Custom"),
    };

    // Status and role colors shared by every preset.
    private ThemePalette WithStatus()
    {
        Good = Rgb(0x7C, 0xD3, 0x8B);
        Warn = Rgb(0xE8, 0xB8, 0x4B);
        Bad = Rgb(0xE4, 0x69, 0x5F);
        Tank = Rgb(0x5B, 0x8F, 0xE0);
        Healer = Rgb(0x5F, 0xC2, 0x7A);
        Dps = Rgb(0xD9, 0x6A, 0x6A);
        return this;
    }

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    // ------------------------------------------------------------------ Share codes

    public const string CodePrefix = "RCTHEME1:";

    private sealed record Code(string N, string[] C);

    /// <summary>A text code holding a theme (its name and colors), for another player to import.</summary>
    public string Export(string name)
    {
        static string Hex(Vector4 c) =>
            string.Concat(new[] { c.X, c.Y, c.Z, c.W }.Select(v => ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2")));
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Code(name, Entries().Select(e => Hex(e.Get())).ToArray()));
        using var output = new System.IO.MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
            deflate.Write(json);
        return CodePrefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>The theme held by a code (its name and colors), null when the text is not a theme code.</summary>
    public static SavedTheme? Import(string code)
    {
        code = code.Trim();
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
            return null;

        try
        {
            using var input = new System.IO.Compression.DeflateStream(new System.IO.MemoryStream(Convert.FromBase64String(code[CodePrefix.Length..])),
                                                                       System.IO.Compression.CompressionMode.Decompress);
            using var json = new System.IO.MemoryStream();
            input.CopyTo(json);
            var data = System.Text.Json.JsonSerializer.Deserialize<Code>(json.ToArray());
            if (data is null)
                return null;

            var palette = For(ThemePreset.Midnight);
            var entries = palette.Entries().ToList();
            for (var i = 0; i < entries.Count && i < data.C.Length; i++)
            {
                var hex = data.C[i];
                if (hex.Length != 8)
                    return null;
                entries[i].Set(new Vector4(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16),
                                           Convert.ToInt32(hex[4..6], 16), Convert.ToInt32(hex[6..], 16)) / 255f);
            }

            return new SavedTheme { Name = data.N.Trim().Length > 0 ? data.N.Trim() : "Shared theme", Palette = palette };
        }
        catch (Exception e) when (e is FormatException or System.IO.InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>A custom theme saved by the user under a name.</summary>
[Serializable]
public sealed class SavedTheme
{
    public string Name { get; set; } = string.Empty;
    public ThemePalette Palette { get; set; } = ThemePalette.For(ThemePreset.Midnight);
}
