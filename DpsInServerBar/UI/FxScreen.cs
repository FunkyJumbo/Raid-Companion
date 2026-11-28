using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DpsInServerBar.UI;

/// <summary>A gold light following the mouse over a hovered button, chip or tab.</summary>
public static class HoverLight
{
    public static void Draw(Vector2 min, Vector2 max)
    {
        if (!Fx.Enabled)
            return;
        var mouse = ImGui.GetMousePos();
        if (mouse.X < min.X || mouse.Y < min.Y || mouse.X > max.X || mouse.Y > max.Y)
            return;
        var dl = ImGui.GetWindowDrawList();
        var radius = Math.Max(Theme.Px(22), (max.Y - min.Y) * 1.2f);
        dl.PushClipRect(min, max, true);
        for (var i = 1; i <= 4; i++)
            dl.AddCircleFilled(mouse, radius * i / 4f, Theme.U32(Theme.Gold, 0.05f), 24);
        dl.PopClipRect();
    }

    /// <summary>After an ImGui item: the light when it is hovered.</summary>
    public static void OnItem()
    {
        if (ImGui.IsItemHovered())
            Draw(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
    }
}

/// <summary>Windows fading in when they open.</summary>
public static class OpenFade
{
    private const float Duration = 0.22f;
    private static readonly Dictionary<string, (int Frame, float Opened)> Windows = new();

    /// <summary>The alpha of a window this frame (call once per frame it is drawn, before it begins).</summary>
    public static float Alpha(string window)
    {
        var frame = ImGui.GetFrameCount();
        var now = Fx.Now;
        if (!Windows.TryGetValue(window, out var state) || frame - state.Frame > 2)
            state = (frame, Fx.Enabled ? now : -100f);
        state.Frame = frame;
        Windows[window] = state;
        return 0.05f + 0.95f * Fx.EaseOut((now - state.Opened) / Duration);
    }
}

/// <summary>
/// The theme's ambience behind a window's content: snow for Frost, embers for Crimson, fireflies for Emerald, wisps of
/// aether for Aether, slow dust for Slate, golden motes otherwise. Faint; off with the animations or its own setting.
/// </summary>
public static class Ambience
{
    public static bool Enabled { get; set; } = true;
    public static ThemePreset Preset { get; set; } = ThemePreset.Midnight;

    /// <summary>The ambience follows the game: rain or snow after the weather.</summary>
    public static bool FollowGame { get; set; } = true;

    private struct Mote
    {
        public Vector2 Pos; // 0..1 in the window
        public float Speed, Phase, Size;
    }

    private static readonly Dictionary<uint, List<Mote>> Windows = new();
    private static readonly Random Random = new();

    // Per window (its root): the places of its tables, graphs and lists drawn this frame, kept for the next one (the
    // ambience is drawn first, before them). No particle shows there.
    private static readonly Dictionary<uint, List<(Vector2 Min, Vector2 Max)>> Covered = new();

    // The places covered, as read at the start of this frame (for the backgrounds of the views).
    private static readonly Dictionary<uint, (Vector2 Min, Vector2 Max)[]> LastCovered = new();

    /// <summary>The places of the current window's tables, graphs and lists (last frame): keep a background out of them.</summary>
    public static (Vector2 Min, Vector2 Max)[] CoveredHere() => LastCovered.TryGetValue(RootId(), out var covered) ? covered : [];

    private static unsafe uint RootId()
    {
        var window = ImGuiP.GetCurrentWindow();
        return window.Handle == null || window.RootWindow.Handle == null ? 0 : window.RootWindow.ID;
    }

    /// <summary>Keeps the particles of the window drawn out of a place (a table, a graph...), screen coordinates.</summary>
    public static void Occlude(Vector2 min, Vector2 max)
    {
        // Kept for the backgrounds of the views too, which can be on without the ambience.
        if ((!Enabled && !Backdrop.Enabled) || !Fx.Enabled)
            return;
        var key = RootId();
        if (!Covered.TryGetValue(key, out var list))
            Covered[key] = list = [];
        list.Add((min, max));
    }

    /// <summary>The same for what is drawn next at the cursor (a graph, a list), of this size (-1 or 0: all the width or height left).</summary>
    public static void OccludeNext(Vector2 size)
    {
        var pos = ImGui.GetCursorScreenPos();
        var room = ImGui.GetContentRegionAvail();
        Occlude(pos, pos + new Vector2(size.X <= 0 ? room.X + size.X + 1 : size.X, size.Y <= 0 ? room.Y + size.Y + 1 : size.Y));
    }

    private static Mote New() => new()
    {
        Pos = new Vector2((float)Random.NextDouble(), (float)Random.NextDouble()),
        Speed = 0.5f + (float)Random.NextDouble(),
        Phase = (float)Random.NextDouble() * MathF.Tau,
        Size = 0.7f + (float)Random.NextDouble() * 0.8f,
    };

    /// <summary>Call at the start of a window's Draw: drawn behind its content.</summary>
    public static void Draw()
    {
        if (!Fx.Enabled)
            return;
        // The places covered last frame; this frame's are gathered anew (the backgrounds of the views use them even
        // with the ambience off).
        var key = RootId();
        var covered = Covered.TryGetValue(key, out var lastFrame) ? lastFrame.ToArray() : [];
        lastFrame?.Clear();
        LastCovered[key] = covered;
        if (!Enabled)
            return;
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (size.X < 50 || size.Y < 50)
            return;

        if (!Windows.TryGetValue(key, out var motes))
            Windows[key] = motes = [];
        var px = Theme.Px(1);
        var wanted = Math.Clamp((int)(size.X * size.Y / (px * px * 9000f)), 12, 60);
        while (motes.Count < wanted)
            motes.Add(New());
        if (motes.Count > wanted)
            motes.RemoveRange(wanted, motes.Count - wanted);

        var dl = ImGui.GetWindowDrawList();
        var dt = Fx.Delta;
        var time = Fx.Now;
        var sky = FollowGame ? GameSky.Weather : GameSky.Sky.Clear;
        dl.PushClipRect(min, min + size, true);
        if (sky == GameSky.Sky.Rain)
            DrawRain(dl, min, size, px, time, covered);
        for (var i = 0; i < motes.Count; i++)
        {
            var m = motes[i];
            if (sky == GameSky.Sky.Snow && i % 3 == 0)
            {
                DrawSnowflake(dl, ref m, min, size, px, time, dt, covered);
                motes[i] = m;
                continue;
            }

            // How it moves (pixels per second) and looks, per theme.
            Vector2 move;
            Vector4 color;
            float alpha;
            var radius = m.Size * px * 1.3f;
            switch (Preset)
            {
                case ThemePreset.Frost: // snow falling, swaying
                    move = new Vector2(MathF.Sin(time * 0.6f + m.Phase) * 8f, 14f * m.Speed);
                    color = new Vector4(0.9f, 0.95f, 1f, 1f);
                    alpha = 0.45f;
                    radius *= 1.2f;
                    break;
                case ThemePreset.Crimson: // embers rising, flickering
                    move = new Vector2(MathF.Sin(time * 1.4f + m.Phase) * 10f, -18f * m.Speed);
                    color = new Vector4(1f, 0.5f, 0.25f, 1f);
                    alpha = 0.35f + 0.3f * MathF.Sin(time * 6f + m.Phase * 3);
                    break;
                case ThemePreset.Emerald: // fireflies wandering, blinking
                    move = new Vector2(MathF.Cos(time * 0.5f + m.Phase) * 12f, MathF.Sin(time * 0.7f + m.Phase * 2) * 10f);
                    color = new Vector4(0.65f, 1f, 0.55f, 1f);
                    alpha = 0.55f * MathF.Max(0f, MathF.Sin(time * 1.3f + m.Phase * 4));
                    break;
                case ThemePreset.Aether: // wisps of aether rising
                    move = new Vector2(MathF.Sin(time * 0.8f + m.Phase) * 6f, -10f * m.Speed);
                    color = Theme.Crystal;
                    alpha = 0.3f + 0.2f * MathF.Sin(time * 2f + m.Phase);
                    break;
                case ThemePreset.Slate: // slow dust
                    move = new Vector2(4f * m.Speed, MathF.Sin(time * 0.3f + m.Phase) * 3f);
                    color = Theme.Text;
                    alpha = 0.2f;
                    radius *= 0.8f;
                    break;
                default: // golden motes
                    move = new Vector2(MathF.Sin(time * 0.8f + m.Phase) * 6f, -9f * m.Speed);
                    color = Theme.Gold;
                    alpha = 0.3f + 0.2f * MathF.Sin(time * 2f + m.Phase);
                    break;
            }

            m.Pos += move * px / size * dt;
            m.Pos = new Vector2(Wrap(m.Pos.X), Wrap(m.Pos.Y));
            motes[i] = m;
            var at = min + m.Pos * size;
            var hidden = false;
            foreach (var (from, to) in covered)
            {
                if (at.X >= from.X - radius && at.Y >= from.Y - radius && at.X <= to.X + radius && at.Y <= to.Y + radius)
                {
                    hidden = true;
                    break;
                }
            }

            if (!hidden)
                Fx.Glow(dl, at, radius, color, Math.Max(0f, alpha) * 0.7f);
        }

        dl.PopClipRect();
    }

    private static float Wrap(float v) => v < 0 ? v + 1 : v > 1 ? v - 1 : v;

    // Fine rain falling slant behind the content (not over the tables and graphs).
    private static void DrawRain(ImDrawListPtr dl, Vector2 min, Vector2 size, float px, float time, (Vector2 Min, Vector2 Max)[] covered)
    {
        var count = Math.Clamp((int)(size.X / (px * 14)), 20, 90);
        var color = Theme.U32(new Vector4(0.7f, 0.82f, 1f, 1f), 0.16f);
        var length = px * 11;
        for (var i = 0; i < count; i++)
        {
            // Each drop: its own column and speed, the same from frame to frame.
            var seed = (uint)(i * 2654435761);
            var column = (seed % 1000) / 1000f;
            var speed = 260f + seed % 140;
            var y = (time * speed * px + (seed >> 12) % 1000 / 1000f * size.Y) % (size.Y + length) - length;
            var at = min + new Vector2(column * size.X + y * 0.12f % size.X, y);
            var hidden = false;
            foreach (var (from, to) in covered)
            {
                if (at.X >= from.X && at.Y >= from.Y - length && at.X <= to.X && at.Y <= to.Y)
                {
                    hidden = true;
                    break;
                }
            }

            if (!hidden)
                dl.AddLine(at, at + new Vector2(-length * 0.25f, length), color, px);
        }
    }

    // A snowflake drifting down when it snows where you are.
    private static void DrawSnowflake(ImDrawListPtr dl, ref Mote m, Vector2 min, Vector2 size, float px, float time, float dt,
                                      (Vector2 Min, Vector2 Max)[] covered)
    {
        var move = new Vector2(MathF.Sin(time * 0.7f + m.Phase) * 10f, 13f * m.Speed);
        m.Pos += move * px / size * dt;
        m.Pos = new Vector2(Wrap(m.Pos.X), Wrap(m.Pos.Y));
        var at = min + m.Pos * size;
        var radius = m.Size * px * 2.2f;
        foreach (var (from, to) in covered)
        {
            if (at.X >= from.X - radius && at.Y >= from.Y - radius && at.X <= to.X + radius && at.Y <= to.Y + radius)
                return;
        }

        Fx.Glow(dl, at, radius * 0.5f, new Vector4(0.92f, 0.96f, 1f, 1f), 0.5f);
    }
}

/// <summary>
/// A success around a window: a beam of light runs once around its edge, then the edge flashes and a pulse leaves it.
/// Age: seconds since the success.
/// </summary>
public static class EdgeBeam
{
    public const float Run = 1.2f, Flash = 0.5f;

    public static void Draw(float age)
    {
        if (!Fx.Enabled || age < 0 || age >= Run + Flash)
            return;
        var pad = Theme.Px(1.5f);
        var min = ImGui.GetWindowPos() + new Vector2(pad);
        var max = ImGui.GetWindowPos() + ImGui.GetWindowSize() - new Vector2(pad);
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min - Theme.Px(new Vector2(24, 24)), max + Theme.Px(new Vector2(24, 24)), false);

        if (age < Run)
        {
            // Ease in and out around the edge, a fading trail a quarter of it long behind the head.
            var k = age / Run;
            var head = k * k * (3 - 2 * k);
            const int Samples = 32;
            for (var i = 0; i < Samples; i++)
            {
                var a = head - i * 0.25f / Samples;
                var b = head - (i + 1) * 0.25f / Samples;
                if (b < 0)
                    break;
                var fade = 1 - i / (float)Samples;
                var from = OnEdge(min, max, a);
                var to = OnEdge(min, max, b);
                dl.AddLine(from, to, Theme.U32(Theme.Gold, 0.35f * fade), Theme.Px(6) * fade + Theme.Px(1));
                dl.AddLine(from, to, Theme.U32(Theme.GoldBright, fade), Theme.Px(2) * fade + Theme.Px(0.5f));
            }

            Fx.Glow(dl, OnEdge(min, max, head), Theme.Px(3), Theme.Gold, 1f);
        }
        else
        {
            var k = (age - Run) / Flash;
            var rounding = ImGui.GetStyle().WindowRounding;
            dl.AddRectFilled(min, max, Theme.U32(Theme.GoldBright, 0.22f * (1 - k)), rounding);
            dl.AddRect(min, max, Theme.U32(Theme.GoldBright, 1 - k), rounding, ImDrawFlags.None, Theme.Px(3));
            var grow = new Vector2(Theme.Px(20) * Fx.EaseOut(k));
            dl.AddRect(min - grow, max + grow, Theme.U32(Theme.Gold, 0.8f * (1 - k)), rounding + grow.X, ImDrawFlags.None, Theme.Px(2));
        }

        dl.PopClipRect();
    }

    // A point on the edge of a rectangle, 0..1 around it clockwise from the top left corner.
    private static Vector2 OnEdge(Vector2 min, Vector2 max, float t)
    {
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        var d = (t - MathF.Floor(t)) * 2 * (w + h);
        if (d < w)
            return new Vector2(min.X + d, min.Y);
        d -= w;
        if (d < h)
            return new Vector2(max.X, min.Y + d);
        d -= h;
        if (d < w)
            return new Vector2(max.X - d, max.Y);
        d -= w;
        return new Vector2(min.X, max.Y - d);
    }
}

