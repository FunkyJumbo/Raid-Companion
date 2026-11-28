using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DpsInServerBar.UI;

/// <summary>
/// A view's background, behind its content and its ambience particles: a glow in a corner (Live), a corridor of grids
/// (Progression), a hive of aether in the corners (Mitigation plans, BiS sets, the opener editor and practice, compact
/// too), a grid of hexagons fading from the right side (Settings). Faint, kept out of the tables and graphs where it is
/// made of points.
/// </summary>
public static class Backdrop
{
    public enum Kind
    {
        CornerGlow,
        Corridor,
        Hive,
        EdgeGrid,
    }

    public static bool Enabled { get; set; } = true;

    /// <summary>Call at the start of a window's Draw, right after <see cref="Ambience.Draw"/>.</summary>
    public static void Draw(Kind kind)
    {
        if (!Enabled || !Fx.Enabled)
            return;
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (size.X < 50 || size.Y < 50)
            return;

        var dl = ImGui.GetWindowDrawList();
        var covered = Ambience.CoveredHere();
        // ImGui clips a window's drawing half its padding inside its sides: the background would leave a bare strip
        // there. It goes to the window's border instead (same top and bottom as ImGui's).
        var border = ImGui.GetStyle().WindowBorderSize;
        dl.PushClipRect(new Vector2(min.X + border, dl.GetClipRectMin().Y), new Vector2(min.X + size.X - border, dl.GetClipRectMax().Y), false);
        switch (kind)
        {
            case Kind.CornerGlow:
                SoftDisc(dl, min, Math.Max(size.X, size.Y) * 0.7f, Theme.Gold, 0.16f); // only the quarter inside the window shows
                break;
            case Kind.Corridor:
                DrawCorridor(dl, min, size);
                break;
            case Kind.Hive:
                DrawHive(dl, min, size, covered);
                break;
            case Kind.EdgeGrid:
                DrawEdgeGrid(dl, min, size, covered);
                break;
        }

        dl.PopClipRect();
    }

    private static bool Hidden(Vector2 p, (Vector2 Min, Vector2 Max)[] covered)
    {
        foreach (var (from, to) in covered)
        {
            if (p.X >= from.X && p.Y >= from.Y && p.X <= to.X && p.Y <= to.Y)
                return true;
        }

        return false;
    }

    // A stable random number for a seed, 0 to 1.
    private static float Hash(int seed)
    {
        var x = (uint)seed * 2654435761u;
        x ^= x >> 15;
        x *= 2246822519u;
        x ^= x >> 13;
        return (x & 0xFFFFFF) / (float)0x1000000;
    }

    // ---- 130: the grid on the floor and the same on the ceiling, meeting far away: a corridor you walk along.
    private static void DrawCorridor(ImDrawListPtr dl, Vector2 min, Vector2 size)
    {
        var center = min + size / 2;
        var near = Theme.Px(8); // half the height of the far end
        var color = Theme.Gold;

        // A soft band of light across the far end (gradients, no visible edge).
        var glow = Theme.U32(color, 0.09f);
        var none = Theme.U32(color, 0f);
        var reach = Theme.Px(36);
        dl.AddRectFilledMultiColor(new Vector2(min.X, center.Y - reach), new Vector2(min.X + size.X, center.Y), none, none, glow, glow);
        dl.AddRectFilledMultiColor(new Vector2(min.X, center.Y), new Vector2(min.X + size.X, center.Y + reach), glow, glow, none, none);

        // The lines running to the far end, on the floor and the ceiling, fading toward it.
        var spread = size.X / 6.4f;
        for (var i = -12; i <= 12; i++)
        {
            for (var side = -1; side <= 1; side += 2)
            {
                var far = new Vector2(center.X + i * Theme.Px(4), center.Y + side * near);
                var end = new Vector2(center.X + i * spread, side > 0 ? min.Y + size.Y : min.Y);
                const int steps = 6;
                for (var s = 0; s < steps; s++)
                {
                    var a = Vector2.Lerp(far, end, s / (float)steps);
                    var b = Vector2.Lerp(far, end, (s + 1) / (float)steps);
                    dl.AddLine(a, b, Theme.U32(color, 0.12f * (s + 1) / steps), Theme.Px(1));
                }
            }
        }

        // The lines across, coming closer and brighter, on both.
        const int across = 8;
        var depth = size.Y / 2 - near;
        for (var i = 0; i < across; i++)
        {
            var f = (i + Fx.Now * 0.3f) % across / across;
            var d = near + MathF.Pow(f, 2.2f) * depth;
            var line = Theme.U32(color, 0.15f * f);
            dl.AddLine(new Vector2(min.X, center.Y + d), new Vector2(min.X + size.X, center.Y + d), line, Theme.Px(1));
            dl.AddLine(new Vector2(min.X, center.Y - d), new Vector2(min.X + size.X, center.Y - d), line, Theme.Px(1));
        }
    }

    // ---- 163 (still): a fine grid of hexagons along the whole right side, fading toward the left like a gradient.
    private static void DrawEdgeGrid(ImDrawListPtr dl, Vector2 min, Vector2 size, (Vector2 Min, Vector2 Max)[] covered)
    {
        // Flat-topped hexagons: columns 1.5 radius apart, every other one half a hexagon lower.
        var radius = Theme.Px(16);
        var across = radius * 1.5f;
        var tall = radius * MathF.Sqrt(3);
        var right = min.X + size.X;
        var reach = size.X * 0.45f;
        var columns = (int)(reach / across) + 2;
        var rows = (int)(size.Y / tall) + 2;
        Span<Vector2> corners = stackalloc Vector2[6];
        for (var cx = 0; cx < columns; cx++)
        {
            var x = right - cx * across;
            var fade = 1 - (right - x) / reach;
            if (fade <= 0)
                continue;
            fade *= fade; // a softer gradient toward the left
            var color = Theme.U32(Theme.Crystal, 0.11f * fade);
            var shift = cx % 2 == 1 ? tall / 2 : 0;
            for (var cy = -1; cy < rows; cy++)
            {
                var center = new Vector2(x, min.Y + cy * tall + shift);
                if (Hidden(center, covered))
                    continue;
                for (var k = 0; k < 6; k++)
                {
                    var (sin, cos) = MathF.SinCos(k * MathF.PI / 3);
                    corners[k] = center + new Vector2(cos, sin) * radius;
                }

                // Its three lower sides: the other three are the lower sides of its neighbours above (no side drawn twice).
                for (var k = 0; k < 3; k++)
                    dl.AddLine(corners[k], corners[k + 1], color, Theme.Px(1));
            }
        }
    }

    // ---- 118: a hive of hexagons in the corners of the window only, fading toward the middle; a few cells light up.
    private static void DrawHive(ImDrawListPtr dl, Vector2 min, Vector2 size, (Vector2 Min, Vector2 Max)[] covered)
    {
        var r = Theme.Px(17);
        var width = r * MathF.Sqrt(3);
        var rows = (int)(size.Y / (r * 1.5f)) + 2;
        var columns = (int)(size.X / width) + 2;
        Span<Vector2> corners = stackalloc Vector2[6];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var center = min + new Vector2(column * width + (row % 2) * width / 2, row * r * 1.5f);
                // Strong in the corners, a little along the sides, nothing in the middle.
                var dx = Math.Min(center.X - min.X, min.X + size.X - center.X) / size.X;
                var dy = Math.Min(center.Y - min.Y, min.Y + size.Y - center.Y) / size.Y;
                var edge = Fx.Clamp01(1 - Math.Min(dx, dy) * 3.2f - Math.Max(dx, dy) * 1.2f);
                if (edge <= 0.01f || Hidden(center, covered))
                    continue;
                var phase = Hash(row * 977 + column * 131);
                var lit = MathF.Pow(Math.Max(0, MathF.Sin(Fx.Now * 0.7f + phase * 40)), 10);
                for (var c = 0; c < 6; c++)
                {
                    var angle = MathF.PI / 3 * c + MathF.PI / 6;
                    corners[c] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r * 0.9f;
                }

                if (lit > 0.02f)
                {
                    for (var c = 1; c < 5; c++)
                        dl.AddTriangleFilled(corners[0], corners[c], corners[c + 1], Theme.U32(Theme.Crystal, 0.065f * lit * edge));
                }

                var line = Theme.U32(Theme.Crystal, (0.075f + 0.26f * lit) * edge);
                for (var c = 0; c < 6; c++)
                    dl.AddLine(corners[c], corners[(c + 1) % 6], line, Theme.Px(1));
            }
        }
    }

    // A disc of light fading smoothly from its middle to nothing at its edge (no ring: one fan of vertices).
    private static void SoftDisc(ImDrawListPtr dl, Vector2 center, float radius, Vector4 color, float alpha)
    {
        const int segments = 48;
        var uv = ImGui.GetFontTexUvWhitePixel();
        var inner = Theme.U32(color, alpha);
        var outer = Theme.U32(color, 0f);
        dl.PrimReserve(segments * 3, segments + 1);
        var first = dl.VtxCurrentIdx;
        dl.PrimWriteVtx(center, uv, inner);
        for (var i = 0; i < segments; i++)
        {
            var angle = i / (float)segments * MathF.Tau;
            dl.PrimWriteVtx(center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius, uv, outer);
        }

        for (var i = 0; i < segments; i++)
        {
            dl.PrimWriteIdx((ushort)first);
            dl.PrimWriteIdx((ushort)(first + 1 + i));
            dl.PrimWriteIdx((ushort)(first + 1 + (i + 1) % segments));
        }
    }
}
