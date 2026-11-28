using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace DpsInServerBar.UI;

/// <summary>
/// The plugin's look: a dark translucent background, an accent color and rounded frames (midnight blue and gold by default, other themes in the Appearance settings).
/// Push it around a window (PreDraw / PostDraw) so the frame and title bar get it too.
/// </summary>
public static partial class Theme
{
    // Palette: the colors of the selected theme (Appearance settings), the shades derived from them.
    private static ThemePalette palette = ThemePalette.For(ThemePreset.Midnight);

    // A theme change: the colors pass from the old theme (from) to the new one (target), a light sweeping the windows.
    private static ThemePalette target = palette;
    private static ThemePalette? from;
    private static readonly ThemePalette Blend = new();
    private static string? paletteKey;
    private static float opacity = 0.96f;

    /// <summary>The glass effect, on any theme: see-through frosted windows, the game blurred behind them, light rims.</summary>
    public static bool Glass { get; private set; }

    private static float glassBlur = 8f;

    // Glass: how much of the window lets the (blurred) game through, and the white of its rims and frosted fields.
    private const float GlassWindow = 0.48f;
    private static readonly Vector4 Frost = new(1, 1, 1, 1);

    /// <summary>Window background, at the opacity chosen in the settings (Glass: never more than see-through).</summary>
    /// <summary>
    /// The background alpha of a window with an opacity of its own (compact meter, opener practice): as chosen, or with
    /// the glass effect, that share of the see-through glass (100%: the glass as the other windows have it).
    /// </summary>
    public static float WindowAlpha(float chosen) => Glass ? Math.Clamp(chosen, 0f, 1f) * GlassWindow : chosen;

    public static Vector4 Ink => palette.Background with { W = Glass ? Math.Min(opacity, GlassWindow) : opacity };
    public static Vector4 Panel => Mix(palette.Background, palette.Surface, 0.4f) with { W = Glass ? 0.55f : 1f };
    public static Vector4 Surface => palette.Surface;
    public static Vector4 SurfaceHover => Lighten(palette.Surface, 0.07f);
    public static Vector4 SurfaceActive => Lighten(palette.Surface, 0.13f);
    public static Vector4 Line => Lighten(palette.Surface, 0.1f);
    public static Vector4 LineSoft => Lighten(palette.Surface, 0.03f);

    public static Vector4 Gold => palette.Accent;
    public static Vector4 GoldBright => Lighten(palette.Accent, 0.4f);
    public static Vector4 GoldDim => Mix(palette.Accent, palette.Background, 0.45f);
    public static Vector4 Crystal => palette.Secondary;

    public static Vector4 Text => palette.Text;
    public static Vector4 TextMuted => Mix(palette.Text, palette.Surface, 0.4f);
    public static Vector4 TextFaint => Mix(palette.Text, palette.Surface, 0.62f);

    public static Vector4 Good => palette.Good;
    public static Vector4 Warn => palette.Warn;
    public static Vector4 Bad => palette.Bad;

    // Roles, for the damage bars of the player tables.
    public static Vector4 Tank => palette.Tank;
    public static Vector4 Healer => palette.Healer;
    public static Vector4 Dps => palette.Dps;

    /// <summary>Line colors of the graphs, in order.</summary>
    public static Vector4[] Series => [Gold, Crystal, Good, Mix(Crystal, Bad, 0.5f), Warn, TextMuted];

    /// <summary>Applies the theme and background opacity chosen in the settings.</summary>
    public static void Configure(Configuration config)
    {
        // Another theme picked (not a color edited): the colors pass to it.
        var key = $"{config.ThemePreset}:{config.SavedThemeIndex}";
        if (paletteKey is not null && key != paletteKey && config.Animations)
        {
            from = palette.Copy();
            ThemeSweep.Started = (float)ImGui.GetTime();
        }

        paletteKey = key;
        target = config.ActivePalette;
        palette = from is null ? target : palette;
        hiddenSections = config.HiddenSections;
        saveSections = config.Save;
        opacity = Math.Clamp(config.BackgroundOpacity, 0.2f, 1f);
        glassBlur = Math.Clamp(config.GlassBlur, 0f, 12f);
        Glass = config.GlassEffect;
        TextOutline.Enabled = config.GlassEffect && config.TextOutline;
        var dark = config.ActivePalette.Background;
        TextOutline.Color = new Vector3(dark.X, dark.Y, dark.Z) * 0.35f;
        Fx.Enabled = config.Animations;
        Ambience.Enabled = config.ThemeAmbience;
        Ambience.FollowGame = config.GameAmbience;
        Backdrop.Enabled = config.ViewBackdrops;
        Ambience.Preset = config.SavedThemeIndex >= 0 && config.SavedThemeIndex < config.SavedThemes.Count ? ThemePreset.Custom : config.ThemePreset;
    }

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

    /// <summary>Brightens a color toward white, keeping its alpha.</summary>
    public static Vector4 Lighten(Vector4 c, float t) =>
        new(c.X + (1 - c.X) * t, c.Y + (1 - c.Y) * t, c.Z + (1 - c.Z) * t, c.W);

    public const float Radius = 6f;
    public const float RadiusLarge = 9f;

    /// <summary>A size in pixels at Dalamud's UI scale.</summary>
    public static float Px(float value) => value * ImGuiHelpers.GlobalScale;

    public static Vector2 Px(Vector2 value) => value * ImGuiHelpers.GlobalScale;

    public static Vector4 Alpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);

    public static uint U32(Vector4 c, float a = 1f) => ImGui.GetColorU32(Alpha(c, a));

    /// <summary>Pushes the colors and metrics of the theme. Dispose to restore Dalamud's style.</summary>
    public static IDisposable Push()
    {
        BlendColors();
        return Glass ? PushGlass() : PushSolid();
    }

    // During a theme change: each color between the old theme's and the new one's.
    private static void BlendColors()
    {
        if (from is null)
            return;
        var k = Fx.EaseOut((Fx.Now - ThemeSweep.Started) / ThemeSweep.Duration);
        if (k >= 1 || !Fx.Enabled)
        {
            from = null;
            palette = target;
            return;
        }

        Blend.Background = Mix(from.Background, target.Background, k);
        Blend.Surface = Mix(from.Surface, target.Surface, k);
        Blend.Accent = Mix(from.Accent, target.Accent, k);
        Blend.Secondary = Mix(from.Secondary, target.Secondary, k);
        Blend.Text = Mix(from.Text, target.Text, k);
        Blend.Good = Mix(from.Good, target.Good, k);
        Blend.Warn = Mix(from.Warn, target.Warn, k);
        Blend.Bad = Mix(from.Bad, target.Bad, k);
        Blend.Tank = Mix(from.Tank, target.Tank, k);
        Blend.Healer = Mix(from.Healer, target.Healer, k);
        Blend.Dps = Mix(from.Dps, target.Dps, k);
        palette = Blend;
    }

    /// <summary>
    /// A table (as ImRaii.Table) that the theme's ambience stays behind: no particle shows over it, as if it were
    /// above them.
    /// </summary>
    public static ThemeTable Table(string id, int columns, ImGuiTableFlags flags = ImGuiTableFlags.None, Vector2 outerSize = default, float innerWidth = 0f) =>
        new(ImRaii.Table(id, columns, flags, outerSize, innerWidth));

    public ref struct ThemeTable
    {
        private ImRaii.TableDisposable table;

        public ThemeTable(ImRaii.TableDisposable table) => this.table = table;

        public readonly bool Success => table.Success;

        public static implicit operator bool(ThemeTable t) => t.Success;

        public void Dispose()
        {
            var shown = table.Success;
            table.Dispose();
            if (shown)
                Ambience.Occlude(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }
    }

    /// <summary>The theme for a window, fading in when it opens.</summary>
    public static IDisposable Push(string window)
    {
        var theme = Push();
        var alpha = OpenFade.Alpha(window);
        var scope = alpha >= 0.999f ? theme : new Both(ImRaii.PushStyle(ImGuiStyleVar.Alpha, alpha), theme);
        // Disposed after the window is drawn: its text then gets its outline.
        return new Both(scope, new Outlined(window));
    }

    private sealed class Outlined(string window) : IDisposable
    {
        public void Dispose() => TextOutline.Apply(window);
    }

    private sealed class Both(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    private static IDisposable PushSolid()
    {
        var clear = new Vector4(0, 0, 0, 0);
        var colors = ImRaii.PushColor(ImGuiCol.WindowBg, Ink)
            .Push(ImGuiCol.ChildBg, clear)
            .Push(ImGuiCol.PopupBg, Panel with { W = 0.98f })
            .Push(ImGuiCol.Border, Line)
            .Push(ImGuiCol.BorderShadow, clear)
            .Push(ImGuiCol.Text, Text)
            .Push(ImGuiCol.TextDisabled, TextFaint)
            .Push(ImGuiCol.TitleBg, palette.Background with { W = Math.Max(opacity, 0.9f) })
            .Push(ImGuiCol.TitleBgActive, Panel with { W = Math.Max(opacity, 0.9f) })
            .Push(ImGuiCol.TitleBgCollapsed, palette.Background with { W = 0.8f })
            .Push(ImGuiCol.FrameBg, Surface)
            .Push(ImGuiCol.FrameBgHovered, SurfaceHover)
            .Push(ImGuiCol.FrameBgActive, SurfaceActive)
            .Push(ImGuiCol.Button, Surface)
            .Push(ImGuiCol.ButtonHovered, SurfaceHover)
            .Push(ImGuiCol.ButtonActive, SurfaceActive)
            .Push(ImGuiCol.Header, Alpha(Crystal, 0.16f))
            .Push(ImGuiCol.HeaderHovered, Alpha(Crystal, 0.26f))
            .Push(ImGuiCol.HeaderActive, Alpha(Crystal, 0.34f))
            .Push(ImGuiCol.Separator, Line)
            .Push(ImGuiCol.SeparatorHovered, Alpha(Gold, 0.6f))
            .Push(ImGuiCol.SeparatorActive, Gold)
            .Push(ImGuiCol.CheckMark, Gold)
            .Push(ImGuiCol.SliderGrab, Gold)
            .Push(ImGuiCol.SliderGrabActive, GoldBright)
            .Push(ImGuiCol.ResizeGrip, Alpha(Gold, 0.25f))
            .Push(ImGuiCol.ResizeGripHovered, Alpha(Gold, 0.55f))
            .Push(ImGuiCol.ResizeGripActive, Alpha(Gold, 0.85f))
            .Push(ImGuiCol.Tab, clear)
            .Push(ImGuiCol.TabHovered, Alpha(Crystal, 0.18f))
            .Push(ImGuiCol.TabActive, Alpha(Crystal, 0.14f))
            .Push(ImGuiCol.TableHeaderBg, Panel)
            .Push(ImGuiCol.TableBorderStrong, Line)
            .Push(ImGuiCol.TableBorderLight, LineSoft)
            .Push(ImGuiCol.TableRowBg, clear)
            .Push(ImGuiCol.TableRowBgAlt, Alpha(Surface, 0.45f))
            .Push(ImGuiCol.ScrollbarBg, clear)
            .Push(ImGuiCol.ScrollbarGrab, ScrollGrab(Line))
            .Push(ImGuiCol.ScrollbarGrabHovered, Alpha(Crystal, 0.5f))
            .Push(ImGuiCol.ScrollbarGrabActive, Alpha(Crystal, 0.75f))
            .Push(ImGuiCol.PlotLines, Gold)
            .Push(ImGuiCol.PlotHistogram, Crystal)
            .Push(ImGuiCol.NavHighlight, Alpha(Gold, 0.7f));

        var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Px(RadiusLarge))
            .Push(ImGuiStyleVar.WindowBorderSize, 1f)
            .Push(ImGuiStyleVar.WindowPadding, Px(new Vector2(14, 12)))
            .Push(ImGuiStyleVar.ChildRounding, Px(Radius))
            .Push(ImGuiStyleVar.FrameRounding, Px(Radius))
            .Push(ImGuiStyleVar.FrameBorderSize, 0f)
            .Push(ImGuiStyleVar.FramePadding, Px(new Vector2(9, 5)))
            .Push(ImGuiStyleVar.PopupRounding, Px(Radius))
            .Push(ImGuiStyleVar.ItemSpacing, Px(new Vector2(8, 6)))
            .Push(ImGuiStyleVar.ItemInnerSpacing, Px(new Vector2(6, 5)))
            .Push(ImGuiStyleVar.IndentSpacing, Px(18))
            .Push(ImGuiStyleVar.ScrollbarSize, Px(11))
            .Push(ImGuiStyleVar.ScrollbarRounding, Px(6))
            .Push(ImGuiStyleVar.GrabMinSize, Px(12))
            .Push(ImGuiStyleVar.GrabRounding, Px(5))
            .Push(ImGuiStyleVar.TabRounding, Px(Radius))
            .Push(ImGuiStyleVar.CellPadding, Px(new Vector2(8, 5)));

        return new Scope(colors, styles);
    }

    /// <summary>
    /// Glass effect: blurs the game behind the current window (call at the start of its Draw). Drawn by the plugin, so
    /// it does not need Dalamud's own window blur turned on. <paramref name="allowed"/>: false for a window whose
    /// background was made fully transparent.
    /// </summary>
    public static void BlurBehind(bool allowed = true)
    {
        ThemeSweep.Draw();
        if (!Glass || !allowed || glassBlur <= 0)
            return;

        var pos = ImGui.GetWindowPos();
        ImGuiHelpers.PrependBlurBehind(ImGui.GetWindowDrawList(), pos, pos + ImGui.GetWindowSize(), glassBlur,
                                       ImGui.GetStyle().WindowRounding, palette.Background with { W = 0.18f },
                                       Vector4.Zero, 0.03f);
    }

    // Glass: the solid theme, then see-through fields and title bar, light rims, rounder corners.
    private static IDisposable PushGlass()
    {
        var solid = PushSolid();
        var rim = Alpha(Frost, 0.16f);
        var colors = ImRaii.PushColor(ImGuiCol.Border, rim)
            .Push(ImGuiCol.ChildBg, Alpha(Frost, 0.025f))
            .Push(ImGuiCol.PopupBg, palette.Background with { W = 0.78f })
            .Push(ImGuiCol.TitleBg, palette.Background with { W = 0.35f })
            .Push(ImGuiCol.TitleBgActive, Mix(palette.Background, palette.Surface, 0.5f) with { W = 0.5f })
            .Push(ImGuiCol.TitleBgCollapsed, palette.Background with { W = 0.3f })
            .Push(ImGuiCol.FrameBg, Alpha(Frost, 0.07f))
            .Push(ImGuiCol.FrameBgHovered, Alpha(Frost, 0.12f))
            .Push(ImGuiCol.FrameBgActive, Alpha(Frost, 0.17f))
            .Push(ImGuiCol.Button, Alpha(Frost, 0.08f))
            .Push(ImGuiCol.ButtonHovered, Alpha(Frost, 0.14f))
            .Push(ImGuiCol.ButtonActive, Alpha(Frost, 0.2f))
            .Push(ImGuiCol.Separator, rim)
            .Push(ImGuiCol.TableHeaderBg, Alpha(Frost, 0.06f))
            .Push(ImGuiCol.TableBorderStrong, rim)
            .Push(ImGuiCol.TableBorderLight, Alpha(Frost, 0.07f))
            .Push(ImGuiCol.TableRowBgAlt, Alpha(Frost, 0.03f))
            .Push(ImGuiCol.ScrollbarGrab, ScrollGrab(Alpha(Frost, 0.18f)));
        var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Px(14))
            .Push(ImGuiStyleVar.ChildRounding, Px(10))
            .Push(ImGuiStyleVar.FrameRounding, Px(8))
            .Push(ImGuiStyleVar.PopupRounding, Px(10))
            .Push(ImGuiStyleVar.FrameBorderSize, 1f)
            .Push(ImGuiStyleVar.TabRounding, Px(8));
        return new Scope(solid, colors, styles);
    }

    private sealed class Scope(params IDisposable[] parts) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            for (var i = parts.Length - 1; i >= 0; i--)
                parts[i].Dispose();
        }
    }

    /// <summary>A FontAwesome glyph inline.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color ?? Text, icon.ToIconString());
    }

    /// <summary>Window header: gold icon, title, muted subtitle and a gold rule under it.</summary>
    public static void WindowHeader(FontAwesomeIcon icon, string title, string? subtitle = null)
    {
        Icon(icon, Gold);
        ImGui.SameLine();
        ImGui.TextColored(GoldBright, title);
        if (subtitle is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(TextMuted, subtitle);
        }

        Rule();
    }

    /// <summary>A separator fading out toward both ends, with a gold core.</summary>
    public static void Rule(float padding = 4f)
    {
        ImGui.Dummy(new Vector2(0, Px(padding)));
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var mid = start.X + width / 2;
        var y = start.Y;
        uint gold = U32(Gold, 0.65f), none = U32(Gold, 0f);
        dl.AddRectFilledMultiColor(start, new Vector2(mid, y + 1), none, gold, gold, none);
        dl.AddRectFilledMultiColor(new Vector2(mid, y), new Vector2(start.X + width, y + 1), gold, none, none, gold);
        ImGui.Dummy(new Vector2(0, Px(padding)));
    }

    /// <summary>Section title: a short gold rule, then the label in gold capitals.</summary>
    /// <param name="help">Shown on hover of a ? right after the title.</param>
    /// <summary>Extra room above section titles (pixels), set by a view that wants its sections further apart.</summary>
    public static float SectionSpacing { get; set; }

    /// <summary>Room between a section title and what follows (pixels), when a view wants it other than the usual.</summary>
    public static float? SectionTitleGap { get; set; }

    // The sections hidden by the player (their keys), from the settings, and how to save them.
    private static HashSet<string> hiddenSections = [];
    private static Action? saveSections;

    /// <summary>
    /// A section header that hides its section: a click on its dash (or title) folds it, the dash turning into a plus,
    /// and a click on the plus shows it again. False when the section is hidden (its title stays; skip its content).
    /// <paramref name="key"/> names the section in the settings.
    /// </summary>
    public static bool Section(string label, FontAwesomeIcon? icon, string key, string? help = null)
    {
        var hidden = hiddenSections.Contains(key);
        if (SectionHeader(label, icon, help, hidden, key))
        {
            if (hidden)
                hiddenSections.Remove(key);
            else
                hiddenSections.Add(key);
            saveSections?.Invoke();
        }

        return !hidden;
    }

    /// <summary>
    /// A table as wide as its content (columns fitted to what they show), not stretched across the window: a name and
    /// its figures stay side by side.
    /// </summary>
    public const ImGuiTableFlags FitTable = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX;

    public static void SectionHeader(string label, FontAwesomeIcon? icon = null, string? help = null) =>
        SectionHeader(label, icon, help, null, null);

    // With a key, the dash and title are a button folding the section: a plus when it is folded. True when clicked.
    private static bool SectionHeader(string label, FontAwesomeIcon? icon, string? help, bool? folded, string? key)
    {
        ImGui.Dummy(new Vector2(0, Px(4) + SectionSpacing));
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        // When the window opens, the sections appear one after the other: the dash grows, the title slides in.
        var shown = SectionCascade.Next(label);
        var lineHeight = ImGui.GetTextLineHeight();
        var lineY = pos.Y + lineHeight / 2;

        var clicked = false;
        var hovered = false;
        if (key is not null)
        {
            var titleWidth = Px(18) + ImGui.CalcTextSize(label.ToUpperInvariant()).X;
            if (icon is { } g)
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    titleWidth += ImGui.CalcTextSize(g.ToIconString()).X;
                titleWidth += ImGui.GetStyle().ItemSpacing.X;
            }

            clicked = ImGui.InvisibleButton($"##section-{key}", new Vector2(titleWidth, lineHeight));
            hovered = ImGui.IsItemHovered();
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                Theme.Tooltip(folded == true ? Loc.T("Show this section") : Loc.T("Hide this section"));
            }
        }

        var alpha = (hovered ? 1f : 0.8f) * (0.1f + 0.9f * shown);
        var thickness = hovered ? 2f : 1.5f;
        dl.AddLine(new Vector2(pos.X, lineY), new Vector2(pos.X + Px(12) * (0.2f + 0.8f * shown), lineY), U32(Gold, alpha), thickness);
        if (shown < 1)
        {
            // A gold line running under the title as it appears.
            var reach = Px(18) + ImGui.CalcTextSize(label).X + Px(30);
            dl.AddLine(new Vector2(pos.X, lineY + lineHeight * 0.7f), new Vector2(pos.X + reach * shown, lineY + lineHeight * 0.7f),
                       U32(Gold, 0.5f * (1 - shown)), 1f);
        }
        if (folded == true)
            dl.AddLine(new Vector2(pos.X + Px(6), lineY - Px(6)), new Vector2(pos.X + Px(6), lineY + Px(6)), U32(Gold, alpha), thickness);
        ImGui.SetCursorScreenPos(new Vector2(pos.X + Px(18) + (1 - shown) * Px(14), pos.Y));
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (0.05f + 0.95f * shown)))
        {
            if (icon is { } glyph)
            {
                Icon(glyph, Gold);
                ImGui.SameLine();
            }

            ImGui.TextColored(Gold, label.ToUpperInvariant());
        }

        if (help is not null)
            HelpMarker(help);
        if (SectionTitleGap is { } gap)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + gap); // from the title to what follows
        else
            ImGui.Dummy(new Vector2(0, Px(1)));
        return clicked;
    }

    /// <summary>Muted explanation text, wrapped to the available width.</summary>
    public static void Hint(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, TextMuted);
        ImGui.TextWrapped(text);
    }

    /// <summary>A question-mark glyph after the previous item, showing the text on hover.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        Icon(FontAwesomeIcon.QuestionCircle, TextFaint);
        if (ImGui.IsItemHovered())
            Tooltip(text, Px(380));
    }

    /// <summary>Header row of a table: white text on the accent color (toned down so the text stays readable).</summary>
    public static void TableHeaders()
    {
        using var color = ImRaii.PushColor(ImGuiCol.TableHeaderBg, Alpha(Gold, 0.45f))
            .Push(ImGuiCol.Text, Vector4.One);
        ImGui.TableHeadersRow();
    }

    /// <summary>A rounded pill for counts and states.</summary>
    public static void Badge(string label, Vector4 color)
    {
        var size = ImGui.CalcTextSize(label);
        var pad = Px(new Vector2(7, 1));
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var max = pos + size + pad * 2;
        dl.AddRectFilled(pos, max, U32(color, 0.16f), size.Y);
        dl.AddRect(pos, max, U32(color, 0.55f), size.Y);
        dl.AddText(pos + pad, U32(color), label);
        ImGui.Dummy(size + pad * 2);
    }

    /// <summary>Filled gold button for the main action of a view.</summary>
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Gold)
            .Push(ImGuiCol.ButtonHovered, GoldBright)
            .Push(ImGuiCol.ButtonActive, GoldDim)
            .Push(ImGuiCol.Text, Ink with { W = 1f });
        var clicked = ImGui.Button(label, size);
        HoverLight.OnItem();
        Ripple.OnItem(clicked);
        return clicked;
    }

    /// <summary>Outlined button for secondary actions.</summary>
    public static bool GhostButton(string label, Vector2 size = default)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.ButtonHovered, Alpha(Gold, 0.14f))
            .Push(ImGuiCol.ButtonActive, Alpha(Gold, 0.26f))
            .Push(ImGuiCol.Border, Alpha(Gold, 0.55f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f);
        var clicked = ImGui.Button(label, size);
        HoverLight.OnItem();
        Ripple.OnItem(clicked);
        return clicked;
    }

    /// <summary>A button showing only a FontAwesome glyph, with a tooltip.</summary>
    // Row tops of each table, this frame and the last (screen y by row index): a row's height is the gap to the next.
    private sealed class RowTops
    {
        public int Frame;
        public Dictionary<int, float> Current = new();
        public Dictionary<int, float> Last = new();
    }

    private static readonly Dictionary<uint, RowTops> TableRows = new();

    /// <summary>
    /// Call right after ImGui.TableNextRow(): lights the row under the mouse, to follow one line across the columns.
    /// The row's height is taken from the last frame (the gap to the next row's top).
    /// </summary>
    public static void RowHover()
    {
        var key = ImGui.GetID("##rowhover");
        if (!TableRows.TryGetValue(key, out var rows))
            TableRows[key] = rows = new RowTops();
        var frame = ImGui.GetFrameCount();
        if (rows.Frame != frame)
        {
            (rows.Last, rows.Current) = (rows.Current, rows.Last);
            rows.Current.Clear();
            rows.Frame = frame;
        }

        var index = ImGui.TableGetRowIndex();
        var top = ImGui.GetCursorScreenPos().Y;
        rows.Current[index] = top;

        var height = rows.Last.TryGetValue(index, out var lastTop) && rows.Last.TryGetValue(index + 1, out var nextTop) && nextTop > lastTop
                         ? nextTop - lastTop
                         : ImGui.GetFrameHeight();
        var mouse = ImGui.GetMousePos().Y;
        if (ImGui.IsWindowHovered() && mouse >= top && mouse < top + height)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, U32(Crystal, 0.14f));
    }

    /// <summary>Width of an <see cref="IconButton"/>: its glyph in the icon font, its padding and its border.</summary>
    public static float IconButtonWidth(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var style = ImGui.GetStyle();
        return ImGui.CalcTextSize(icon.ToIconString()).X + style.FramePadding.X * 2 + style.FrameBorderSize * 2;
    }

    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip, Vector4? color = null)
    {
        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? TextMuted))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0)))
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}");
        }

        HoverLight.OnItem();
        Ripple.OnItem(clicked);

        if (ImGui.IsItemHovered())
            Theme.Tooltip(tooltip);
        return clicked;
    }

    /// <summary>
    /// A figure on a card: small caption, big value, optional hint line. The accent colors the top edge and the value.
    /// </summary>
    /// <param name="fullHeight">Keeps the hint line's room even without a hint, so that cards side by side match.</param>
    public static void StatCard(string caption, string value, Vector4 accent, float width, string? hint = null,
                                Vector4? hintColor = null, Vector4? valueColor = null, bool fullHeight = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var pad = Px(new Vector2(10, 7));
        var line = ImGui.GetTextLineHeight();
        var font = ImGui.GetFont();
        var valueSize = ImGui.GetFontSize() * 1.45f;
        var height = pad.Y * 2 + line + valueSize + Px(2) + (hint is null && !fullHeight ? 0 : line);

        // Wider than asked when the caption, value or hint would not fit.
        width = Math.Max(width, StatCardWidth(caption, value, hint));

        var max = pos + new Vector2(width, height);
        dl.AddRectFilled(pos, max, U32(Surface, 0.8f), Px(Radius));
        dl.AddRect(pos, max, U32(Line, 0.8f), Px(Radius));
        dl.AddRectFilled(pos + new Vector2(Px(Radius), 0), new Vector2(max.X - Px(Radius), pos.Y + Px(2)), U32(accent, 0.9f));

        var y = pos.Y + pad.Y;
        dl.AddText(new Vector2(pos.X + pad.X, y), U32(TextMuted), caption.ToUpperInvariant());
        y += line + Px(1);
        RollingText.Draw(dl, font, valueSize, new Vector2(pos.X + pad.X, y), U32(valueColor ?? Text), value, ImGui.GetID($"card:{caption}"));
        y += valueSize + Px(1);
        if (hint is not null)
            dl.AddText(new Vector2(pos.X + pad.X, y), U32(hintColor ?? TextFaint), hint);

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>The narrowest a card can be with this caption, value and hint.</summary>
    public static float StatCardWidth(string caption, string value, string? hint)
    {
        var valueSize = ImGui.GetFontSize() * 1.45f;
        var content = Math.Max(ImGui.CalcTextSize(caption.ToUpperInvariant()).X,
                               ImGui.CalcTextSize(value).X * valueSize / ImGui.GetFontSize());
        if (hint is not null)
            content = Math.Max(content, ImGui.CalcTextSize(hint).X);
        return content + Px(10) * 2;
    }

    /// <summary>Width of each of <paramref name="count"/> cards filling the line.</summary>
    public static float CardWidth(int count) =>
        count <= 0 ? 0 : (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * (count - 1)) / count;

    /// <summary>A pill that toggles: gold when on. Returns true when clicked.</summary>
    /// <param name="accent">Draws the chip as on, in this color (a button standing out rather than a toggle).</param>
    public static bool Chip(string label, bool on, bool transparent = false, Vector4? accent = null)
    {
        var color = accent ?? Gold;
        on |= accent is not null;
        // "Text##id": the id keeps chips with the same text apart but is not shown.
        var cut = label.IndexOf("##", StringComparison.Ordinal);
        var text = cut >= 0 ? label[..cut] : label;
        var size = ImGui.CalcTextSize(text) + Px(new Vector2(20, 8));
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##chip{label}", size);
        var hovered = ImGui.IsItemHovered();
        Ripple.OnItem(clicked);

        var dl = ImGui.GetWindowDrawList();
        var max = pos + size;
        var rounding = size.Y / 2;
        if (on)
        {
            dl.AddRectFilled(pos, max, U32(color, hovered ? 0.24f : 0.16f), rounding);
            dl.AddRect(pos, max, U32(color, 0.7f), rounding);
        }
        else if (transparent)
        {
            // On a see-through window: an outline, filled only on hover.
            if (hovered)
                dl.AddRectFilled(pos, max, U32(SurfaceHover, 0.6f), rounding);
            dl.AddRect(pos, max, U32(TextMuted, 0.5f), rounding);
        }
        else
        {
            dl.AddRectFilled(pos, max, U32(hovered ? SurfaceHover : Surface), rounding);
        }

        if (hovered)
            HoverLight.Draw(pos, max);
        dl.AddText(pos + Px(new Vector2(10, 4)), U32(on ? Lighten(color, 0.4f) : TextMuted), text);
        return clicked;
    }

    /// <summary>
    /// Pills to switch between views, with a gold underline on the selected one. Returns true when it changed.
    /// </summary>
    private static readonly Dictionary<uint, (Vector2 Min, Vector2 Max)> TabSelections = new();

    public static bool TabStrip(string id, ref int selected, params (FontAwesomeIcon Icon, string Label)[] tabs)
    {
        var changed = false;
        var dl = ImGui.GetWindowDrawList();
        // The texts over the selection drawn after them (it glides from tab to tab).
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        var window = ImGui.GetWindowPos();
        var target = (Min: Vector2.Zero, Max: Vector2.Zero);
        for (var i = 0; i < tabs.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Px(4));

            var (icon, label) = tabs[i];
            var glyph = icon.ToIconString();
            float glyphWidth;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                glyphWidth = ImGui.CalcTextSize(glyph).X;

            var textSize = ImGui.CalcTextSize(label);
            var size = new Vector2(glyphWidth + Px(8) + textSize.X, textSize.Y) + Px(new Vector2(28, 12));
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##{id}{i}", size) && selected != i)
            {
                selected = i;
                changed = true;
            }

            var hovered = ImGui.IsItemHovered();

            var isSelected = selected == i;
            var max = pos + size;
            if (isSelected)
            {
                target = (pos - window, max - window);
            }
            else if (hovered)
            {
                dl.AddRectFilled(pos, max, U32(SurfaceHover, 0.7f), Px(Radius));
            }

            if (hovered)
                HoverLight.Draw(pos, max);

            var color = isSelected ? GoldBright : hovered ? Text : TextMuted;
            var textPos = pos + Px(new Vector2(14, 6));
            using (ImRaii.PushFont(UiBuilder.IconFont))
                dl.AddText(textPos, U32(isSelected ? Gold : color), glyph);
            dl.AddText(textPos + new Vector2(glyphWidth + Px(8), 0), U32(color), label);
        }

        // The selection, where it was last frame moving toward the tab selected (in the window: it follows a move).
        var key = ImGui.GetID($"tabs:{id}");
        var rect = TabSelections.TryGetValue(key, out var shown) && Fx.Enabled
                       ? (Min: Vector2.Lerp(shown.Min, target.Min, Math.Min(1f, Fx.Delta * 14f)), Max: Vector2.Lerp(shown.Max, target.Max, Math.Min(1f, Fx.Delta * 14f)))
                       : target;
        TabSelections[key] = rect;
        dl.ChannelsSetCurrent(0);
        var (min, maxRect) = (rect.Min + window, rect.Max + window);
        if (maxRect.X - min.X > Px(16))
        {
            dl.AddRectFilled(min, maxRect, U32(Gold, 0.12f), Px(Radius));
            dl.AddRectFilled(new Vector2(min.X + Px(8), maxRect.Y - Px(2)), new Vector2(maxRect.X - Px(8), maxRect.Y), U32(Gold));
        }
        dl.ChannelsMerge();
        return changed;
    }

    /// <summary>
    /// A navigation entry for a side menu: icon and label, a gold bar and tint when selected. Returns true when clicked.
    /// </summary>
    public static bool NavItem(string id, FontAwesomeIcon icon, string label, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeight() + Px(14);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var max = pos + new Vector2(width, height);
        if (selected)
        {
            dl.AddRectFilled(pos, max, U32(Gold, 0.12f), Px(Radius));
            dl.AddRectFilled(pos, new Vector2(pos.X + Px(3), max.Y), U32(Gold), Px(2));
        }
        else if (hovered)
        {
            dl.AddRectFilled(pos, max, U32(SurfaceHover, 0.7f), Px(Radius));
        }

        var textY = pos.Y + (height - ImGui.GetTextLineHeight()) / 2;
        var color = selected ? GoldBright : hovered ? Text : TextMuted;
        var glyph = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyphWidth = ImGui.CalcTextSize(glyph).X;
            var iconColumn = Px(18);
            dl.AddText(new Vector2(pos.X + Px(12) + (iconColumn - glyphWidth) / 2, textY), U32(selected ? Gold : color), glyph);
        }

        dl.AddText(new Vector2(pos.X + Px(38), textY), U32(color), label);
        return clicked;
    }
}
