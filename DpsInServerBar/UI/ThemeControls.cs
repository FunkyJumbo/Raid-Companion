using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace DpsInServerBar.UI;

/// <summary>
/// The plugin's form controls: ImGui's, with their animations (a check traced, a slider's trail, a field's underline,
/// a refused entry shaking, a button held to confirm) and tooltips unrolling.
/// </summary>
public static partial class Theme
{
    // ---- Tooltips: they unroll from the top, a gold edge running down with them.

    private static int tooltipFrame;
    private static float tooltipAt;
    private static string? tooltipText;

    /// <summary>A tooltip for the item hovered (in place of ImGui.SetTooltip).</summary>
    public static void Tooltip(string text) => Tooltip(text, 0);

    /// <summary>The same, its text wrapped at this width (0: not wrapped).</summary>
    public static void Tooltip(string text, float wrap)
    {
        var frame = ImGui.GetFrameCount();
        // A new tooltip (a second one in the same frame keeps the first one's timing).
        if (frame - tooltipFrame > 1 || (text != tooltipText && frame != tooltipFrame))
        {
            tooltipAt = Fx.Now;
            tooltipText = text;
        }

        tooltipFrame = frame;
        var k = Fx.Enabled ? Fx.EaseOut((Fx.Now - tooltipAt) / 0.22f) : 1f;
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (0.35f + 0.65f * k), k < 1);
        using var tooltip = ImRaii.Tooltip();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var edge = pos.Y + size.Y * k;
        if (k < 1)
            ImGui.PushClipRect(pos, new Vector2(pos.X + size.X, edge), true);
        if (wrap > 0)
            ImGui.PushTextWrapPos(wrap);
        ImGui.TextUnformatted(text);
        if (wrap > 0)
            ImGui.PopTextWrapPos();
        if (k >= 1)
            return;
        ImGui.PopClipRect();
        ImGui.GetWindowDrawList().AddLine(new Vector2(pos.X + Px(4), edge), new Vector2(pos.X + size.X - Px(4), edge), U32(GoldBright, 0.8f * (1 - k)), Px(1.5f));
    }

    // ---- Check boxes: the check traced in one stroke, a ring pulsing out of the box.

    private static readonly Dictionary<uint, float> Ticked = new();

    public static bool Checkbox(string label, ref bool value)
    {
        var id = ImGui.GetID(label);
        var mark = ImGui.GetColorU32(ImGuiCol.CheckMark);
        bool changed;
        using (ImRaii.PushColor(ImGuiCol.CheckMark, Vector4.Zero))
            changed = ImGui.Checkbox(label, ref value);
        if (changed)
        {
            if (value && Fx.Enabled)
                Ticked[id] = Fx.Now;
            else
                Ticked.Remove(id);
        }

        var min = ImGui.GetItemRectMin();
        var box = ImGui.GetFrameHeight();
        var age = Ticked.TryGetValue(id, out var at) ? Fx.Now - at : 100f;
        if (age >= 0.6f)
            Ticked.Remove(id);
        if (!value)
            return changed;

        var dl = ImGui.GetWindowDrawList();
        var pad = MathF.Max(1f, box / 6f);
        var thickness = MathF.Max(box / 5.5f, 1f);
        Vector2 a = min + new Vector2(pad + box * 0.02f, box * 0.52f),
                b = min + new Vector2(box * 0.42f, box - pad * 1.35f),
                c = min + new Vector2(box - pad, pad * 1.25f);
        var k = Fx.EaseOut(age / 0.25f);
        var first = Math.Min(1f, k * 2.2f);
        var mid = Vector2.Lerp(a, b, first);
        dl.AddLine(a, mid, mark, thickness);
        if (k * 2.2f > 1f)
        {
            var end = Vector2.Lerp(b, c, Math.Min(1f, (k * 2.2f - 1f) / 1.2f));
            dl.AddLine(b - Vector2.Normalize(b - a) * thickness * 0.3f, end, mark, thickness);
        }

        if (age < 0.6f)
        {
            var ring = age / 0.6f;
            var center = min + new Vector2(box / 2);
            dl.AddRect(center - new Vector2(box / 2 + Px(5) * ring), center + new Vector2(box / 2 + Px(5) * ring), U32(GoldBright, 0.6f * (1 - ring)),
                       Px(4), ImDrawFlags.None, Px(1.5f));
        }

        return changed;
    }

    // ---- Sliders: a short trail of light behind the grab while it is dragged.

    private static uint trailId;
    private static readonly List<(float X, float At)> Trail = [];

    public static bool SliderInt(string label, ref int value, int min, int max, string format = "%d")
    {
        var width = ImGui.CalcItemWidth();
        var changed = ImGui.SliderInt(label, ref value, min, max, format);
        SliderTrail(label, width, max > min ? (value - min) / (float)(max - min) : 0f, max - min);
        return changed;
    }

    public static bool SliderFloat(string label, ref float value, float min, float max, string format = "%.3f")
    {
        var width = ImGui.CalcItemWidth();
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        SliderTrail(label, width, max > min ? (value - min) / (max - min) : 0f, 0);
        return changed;
    }

    private static void SliderTrail(string label, float width, float fraction, int steps)
    {
        if (!Fx.Enabled)
            return;
        var id = ImGui.GetID(label);
        var style = ImGui.GetStyle();
        var min = ImGui.GetItemRectMin();
        var height = ImGui.GetFrameHeight();
        var inner = width - 2;
        var grab = steps > 0 ? Math.Max(style.GrabMinSize, inner / (steps + 1)) : style.GrabMinSize;
        var x = min.X + 1 + grab / 2 + Math.Clamp(fraction, 0, 1) * (inner - grab);
        if (ImGui.IsItemActive())
        {
            if (trailId != id)
            {
                trailId = id;
                Trail.Clear();
            }

            if (Trail.Count == 0 || Math.Abs(Trail[^1].X - x) > 0.5f)
                Trail.Add((x, Fx.Now));
        }

        if (trailId != id)
            return;
        Trail.RemoveAll(p => Fx.Now - p.At > 0.35f);
        var dl = ImGui.GetWindowDrawList();
        var y = min.Y + height / 2;
        foreach (var (px, at) in Trail)
            Fx.Glow(dl, new Vector2(px, y), Px(2.2f), GoldBright, 0.5f * (1 - (Fx.Now - at) / 0.35f));
        if (ImGui.IsItemActive())
            Fx.Glow(dl, new Vector2(x, y), Px(3), GoldBright, 0.6f);
    }

    // ---- Text fields: an underline spreading from the middle on focus; a refused entry shakes and turns red.

    private static readonly Dictionary<uint, (bool Active, float At)> Fields = new();
    private static readonly Dictionary<uint, float> Refused = new();

    /// <summary>Marks the field with this label (in the same window and id scope) as refused: it shakes, red.</summary>
    public static void Refuse(string label) => Refused[ImGui.GetID(label)] = Fx.Now;

    public static bool InputText(string label, ref string value, int maxLength, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var (id, width) = BeforeField(label);
        var changed = ImGui.InputText(label, ref value, maxLength, flags);
        AfterField(id, width);
        return changed;
    }

    public static bool InputTextWithHint(string label, string hint, ref string value, int maxLength, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var (id, width) = BeforeField(label);
        var changed = ImGui.InputTextWithHint(label, hint, ref value, maxLength, flags);
        AfterField(id, width);
        return changed;
    }

    private static (uint Id, float Width) BeforeField(string label)
    {
        var id = ImGui.GetID(label);
        var width = ImGui.CalcItemWidth();
        if (Refused.TryGetValue(id, out var at) && Fx.Enabled && Fx.Now - at < 0.4f)
        {
            var age = Fx.Now - at;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Sin(age * 60f) * Px(5) * (1 - age / 0.4f));
        }

        return (id, width);
    }

    private static void AfterField(uint id, float width)
    {
        var min = ImGui.GetItemRectMin();
        var max = new Vector2(min.X + width, min.Y + ImGui.GetFrameHeight());
        var dl = ImGui.GetWindowDrawList();
        if (Refused.TryGetValue(id, out var refused))
        {
            var age = Fx.Now - refused;
            if (age >= 0.9f)
                Refused.Remove(id);
            else
            {
                var k = 1 - age / 0.9f;
                dl.AddRectFilled(min, max, U32(Bad, 0.14f * k), Px(Radius));
                dl.AddRect(min, max, U32(Bad, 0.9f * k), Px(Radius), ImDrawFlags.None, Px(1.5f));
            }
        }

        var active = ImGui.IsItemActive();
        var state = Fields.TryGetValue(id, out var s) ? s : (Active: false, At: -100f);
        if (active != state.Active)
            state = (active, Fx.Enabled ? Fx.Now : -100f);
        Fields[id] = state;
        var t = Fx.EaseOut((Fx.Now - state.At) / (active ? 0.25f : 0.15f));
        var reach = active ? t : 1 - t;
        if (reach <= 0)
            return;
        var middle = (min.X + max.X) / 2;
        var half = (max.X - min.X) / 2 * reach;
        dl.AddLine(new Vector2(middle - half, max.Y - Px(1)), new Vector2(middle + half, max.Y - Px(1)), U32(GoldBright, 0.9f), Px(2));
    }

    // ---- Scrollbars: lit while the mouse wheel scrolls, fading after.

    private static float scrolledAt = -100f;

    private static Vector4 ScrollGrab(Vector4 rest)
    {
        if (ImGui.GetIO().MouseWheel != 0)
            scrolledAt = Fx.Now;
        if (!Fx.Enabled)
            return rest;
        var k = 1 - Fx.Clamp01((Fx.Now - scrolledAt - 0.4f) / 0.6f);
        return k <= 0 ? rest : Vector4.Lerp(rest, Gold with { W = 0.75f }, k);
    }
}
