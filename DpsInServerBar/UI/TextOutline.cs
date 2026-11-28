using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace DpsInServerBar.UI;

/// <summary>
/// A dark outline around the grey text of the plugin's windows (glass effect: the details, hints, job names, times...),
/// which a bright game behind them washes out; the white and colored text is left as it is. ImGui draws no outline, so
/// once a window is drawn, each grey letter of its draw lists gets copies of itself, shifted a pixel around it in a dark
/// color, drawn just before it, and the faintest of them a little less faint. The icons are left as they are.
/// </summary>
public static unsafe class TextOutline
{
    public static bool Enabled { get; set; }

    // The outline's color (alpha taken from each letter), set by the theme.
    public static Vector3 Color { get; set; }

    // The theme's greys this frame (muted and faint text), RGB only: the letters of these colors get the outline.
    private static uint muted, faint;

    // Where the icons' glyphs are in the font atlas (their top left UV): an icon gets no outline.
    private static readonly HashSet<long> IconUvs = new();
    private static (nint Font, int Count) iconsFor;

    // The least opacity of a grey letter with the outline on.
    private const float LeastAlpha = 0.7f;

    private static readonly HashSet<nint> Atlases = new();
    private static ImDrawVert[] vertices = new ImDrawVert[4096];
    private static ushort[] indices = new ushort[8192];
    private static ImDrawCmd[] commands = new ImDrawCmd[64];
    private static ImDrawVert[] outVertices = new ImDrawVert[8192];
    private static ushort[] outIndices = new ushort[16384];
    private static readonly List<uint> Offsets = new();
    private static readonly Dictionary<uint, (int Size, int Extra, int Start, int Cursor)> Groups = new();

    private static readonly Vector2[] Around =
    [
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1), new(-1, -1), new(1, -1), new(-1, 1), new(1, 1),
    ];

    /// <summary>After a window is drawn (ImGui.End done): outlines the text of it and its child windows.</summary>
    public static void Apply(string windowName)
    {
        if (!Enabled)
            return;
        try
        {
            var target = ImGuiP.FindWindowByName(windowName);
            if (target.Handle == null || !target.Active)
                return;

            muted = ImGui.ColorConvertFloat4ToU32(Theme.TextMuted) & 0xFFFFFF;
            faint = ImGui.ColorConvertFloat4ToU32(Theme.TextFaint) & 0xFFFFFF;
            ReadIcons();
            Atlases.Clear();
            ref var fontTextures = ref ImGui.GetIO().Fonts.Textures;
            for (var i = 0; i < fontTextures.Size; i++)
                Atlases.Add((nint)fontTextures.Data[i].TexID.Handle);

            ref var windows = ref ImGui.GetCurrentContext().Windows;
            for (var i = 0; i < windows.Size; i++)
            {
                var w = windows.Data[i];
                if (w.Handle == null || w.RootWindow.Handle != target.Handle || !w.Active || w.Hidden
                    || (w.Flags & ImGuiWindowFlags.Tooltip) != 0)
                    continue;
                Outline(w.DrawList);
            }
        }
        catch (Exception)
        {
            // Never break the window for an outline.
            Enabled = false;
        }
    }

    private static void Outline(ImDrawListPtr dl)
    {
        ref var vb = ref dl.VtxBuffer;
        ref var ib = ref dl.IdxBuffer;
        ref var cb = ref dl.CmdBuffer;
        int vn = vb.Size, inn = ib.Size, cn = cb.Size;
        if (inn == 0 || vn == 0 || cn == 0)
            return;

        Grow(ref vertices, vn);
        Grow(ref indices, inn);
        Grow(ref commands, cn);
        new Span<ImDrawVert>(vb.Data, vn).CopyTo(vertices);
        new Span<ushort>(ib.Data, inn).CopyTo(indices);
        new Span<ImDrawCmd>(cb.Data, cn).CopyTo(commands);

        // The textures of the fonts: Dalamud's atlas, and any texture drawing flat shapes (only an atlas does).
        for (var c = 0; c < cn; c++)
        {
            ref var cmd = ref commands[c];
            if (cmd.UserCallback != null || Atlases.Contains(Tex(cmd)))
                continue;
            var end = (int)Math.Min(cmd.IdxOffset + cmd.ElemCount, (uint)inn);
            for (var j = (int)cmd.IdxOffset; j + 2 < end; j += 3)
            {
                var o = (int)cmd.VtxOffset;
                var a = o + indices[j];
                var b = o + indices[j + 1];
                var d = o + indices[j + 2];
                if (a >= vn || b >= vn || d >= vn)
                    break;
                if (vertices[a].Uv == vertices[b].Uv && vertices[b].Uv == vertices[d].Uv)
                {
                    Atlases.Add(Tex(cmd));
                    break;
                }
            }
        }

        // The vertices of each vertex range (one per VtxOffset), and the outline's vertices each needs.
        Offsets.Clear();
        Groups.Clear();
        for (var c = 0; c < cn; c++)
        {
            if (commands[c].UserCallback == null && !Groups.ContainsKey(commands[c].VtxOffset))
            {
                Groups[commands[c].VtxOffset] = default;
                Offsets.Add(commands[c].VtxOffset);
            }
        }

        Offsets.Sort();
        for (var g = 0; g < Offsets.Count; g++)
        {
            var size = (int)((g + 1 < Offsets.Count ? Offsets[g + 1] : (uint)vn) - Offsets[g]);
            Groups[Offsets[g]] = (size, 0, 0, 0);
        }

        var total = 0;
        for (var c = 0; c < cn; c++)
        {
            ref var cmd = ref commands[c];
            if (cmd.UserCallback != null || !Atlases.Contains(Tex(cmd)))
                continue;
            var glyphs = 0;
            var end = (int)Math.Min(cmd.IdxOffset + cmd.ElemCount, (uint)inn);
            for (var j = (int)cmd.IdxOffset; j + 2 < end;)
            {
                if (j + 5 < end && Glyph(j, (int)cmd.VtxOffset, vn))
                {
                    glyphs++;
                    j += 6;
                }
                else
                {
                    j += 3;
                }
            }

            var group = Groups[cmd.VtxOffset];
            group.Extra += glyphs * 4 * Around.Length;
            Groups[cmd.VtxOffset] = group;
            total += glyphs;
        }

        if (total == 0)
            return;

        // Where each range goes: its vertices, then its outline's (none when that would pass 16-bit indices).
        var newVn = 0;
        foreach (var offset in Offsets)
        {
            var group = Groups[offset];
            if (group.Size + group.Extra > ushort.MaxValue)
                group.Extra = -1;
            group.Start = newVn;
            group.Cursor = newVn + group.Size;
            newVn += group.Size + Math.Max(0, group.Extra);
            Groups[offset] = group;
        }

        Grow(ref outVertices, newVn);
        Grow(ref outIndices, inn + total * 6 * Around.Length);
        foreach (var offset in Offsets)
        {
            var group = Groups[offset];
            Array.Copy(vertices, (int)offset, outVertices, group.Start, group.Size);
        }

        var px = Math.Max(1f, MathF.Round(ImGuiHelpers.GlobalScale));
        var shade = Color;
        var newIn = 0;
        for (var c = 0; c < cn; c++)
        {
            ref var cmd = ref commands[c];
            var from = (int)cmd.IdxOffset;
            var end = (int)Math.Min(cmd.IdxOffset + cmd.ElemCount, (uint)inn);
            cmd.IdxOffset = (uint)newIn;
            if (cmd.UserCallback != null)
            {
                for (var j = from; j < end; j++)
                    outIndices[newIn++] = indices[j];
                cmd.ElemCount = (uint)(newIn - cmd.IdxOffset);
                continue;
            }

            var source = (int)cmd.VtxOffset;
            var group = Groups[cmd.VtxOffset];
            var outline = group.Extra > 0 && Atlases.Contains(Tex(cmd));
            for (var j = from; j < end;)
            {
                if (outline && j + 5 < end && Glyph(j, source, vn))
                {
                    var first = source + indices[j];
                    var col = vertices[first].Col;
                    var alpha = MathF.Max((col >> 24) / 255f, LeastAlpha);
                    var color = ImGui.GetColorU32(new Vector4(shade, alpha * 0.85f));

                    // The letter itself: not fainter than LeastAlpha.
                    var raised = (col & 0xFFFFFF) | ((uint)(alpha * 255) << 24);
                    for (var k = 0; k < 4; k++)
                        outVertices[group.Start + indices[j] + k].Col = raised;
                    foreach (var shift in Around)
                    {
                        var local = (ushort)(group.Cursor - group.Start);
                        for (var k = 0; k < 4; k++)
                        {
                            var v = vertices[first + k];
                            outVertices[group.Cursor++] = new ImDrawVert(v.Pos + shift * px, v.Uv, color);
                        }

                        outIndices[newIn++] = local;
                        outIndices[newIn++] = (ushort)(local + 1);
                        outIndices[newIn++] = (ushort)(local + 2);
                        outIndices[newIn++] = local;
                        outIndices[newIn++] = (ushort)(local + 2);
                        outIndices[newIn++] = (ushort)(local + 3);
                    }

                    for (var k = 0; k < 6; k++)
                        outIndices[newIn++] = indices[j + k];
                    j += 6;
                }
                else
                {
                    var count = Math.Min(3, end - j);
                    for (var k = 0; k < count; k++)
                        outIndices[newIn++] = indices[j + k];
                    j += count;
                }
            }

            Groups[cmd.VtxOffset] = group;
            cmd.VtxOffset = (uint)group.Start;
            cmd.ElemCount = (uint)(newIn - cmd.IdxOffset);
        }

        vb.Resize(newVn);
        ib.Resize(newIn);
        new Span<ImDrawVert>(outVertices, 0, newVn).CopyTo(new Span<ImDrawVert>(vb.Data, newVn));
        new Span<ushort>(outIndices, 0, newIn).CopyTo(new Span<ushort>(ib.Data, newIn));
        new Span<ImDrawCmd>(commands, 0, cn).CopyTo(new Span<ImDrawCmd>(cb.Data, cn));

        // Anything drawn after (nothing should be) goes on at the end of the new buffers.
        var last = Groups[Offsets[^1]];
        ref var header = ref dl.CmdHeader;
        header.VtxOffset = (uint)last.Start;
        dl.VtxCurrentIdx = (uint)(newVn - last.Start);
        dl.VtxWritePtr = new ImDrawVertPtr(vb.Data + newVn);
        dl.IdxWritePtr = ib.Data + newIn;
    }

    private static nint Tex(in ImDrawCmd cmd) => (nint)cmd.TextureId.Handle;

    // Six indices drawing a letter: a quad of four vertices in a row, straight, textured, one of the theme's greys.
    private static bool Glyph(int j, int offset, int vn)
    {
        var a = indices[j];
        if (indices[j + 1] != a + 1 || indices[j + 2] != a + 2 || indices[j + 3] != a || indices[j + 4] != a + 2 || indices[j + 5] != a + 3)
            return false;
        var first = offset + a;
        if (first + 3 >= vn)
            return false;
        ref var v0 = ref vertices[first];
        ref var v1 = ref vertices[first + 1];
        ref var v2 = ref vertices[first + 2];
        ref var v3 = ref vertices[first + 3];
        if (v0.Pos.Y != v1.Pos.Y || v1.Pos.X != v2.Pos.X || v2.Pos.Y != v3.Pos.Y || v0.Pos.X != v3.Pos.X)
            return false;
        if (v0.Uv.X == v2.Uv.X || v0.Uv.Y == v2.Uv.Y || IconUvs.Contains(UvKey(v0.Uv.X, v0.Uv.Y)))
            return false;
        var col = v0.Col;
        if (v1.Col != col || v2.Col != col || v3.Col != col || col >> 24 < 24)
            return false;
        return Near(col, muted) || Near(col, faint);
    }

    private static long UvKey(float u, float v) => ((long)BitConverter.SingleToInt32Bits(u) << 32) | (uint)BitConverter.SingleToInt32Bits(v);

    // The UVs of the icon font's glyphs, read again when the font changes.
    private static void ReadIcons()
    {
        var font = UiBuilder.IconFont;
        if (font.Handle == null)
            return;
        ref var glyphs = ref font.Glyphs;
        var key = ((nint)font.Handle, glyphs.Size);
        if (key == iconsFor)
            return;
        iconsFor = key;
        IconUvs.Clear();
        for (var i = 0; i < glyphs.Size; i++)
            IconUvs.Add(UvKey(glyphs.Data[i].U0, glyphs.Data[i].V0));
    }

    // The same color (its alpha aside), give or take a little rounding.
    private static bool Near(uint col, uint grey)
    {
        for (var shift = 0; shift < 24; shift += 8)
        {
            if (Math.Abs((int)((col >> shift) & 0xFF) - (int)((grey >> shift) & 0xFF)) > 3)
                return false;
        }

        return true;
    }

    private static void Grow<T>(ref T[] array, int size)
    {
        if (array.Length < size)
            array = new T[Math.Max(size, array.Length * 2)];
    }
}
