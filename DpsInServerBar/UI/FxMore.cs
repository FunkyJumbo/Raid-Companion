using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace DpsInServerBar.UI;

/// <summary>A ripple spreading from where a button was clicked, inside the button.</summary>
public static class Ripple
{
    private const float Duration = 0.55f;
    private static readonly Dictionary<int, (Vector2 At, float Started)> Ripples = new();

    /// <summary>After an ImGui item: starts a ripple when it was clicked, draws it while it lasts.</summary>
    public static void OnItem(bool clicked)
    {
        if (!Fx.Enabled)
            return;
        // The item: its place in its window (ImGui does not give its id here).
        var id = HashCode.Combine(ImGui.GetID("##ripple"), ImGui.GetItemRectMin() - ImGui.GetWindowPos());
        if (clicked)
            Ripples[id] = (ImGui.GetMousePos(), Fx.Now);
        if (!Ripples.TryGetValue(id, out var ripple))
            return;
        var k = (Fx.Now - ripple.Started) / Duration;
        if (k >= 1)
        {
            Ripples.Remove(id);
            return;
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var reach = (max - min).Length();
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min, max, true);
        dl.AddCircleFilled(ripple.At, Theme.Px(6) + reach * Fx.EaseOut(k), Theme.U32(Theme.GoldBright, 0.3f * (1 - k)), 32);
        dl.PopClipRect();
    }
}

/// <summary>
/// Small messages in the bottom right corner of the screen ("Copied", a new best pull...): they slide in with a trail
/// of light, stay a few seconds and slide out.
/// </summary>
public static class Toasts
{
    private const float In = 0.35f, Stay = 3.2f, Out = 0.35f;
    private static readonly List<(string Text, FontAwesomeIcon Icon, float Started)> Shown = [];

    public static void Show(string text, FontAwesomeIcon icon = FontAwesomeIcon.Check, float delay = 0f)
    {
        Shown.RemoveAll(t => t.Text == text);
        Shown.Add((text, icon, Fx.Now + (Fx.Enabled ? delay : 0)));
        if (Shown.Count > 4)
            Shown.RemoveAt(0);
    }

    /// <summary>Every frame (the plugin's draw): the toasts on the screen's foreground.</summary>
    public static void Draw()
    {
        var viewport = ImGui.GetMainViewport();
        var bottom = viewport.WorkPos.Y + viewport.WorkSize.Y - Theme.Px(60);
        var right = viewport.WorkPos.X + viewport.WorkSize.X - Theme.Px(24);
        if (Shown.Count == 0)
            return;
        Shown.RemoveAll(t => Fx.Now - t.Started > In + Stay + Out);
        using var theme = Theme.Push();
        var dl = ImGui.GetForegroundDrawList();
        var pad = Theme.Px(new Vector2(12, 8));
        for (var i = Shown.Count - 1; i >= 0; i--)
        {
            var (text, icon, started) = Shown[i];
            var age = Fx.Now - started;
            if (age < 0)
                continue;
            var slide = !Fx.Enabled ? 1 : age < In ? Fx.EaseOut(age / In) : age > In + Stay ? 1 - Fx.EaseOut((age - In - Stay) / Out) : 1;
            string glyph = icon.ToIconString();
            float glyphWidth;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                glyphWidth = ImGui.CalcTextSize(glyph).X;
            var size = new Vector2(glyphWidth + Theme.Px(8) + ImGui.CalcTextSize(text).X, ImGui.GetTextLineHeight()) + pad * 2;
            var min = new Vector2(right - size.X + (1 - slide) * (size.X + Theme.Px(40)), bottom - size.Y);
            var max = min + size;
            var alpha = Math.Max(0.05f, slide);

            // The trail: fainter copies behind it while it slides in.
            if (Fx.Enabled && age < In)
            {
                for (var t = 1; t <= 4; t++)
                {
                    var back = new Vector2(t * Theme.Px(16) * (1 - slide), 0);
                    dl.AddRectFilled(min + back, max + back, Theme.U32(Theme.Gold, 0.09f * (1 - t / 5f)), Theme.Px(Theme.Radius));
                }
            }

            dl.AddRectFilled(min, max, Theme.U32(Theme.Panel with { W = 1 }, 0.96f * alpha), Theme.Px(Theme.Radius));
            dl.AddRect(min, max, Theme.U32(Theme.Gold, 0.8f * alpha), Theme.Px(Theme.Radius));
            using (ImRaii.PushFont(UiBuilder.IconFont))
                dl.AddText(min + pad, Theme.U32(Theme.Gold, alpha), glyph);
            dl.AddText(min + pad + new Vector2(glyphWidth + Theme.Px(8), 0), Theme.U32(Theme.Text, alpha), text);
            bottom = min.Y - Theme.Px(8);
        }
    }
}

/// <summary>A light sweeping across every window when the theme changes, while the colors pass to the new one.</summary>
public static class ThemeSweep
{
    public const float Duration = 0.7f;
    public static float Started { get; set; } = -100f;

    /// <summary>At the start of a window's Draw: the sweep over it, on the foreground.</summary>
    public static void Draw()
    {
        var k = (Fx.Now - Started) / Duration;
        if (!Fx.Enabled || k is < 0 or >= 1)
            return;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var band = Theme.Px(70);
        var x = min.X - band + (max.X - min.X + band * 2) * Fx.EaseOut(k);
        var dl = ImGui.GetForegroundDrawList();
        dl.PushClipRect(min, max, true);
        uint none = Theme.U32(Theme.GoldBright, 0f), lit = Theme.U32(Theme.GoldBright, 0.28f);
        dl.AddRectFilledMultiColor(new Vector2(x - band, min.Y), new Vector2(x, max.Y), none, lit, lit, none);
        dl.AddRectFilledMultiColor(new Vector2(x, min.Y), new Vector2(x + Theme.Px(3), max.Y), lit, lit, lit, lit);
        dl.PopClipRect();
    }
}

/// <summary>
/// The sections of a window appearing one after the other when it opens: each section title's rule grows and its
/// title slides in a little after the one above.
/// </summary>
public static class SectionCascade
{
    private static readonly Dictionary<uint, (int Frame, float Opened, int Count, string First)> Windows = new();

    /// <summary>
    /// For the next section title of the current window: 0 to 1, how far it has appeared. The cascade starts again when
    /// the window opens or shows other sections (its first title changes, e.g. another settings page).
    /// </summary>
    public static float Next(string label)
    {
        if (!Fx.Enabled)
            return 1;
        var key = ImGui.GetID("##sectioncascade");
        var frame = ImGui.GetFrameCount();
        if (!Windows.TryGetValue(key, out var state) || frame - state.Frame > 2)
            state = (frame, Fx.Now, 0, label);
        if (state.Frame != frame)
            state = state.First == label ? (frame, state.Opened, 0, label) : (frame, Fx.Now, 0, label);
        var index = state.Count;
        Windows[key] = (frame, state.Opened, index + 1, state.First);
        return Fx.EaseOut((Fx.Now - state.Opened - 0.08f - index * 0.07f) / 0.35f);
    }
}

/// <summary>A title made of particles flying together into its letters, then the text lit in.</summary>
public sealed class ParticleTitle
{
    private const float Gather = 1.6f;
    private readonly List<(Vector2 From, Vector2 To, float Delay)> dots = [];
    private string text = string.Empty;
    private float started = -100f;
    private readonly Random random = new();

    public void Start() => started = Fx.Enabled ? Fx.Now : -100f;

    /// <summary>The title at a place (its top left), this font size.</summary>
    public void Draw(ImDrawListPtr dl, Vector2 at, float size, string title, Vector4 color, Vector2 areaMin, Vector2 areaMax)
    {
        var scale = size / ImGui.GetFontSize();
        if (title != text || dots.Count == 0)
        {
            // Targets in each letter's cell (none in the spaces), sources anywhere in the area.
            text = title;
            dots.Clear();
            var x = 0f;
            foreach (var ch in title)
            {
                var w = ImGui.CalcTextSize(ch.ToString()).X * scale;
                if (!char.IsWhiteSpace(ch))
                {
                    for (var i = 0; i < 9; i++)
                    {
                        dots.Add((new Vector2(random.NextSingle(), random.NextSingle()),
                                  new Vector2(x + w * (0.15f + 0.7f * random.NextSingle()), size * (0.2f + 0.65f * random.NextSingle())),
                                  random.NextSingle() * 0.35f));
                    }
                }

                x += w;
            }
        }

        var age = Fx.Now - started;
        var lit = Fx.Clamp01((age - Gather + 0.2f) / 0.5f);
        if (age < Gather + 0.4f)
        {
            foreach (var (from, to, delay) in dots)
            {
                var k = Fx.EaseOut((age - delay) / (Gather - 0.35f));
                var source = areaMin + from * (areaMax - areaMin);
                var pos = Vector2.Lerp(source, at + to, k);
                Fx.Glow(dl, pos, Theme.Px(1.2f), color, (0.35f + 0.65f * k) * (1 - lit));
            }
        }

        // The text lit in, with a flash as it forms.
        dl.AddText(ImGui.GetFont(), size, at, Theme.U32(color, age >= 0 ? lit : 1), title);
        if (age >= 0 && lit is > 0 and < 1)
            dl.AddText(ImGui.GetFont(), size, at, Theme.U32(Vector4.One, 0.6f * (1 - lit) * lit * 4), title);
    }
}

/// <summary>
/// A golden star above a spot (the furthest pull): it grows in when the spot is new, then twinkles while a ring leaves
/// the spot now and then.
/// </summary>
public sealed class PulseStar
{
    private const float GrowIn = 0.45f, RingEvery = 1.6f;
    private float started = -100f;
    private object? key;

    /// <param name="what">What the star marks: a new one grows it in again.</param>
    public void Draw(ImDrawListPtr dl, Vector2 spot, float height, Vector4 color, object what)
    {
        if (!Equals(what, key))
        {
            key = what;
            started = Fx.Enabled ? Fx.Now : -100f;
        }

        var shown = Fx.EaseOut((Fx.Now - started) / GrowIn);
        if (Fx.Enabled)
        {
            var r = Fx.Now % RingEvery / RingEvery;
            dl.AddCircle(spot, Theme.Px(6) + r * height * 0.8f, Theme.U32(color, 0.7f * (1 - r) * shown), 32, Theme.Px(1.5f));
        }

        Shapes.Star(dl, spot - new Vector2(0, height * 0.6f), height * 0.4f * shown, color);
    }
}

/// <summary>Small shapes drawn by hand.</summary>
public static class Shapes
{
    /// <summary>A five-pointed star twinkling (phase: a different rhythm per star).</summary>
    public static void Star(ImDrawListPtr dl, Vector2 center, float radius, Vector4 color, float phase = 0)
    {
        var twinkle = Fx.Enabled ? 0.85f + 0.2f * MathF.Sin(Fx.Now * 4f + phase) : 1f;
        var r = radius * twinkle;
        Span<Vector2> points = stackalloc Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var (s, c) = MathF.SinCos(-MathF.PI / 2 + i * MathF.PI / 5);
            points[i] = center + new Vector2(c, s) * (i % 2 == 0 ? r : r * 0.45f);
        }

        // As triangles from the center (the star is not convex).
        for (var i = 0; i < 10; i++)
            dl.AddTriangleFilled(center, points[i], points[(i + 1) % 10], Theme.U32(color));
        if (Fx.Enabled)
            Fx.Glow(dl, center, radius * 0.35f, color, 0.25f + 0.25f * MathF.Sin(Fx.Now * 4f + phase));
    }
}

/// <summary>
/// A list filtered by a search: when the search changes, the rows left out slide away to the right and fade, and a
/// light carries each row kept from its old place to its new one.
/// </summary>
public sealed class SearchFlow
{
    private const float Duration = 0.35f;
    private string filter = string.Empty;
    private float changedAt = -100f;
    private List<(string Text, float X, float Y)> current = [], previous = [];
    private Vector2 origin;

    /// <summary>Before the list's rows, with the search typed.</summary>
    public void Begin(string search)
    {
        if (search != filter)
        {
            filter = search;
            changedAt = Fx.Enabled ? Fx.Now : -100f;
            (previous, current) = (current, previous);
        }

        current.Clear();
        origin = ImGui.GetCursorScreenPos();
    }

    /// <summary>Before each row shown, with its text.</summary>
    public void Row(string text)
    {
        var at = ImGui.GetCursorScreenPos() - origin;
        current.Add((text, at.X, at.Y));
    }

    /// <summary>After the rows.</summary>
    public void End()
    {
        var k = (Fx.Now - changedAt) / Duration;
        if (k is < 0 or >= 1)
            return;
        var e = Fx.EaseOut(k);
        var dl = ImGui.GetWindowDrawList();
        var line = ImGui.GetTextLineHeight();
        var width = ImGui.GetContentRegionAvail().X;
        foreach (var (text, x, y) in previous)
        {
            var kept = current.FindIndex(r => r.Text == text);
            if (kept < 0)
            {
                // Left out: slides away and fades.
                dl.AddText(origin + new Vector2(x + e * Theme.Px(60), y), Theme.U32(Theme.TextMuted, 0.8f * (1 - e)), text);
                continue;
            }

            var to = current[kept].Y;
            if (Math.Abs(to - y) < 1)
                continue;
            // Kept, moved: a light going from its old place up to the new one.
            var band = origin.Y + y + (to - y) * e;
            dl.AddRectFilled(new Vector2(origin.X, band), new Vector2(origin.X + width, band + line + Theme.Px(2)),
                             Theme.U32(Theme.Gold, 0.14f * (1 - e)), Theme.Px(4));
        }
    }
}

