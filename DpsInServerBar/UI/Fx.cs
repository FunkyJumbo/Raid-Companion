using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DpsInServerBar.UI;

/// <summary>
/// The plugin's small animations, drawn with ImGui's draw lists: motes of aether rising, a constellation drawing itself,
/// a ring spreading out, sparks. All of them move by the time gone by (not by frame), and stay still when the player
/// turns the animations off (Appearance settings).
/// </summary>
public static class Fx
{
    /// <summary>The animations are on (Appearance settings).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Time since the last frame, in seconds (at most a twentieth: no jump after a freeze), at the animations' speed.</summary>
    public static float Delta => Math.Min(ImGui.GetIO().DeltaTime, 0.05f);

    /// <summary>The animations' clock, in seconds (ImGui's).</summary>
    public static float Now => (float)ImGui.GetTime();

    public static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    /// <summary>Eases out (fast, then slowing down): 0 to 1.</summary>
    public static float EaseOut(float t)
    {
        t = Clamp01(t);
        return 1 - (1 - t) * (1 - t) * (1 - t);
    }

    /// <summary>A heartbeat: mostly 0, a short beat up to 1 about once per second.</summary>
    public static float Beat(float time, float perSecond = 1.1f) =>
        MathF.Pow(MathF.Max(0f, MathF.Sin(time * MathF.Tau * perSecond)), 8f);

    /// <summary>A dot with a soft halo.</summary>
    public static void Glow(ImDrawListPtr dl, Vector2 at, float radius, Vector4 color, float alpha)
    {
        if (alpha <= 0.01f)
            return;
        dl.AddCircleFilled(at, radius * 3.2f, Theme.U32(color, alpha * 0.12f), 12);
        dl.AddCircleFilled(at, radius * 1.9f, Theme.U32(color, alpha * 0.22f), 12);
        dl.AddCircleFilled(at, radius, Theme.U32(Theme.Lighten(color, 0.35f), alpha), 10);
    }
}

/// <summary>Motes of aether rising slowly through an area, swaying and twinkling, fading in at the bottom and out at the top.</summary>
public sealed class AetherMotes
{
    private struct Mote
    {
        public Vector2 Pos;   // in the area, 0..1 across, pixels up from the bottom
        public float Speed, Size, Phase, Age;
    }

    private readonly List<Mote> motes = [];
    private readonly Random random = new();
    private readonly int count;

    public AetherMotes(int count = 60) => this.count = count;

    private Mote New(float height, bool anywhere) => new()
    {
        Pos = new Vector2((float)random.NextDouble(), anywhere ? (float)random.NextDouble() * height : -6f),
        Speed = 6f + (float)random.NextDouble() * 16f,
        Size = 0.7f + (float)random.NextDouble() * 1.5f,
        Phase = (float)random.NextDouble() * MathF.Tau,
        Age = anywhere ? 2f : 0f,
    };

    public void Draw(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 color)
    {
        var size = max - min;
        if (size.X < 4 || size.Y < 4)
            return;
        var scale = Theme.Px(1);
        var time = Fx.Now;
        var dt = Fx.Enabled ? Fx.Delta : 0f;
        while (motes.Count < count)
            motes.Add(New(size.Y, anywhere: true));

        dl.PushClipRect(min, max, true);
        for (var i = 0; i < motes.Count; i++)
        {
            var m = motes[i];
            m.Pos.Y += m.Speed * scale * dt;
            m.Pos.X += MathF.Sin(time * 0.8f + m.Phase) * 0.02f * dt;
            m.Age += dt;
            if (m.Pos.Y > size.Y + 6)
                m = New(size.Y, anywhere: false);
            motes[i] = m;

            var at = new Vector2(min.X + m.Pos.X * size.X, max.Y - m.Pos.Y);
            var fade = Fx.Clamp01(m.Age / 1.5f) * Fx.Clamp01((size.Y - m.Pos.Y) / (size.Y * 0.3f));
            var twinkle = 0.55f + 0.45f * MathF.Sin(time * 2f + m.Phase);
            Fx.Glow(dl, at, m.Size * scale, color, fade * twinkle * 0.8f);
        }

        dl.PopClipRect();
    }
}

/// <summary>
/// A constellation drawing itself: its stars light up one after the other, then the lines between them are traced.
/// <see cref="Reset"/> makes a new one (a page change); the same seed gives the same constellation.
/// </summary>
public sealed class Constellation
{
    private readonly List<(Vector2 At, float Phase)> stars = [];
    private readonly List<(int From, int To)> lines = [];
    private readonly List<(Vector2 At, float Phase)> dust = [];
    private float born = -100f;

    public void Reset(int seed)
    {
        var random = new Random(seed * 7919 + 13);
        stars.Clear();
        lines.Clear();
        dust.Clear();

        // 6 to 8 stars in the area (0..1 both ways), linked left to right, plus one cross line.
        var n = 6 + random.Next(3);
        var cx = 0.3f + (float)random.NextDouble() * 0.4f;
        for (var i = 0; i < n; i++)
            stars.Add((new Vector2(Math.Clamp(cx + ((float)random.NextDouble() - 0.5f) * 0.75f, 0.06f, 0.94f),
                                   0.15f + (float)random.NextDouble() * 0.7f), (float)random.NextDouble() * MathF.Tau));
        stars.Sort((a, b) => a.At.X.CompareTo(b.At.X));
        for (var i = 1; i < n; i++)
            lines.Add((i - 1, i));
        lines.Add((1, n - 2));

        for (var i = 0; i < 40; i++)
            dust.Add((new Vector2((float)random.NextDouble(), (float)random.NextDouble()), (float)random.NextDouble() * MathF.Tau));
        born = Fx.Enabled ? Fx.Now : -100f;
    }

    public void Draw(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 color)
    {
        if (stars.Count == 0)
            Reset(0);
        var size = max - min;
        var time = Fx.Now;
        var age = Fx.Enabled ? time - born : 100f;
        Vector2 At(Vector2 p) => min + p * size;

        dl.PushClipRect(min, max, true);
        foreach (var (at, phase) in dust)
            dl.AddRectFilled(At(at), At(at) + new Vector2(Theme.Px(1)), Theme.U32(Theme.Text, 0.12f + 0.1f * MathF.Sin(time * 1.5f + phase)));

        for (var i = 0; i < lines.Count; i++)
        {
            var k = Fx.EaseOut((age - 0.35f - i * 0.16f) / 0.4f);
            if (k <= 0)
                continue;
            var a = At(stars[lines[i].From].At);
            var b = At(stars[lines[i].To].At);
            dl.AddLine(a, a + (b - a) * k, Theme.U32(color, 0.4f), Theme.Px(1));
        }

        for (var i = 0; i < stars.Count; i++)
        {
            var (at, phase) = stars[i];
            var k = Fx.EaseOut((age - i * 0.08f) / 0.4f);
            var twinkle = 0.75f + 0.25f * MathF.Sin(time * 3f + phase);
            Fx.Glow(dl, At(at), Theme.Px(2f) * (0.4f + 0.6f * k), color, k * twinkle);
        }

        dl.PopClipRect();
    }
}

/// <summary>Rings spreading out from a point and fading (the start of a pull).</summary>
public sealed class RingWave
{
    private float started = -100f;
    private const float Duration = 1.4f;

    public void Start()
    {
        if (Fx.Enabled)
            started = Fx.Now;
    }

    public bool Active => Fx.Now - started < Duration;

    public void Draw(ImDrawListPtr dl, Vector2 center, float reach, Vector4 color)
    {
        var age = Fx.Now - started;
        if (age >= Duration || age < 0)
            return;
        for (var r = 0; r < 2; r++)
        {
            var k = Fx.Clamp01(age / Duration - r * 0.12f);
            if (k <= 0)
                continue;
            var eased = Fx.EaseOut(k);
            dl.AddCircle(center, Theme.Px(14) + eased * reach, Theme.U32(color, (1 - k) * (r == 0 ? 0.8f : 0.35f)), 64,
                         Theme.Px(0.5f + 2f * (1 - k)));
        }
    }
}

/// <summary>
/// The damage bars of a meter (Live table, compact meter): each one glides to its width, the player passing another
/// one flashes and throws sparks from the bar's end, a light passes over the top one. Its state follows one pull.
/// </summary>
public sealed class BarAnimator
{
    private readonly Dictionary<string, (float Shown, int Rank, float Rose)> bars = new();
    private readonly Dictionary<string, (int Deaths, float At)> deaths = new();
    private readonly Dictionary<string, (bool Dead, float At)> life = new();
    private DateTime pull;

    private void Follow(DateTime pullStart)
    {
        if (pullStart == pull)
            return;
        pull = pullStart;
        bars.Clear();
        deaths.Clear();
    }

    /// <summary>Seconds since the player's last death in the pull (large when none since the meter saw them).</summary>
    public float SinceDeath(DateTime pullStart, string name, int count)
    {
        Follow(pullStart);
        if (!deaths.TryGetValue(name, out var d))
            d = (count, -100f);
        if (count > d.Deaths && Fx.Enabled)
            d.At = Fx.Now;
        d.Deaths = count;
        deaths[name] = d;
        return Fx.Now - d.At;
    }

    private const float DeathLength = 1.2f;

    /// <summary>How long a raised player's bar takes to come back to life (seconds).</summary>
    public const float RaiseLength = 1.4f;

    private static readonly Vector4 Ash = new(0.42f, 0.43f, 0.47f, 1f);

    /// <summary>
    /// A player's state on their bar: how dead it shows (0..1: greyed, the break forming just after the death) and the
    /// seconds since they were raised (large when they were not, or before the meter saw them dead).
    /// </summary>
    public (float Dead, float SinceRaise) Life(DateTime pullStart, string name, bool dead)
    {
        Follow(pullStart);
        if (!life.TryGetValue(name, out var l))
            l = (dead, -100f);
        else if (l.Dead != dead)
            l = (dead, Fx.Enabled ? Fx.Now : -100f);
        life[name] = l;
        var age = Fx.Now - l.At;
        return dead ? (Fx.Clamp01(age / 0.35f), 100f) : (0f, age);
    }

    /// <summary>The bar's color for a player's state: grey while dead, its color coming back once raised.</summary>
    public static Vector4 LifeColor(Vector4 color, float dead, float sinceRaise)
    {
        if (dead > 0)
            return Vector4.Lerp(color, Ash, dead);
        return sinceRaise is >= 0 and < RaiseLength ? Vector4.Lerp(Ash, color, Fx.EaseOut(sinceRaise / RaiseLength)) : color;
    }

    /// <summary>A dead player's bar, broken: a jagged crack across it, the piece after it sunk a little in the dark.</summary>
    public static void Broken(ImDrawListPtr dl, Vector2 min, Vector2 max, float dead)
    {
        var width = max.X - min.X;
        if (dead <= 0 || width < Theme.Px(12))
            return;
        var height = max.Y - min.Y;
        var x = min.X + Math.Clamp(width * 0.58f, Theme.Px(6), width - Theme.Px(6));
        dl.PushClipRect(min - new Vector2(Theme.Px(2)), max + new Vector2(Theme.Px(2)), false);
        dl.AddRectFilled(new Vector2(x, min.Y), max, Theme.U32(new Vector4(0, 0, 0, 1), 0.22f * dead), Theme.Px(3), ImDrawFlags.RoundCornersRight);
        var points = new[]
        {
            new Vector2(x + Theme.Px(2), min.Y), new Vector2(x - Theme.Px(3), min.Y + height * 0.3f),
            new Vector2(x + Theme.Px(2), min.Y + height * 0.55f), new Vector2(x - Theme.Px(2), min.Y + height * 0.78f),
            new Vector2(x + Theme.Px(1), max.Y),
        };
        var reach = dead * (points.Length - 1);
        var crack = Theme.U32(new Vector4(0.05f, 0.05f, 0.07f, 1), 0.9f);
        var edge = Theme.U32(Theme.Text, 0.35f * dead);
        for (var p = 0; p < points.Length - 1 && p < reach; p++)
        {
            var to = Vector2.Lerp(points[p], points[p + 1], Math.Min(1, reach - p));
            dl.AddLine(points[p], to, crack, Theme.Px(2.2f));
            dl.AddLine(points[p] + new Vector2(Theme.Px(1.6f), 0), to + new Vector2(Theme.Px(1.6f), 0), edge, Theme.Px(0.8f));
        }

        // A small shard off the crack, once it is through.
        if (dead >= 1)
            dl.AddTriangleFilled(points[2], points[2] + new Vector2(Theme.Px(5), Theme.Px(-2)), points[2] + new Vector2(Theme.Px(4), Theme.Px(3)), crack);
        dl.PopClipRect();
    }

    /// <summary>A player raised: a gold light rising up their row while their bar recolors from the left.</summary>
    public static void Raise(ImDrawListPtr dl, Vector2 rowMin, Vector2 rowMax, float sinceRaise)
    {
        if (!Fx.Enabled || sinceRaise is < 0 or >= RaiseLength)
            return;
        var k = sinceRaise / RaiseLength;
        var width = rowMax.X - rowMin.X;
        var height = rowMax.Y - rowMin.Y;
        var window = ImGui.GetWindowPos();
        dl.PushClipRect(window, window + ImGui.GetWindowSize(), false);

        // The recolor front, a gold band behind it fading.
        var front = rowMin.X + width * Fx.EaseOut(Math.Min(1, k * 1.4f));
        var clear = Theme.U32(Theme.GoldBright, 0f);
        var lit = Theme.U32(Theme.GoldBright, 0.35f * (1 - k));
        dl.AddRectFilledMultiColor(rowMin, new Vector2(front, rowMax.Y), clear, lit, lit, clear);

        // The light rising: motes coming up from under the row and through it.
        for (var i = 0; i < 9; i++)
        {
            var rise = Fx.Clamp01(k * 1.5f - i % 3 * 0.15f);
            if (rise <= 0 || rise >= 1)
                continue;
            var x = rowMin.X + width * ((i * 0.137f + 0.06f) % 1f);
            var y = rowMax.Y + Theme.Px(4) - rise * (height + Theme.Px(14)) - i * 7 % 5;
            Fx.Glow(dl, new Vector2(x, y), Theme.Px(1.6f), Theme.GoldBright, (1 - rise) * 0.9f);
        }

        dl.PopClipRect();
    }

    /// <summary>A side shake of the row of a player who just died (pixels).</summary>
    public static float Shake(float sinceDeath) =>
        sinceDeath is >= 0 and < 0.3f ? MathF.Sin(sinceDeath * 70f) * Theme.Px(3) * (1 - sinceDeath / 0.3f) : 0;

    /// <summary>A player's death: a red ring out of the job icon and the row flashing red, fading.</summary>
    public static void DeathOverlay(ImDrawListPtr dl, Vector2 icon, Vector2 rowMin, Vector2 rowMax, float sinceDeath)
    {
        if (sinceDeath < 0 || sinceDeath >= DeathLength)
            return;
        var k = sinceDeath / DeathLength;
        var window = ImGui.GetWindowPos();
        dl.PushClipRect(window, window + ImGui.GetWindowSize(), false);
        dl.AddRectFilled(rowMin, rowMax, Theme.U32(Theme.Bad, 0.3f * (1 - k)), Theme.Px(3));
        dl.AddCircle(icon, Theme.Px(8) + Fx.EaseOut(k) * Theme.Px(70), Theme.U32(Theme.Bad, 1 - k), 48, Theme.Px(0.5f + 2 * (1 - k)));
        dl.PopClipRect();
    }

    public Sparks Sparks { get; } = new();

    /// <summary>
    /// The width to draw for a player's bar (share of the top, 0..1) and its flash (0..1). Rank: the player's place
    /// (1 the top, 0 for no rank); start, width and height: where the whole bar would be.
    /// </summary>
    public (float Share, float Flash) Bar(DateTime pullStart, string name, float target, int rank, Vector2 start, float width, float height, bool you)
    {
        Follow(pullStart);
        if (!Fx.Enabled)
            return (target, 0);

        var now = Fx.Now;
        if (!bars.TryGetValue(name, out var bar))
            bar = (target, rank, -100f);

        bar.Shown += (target - bar.Shown) * Math.Min(1f, Fx.Delta * 6f);
        if (you && rank > 0 && bar.Rank > 0 && rank < bar.Rank)
        {
            bar.Rose = now;
            Sparks.Burst(start + new Vector2(width * bar.Shown, height / 2), Theme.Gold);
        }

        if (rank > 0)
            bar.Rank = rank;
        bars[name] = bar;
        return (bar.Shown, Fx.Clamp01(1 - (now - bar.Rose) / 1.2f));
    }

    /// <summary>
    /// Over a bar: its gold flash, and the light passing on your own bar every few seconds. Drawn over the whole bar,
    /// even across table cells.
    /// </summary>
    public static void Overlay(ImDrawListPtr dl, Vector2 min, Vector2 max, float flash, bool top)
    {
        if (!Fx.Enabled || max.X - min.X < 2)
            return;
        dl.PushClipRect(min, max, false);
        if (flash > 0)
            dl.AddRectFilled(min, max, Theme.U32(Theme.Gold, 0.35f * flash), Theme.Px(3));

        const float Every = 4f, Pass = 1.3f;
        var t = Fx.Now % Every / Pass;
        if (top && t <= 1)
        {
            var width = Theme.Px(40);
            var x = min.X - width + (max.X - min.X + width * 2) * t;
            var clear = Theme.U32(Theme.Text, 0f);
            var light = Theme.U32(Theme.Text, 0.28f);
            dl.AddRectFilledMultiColor(new Vector2(x - width / 2, min.Y), new Vector2(x, max.Y), clear, light, light, clear);
            dl.AddRectFilledMultiColor(new Vector2(x, min.Y), new Vector2(x + width / 2, max.Y), light, clear, clear, light);
        }

        dl.PopClipRect();
    }
}

/// <summary>Sparks thrown out from a point, falling a little and fading (a player passing another one).</summary>
public sealed class Sparks
{
    private struct Spark
    {
        public Vector2 Pos, Speed;
        public float Life, MaxLife;
        public Vector4 Color;
    }

    private readonly List<Spark> sparks = [];
    private readonly Random random = new();

    public bool Any => sparks.Count > 0;

    public void Burst(Vector2 at, Vector4 color, int count = 24, float speed = 1f, float life = 0.8f)
    {
        if (!Fx.Enabled)
            return;
        for (var i = 0; i < count; i++)
        {
            var angle = (float)random.NextDouble() * MathF.Tau;
            var v = Theme.Px(40f + (float)random.NextDouble() * 90f) * speed;
            var l = life * (0.7f + 0.3f * (float)random.NextDouble());
            sparks.Add(new Spark { Pos = at, Speed = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * v, Life = l, MaxLife = l, Color = color });
        }
    }

    public void Draw(ImDrawListPtr dl)
    {
        var dt = Fx.Delta;
        var size = new Vector2(Theme.Px(2));
        for (var i = sparks.Count - 1; i >= 0; i--)
        {
            var s = sparks[i];
            s.Life -= dt;
            if (s.Life <= 0)
            {
                sparks.RemoveAt(i);
                continue;
            }

            s.Pos += s.Speed * dt;
            s.Speed.Y += Theme.Px(60) * dt;
            s.Speed *= 1f - Math.Min(0.9f, dt * 1.2f); // air: slowing down
            sparks[i] = s;
            dl.AddRectFilled(s.Pos, s.Pos + size, Theme.U32(Theme.Lighten(s.Color, 0.3f), s.Life / s.MaxLife));
        }
    }
}

/// <summary>
/// A value whose characters roll when it changes: each character that changed slides up and out while the new one
/// comes up from below (the figures of the cards).
/// </summary>
public static class RollingText
{
    private const float Roll = 0.35f;
    private static readonly Dictionary<uint, (string Value, string Previous, float Changed)> Values = new();

    public static void Draw(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint color, string value, uint key)
    {
        if (!Values.TryGetValue(key, out var state))
            state = (value, value, -100f);
        if (state.Value != value)
            state = (value, state.Value, Fx.Enabled ? Fx.Now : -100f);
        Values[key] = state;

        var t = (Fx.Now - state.Changed) / Roll;
        if (t >= 1 || t < 0)
        {
            dl.AddText(font, size, pos, color, value);
            return;
        }

        var e = Fx.EaseOut(t);
        var scale = size / ImGui.GetFontSize();
        var width = ImGui.CalcTextSize(value).X * scale;
        dl.PushClipRect(pos, pos + new Vector2(width + Theme.Px(4), size + Theme.Px(2)), true);
        var x = pos.X;
        var previous = state.Previous;
        for (var i = 0; i < value.Length; i++)
        {
            // Matched from the right: the units stay under the units.
            var j = previous.Length - (value.Length - i);
            var now = value[i].ToString();
            var before = j >= 0 ? previous[j] : ' ';
            var charWidth = ImGui.CalcTextSize(now).X * scale;
            if (before == value[i])
            {
                dl.AddText(font, size, new Vector2(x, pos.Y), color, now);
            }
            else
            {
                var lift = size * 0.7f;
                var alpha = (color >> 24) / 255f;
                dl.AddText(font, size, new Vector2(x, pos.Y + lift * (1 - e)), WithAlpha(color, alpha * e), now);
                if (before != ' ')
                    dl.AddText(font, size, new Vector2(x, pos.Y - lift * e), WithAlpha(color, alpha * (1 - e)), before.ToString());
            }

            x += charWidth;
        }

        dl.PopClipRect();
    }

    private static uint WithAlpha(uint color, float alpha) => (color & 0x00FFFFFF) | ((uint)(Fx.Clamp01(alpha) * 255) << 24);
}

/// <summary>A ribbon crossing an area with a title (an ultimate's new phase): it opens, a light passes, it fades.</summary>
public sealed class Banner
{
    private const float Duration = 2.4f;
    private float started = -100f;
    private string text = string.Empty;

    public void Start(string title)
    {
        if (!Fx.Enabled)
            return;
        text = title;
        started = Fx.Now;
    }

    public void Draw(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        var age = Fx.Now - started;
        if (age < 0 || age >= Duration || max.X - min.X < 10)
            return;
        var k = age / Duration;
        var open = k < 0.15f ? Fx.EaseOut(k / 0.15f) : k > 0.8f ? 1 - Fx.EaseOut((k - 0.8f) / 0.2f) : 1;
        var center = (min.Y + max.Y) / 2;
        var half = Theme.Px(20) * open;
        var top = new Vector2(min.X, center - half);
        var bottom = new Vector2(max.X, center + half);
        var width = max.X - min.X;

        dl.PushClipRect(min - new Vector2(0, Theme.Px(30)), max + new Vector2(0, Theme.Px(30)), false);
        dl.AddRectFilled(top, bottom, Theme.U32(Theme.Ink with { W = 1f }, 0.85f * open));
        dl.AddRectFilled(top, bottom, Theme.U32(Theme.Gold, 0.16f * open));
        var line = width * Fx.EaseOut(k * 4);
        dl.AddLine(top, top + new Vector2(line, 0), Theme.U32(Theme.Gold, 0.9f * open), Theme.Px(1.5f));
        dl.AddLine(bottom, bottom - new Vector2(line, 0), Theme.U32(Theme.Gold, 0.9f * open), Theme.Px(1.5f));

        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize() * 1.6f;
        var textWidth = ImGui.CalcTextSize(text).X * 1.6f;
        var slide = (1 - open) * Theme.Px(40);
        dl.AddText(font, size, new Vector2(min.X + (width - textWidth) / 2 + slide, center - size / 2),
                   Theme.U32(Theme.GoldBright, open), text);

        // The light crossing it.
        var pass = (k - 0.15f) / 0.45f;
        if (pass is > 0 and < 1)
        {
            var w = Theme.Px(50);
            var x = min.X - w + (width + w * 2) * pass;
            var clear = Theme.U32(Theme.Text, 0f);
            var light = Theme.U32(Theme.Text, 0.3f * open);
            dl.AddRectFilledMultiColor(new Vector2(x - w / 2, top.Y), new Vector2(x, bottom.Y), clear, light, light, clear);
            dl.AddRectFilledMultiColor(new Vector2(x, top.Y), new Vector2(x + w / 2, bottom.Y), light, clear, clear, light);
        }

        dl.PopClipRect();
    }
}

/// <summary>A ring filling to a share (GCD uptime), gliding to it, a comet at its head.</summary>
public static class Ring
{
    private static readonly Dictionary<uint, float> Shown = new();

    public static void Draw(ImDrawListPtr dl, Vector2 center, float radius, float share, Vector4 color, uint key)
    {
        share = Fx.Clamp01(share);
        var shown = Shown.TryGetValue(key, out var s) ? s : 0f;
        shown = Fx.Enabled ? shown + (share - shown) * Math.Min(1f, Fx.Delta * 3f) : share;
        Shown[key] = shown;

        var thickness = Math.Max(Theme.Px(2.5f), radius * 0.28f);
        dl.AddCircle(center, radius, Theme.U32(Theme.Line, 0.9f), 32, thickness);
        if (shown <= 0.002f)
            return;
        var a0 = -MathF.PI / 2;
        var a1 = a0 + shown * MathF.Tau;
        dl.PathArcTo(center, radius, a0, a1, 32);
        dl.PathStroke(Theme.U32(color, 0.9f), ImDrawFlags.None, thickness);
        if (Fx.Enabled)
        {
            var head = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            Fx.Glow(dl, head, thickness * 0.55f, color, 0.6f + 0.4f * MathF.Sin(Fx.Now * 4f));
        }
    }
}
