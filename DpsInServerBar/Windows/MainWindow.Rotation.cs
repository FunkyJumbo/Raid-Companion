using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DpsInServerBar.Display;
using DpsInServerBar.Opener;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>History, Personal: your rotation in the pull, as a timeline of your GCDs and oGCDs (scrolled sideways).</summary>
public sealed partial class MainWindow
{
    // Pixels per second of the rotation timeline (before the UI scale): zoom with the buttons or Ctrl + wheel. Static, like
    // the Personal section drawing it (there is one main window).
    private static float rotationZoom = 14f;
    private const float MinRotationZoom = 4f;
    private const float MaxRotationZoom = 48f;

    // The lanes of a pull's rotation, rebuilt for another pull or another language.
    private static (Guid Pull, string Language) rotationKey;
    private static List<(double At, uint Icon, string Text, RotationUse Use)> rotationGcds = [];
    private static List<(double At, uint Icon, string Text, RotationUse Use)> rotationOgcds = [];

    // What an action landed on, for its tooltip (empty when not recorded).
    private static string TargetText(RotationUse use)
    {
        var name = use.TargetKind switch
        {
            TargetKind.Self => Loc.T("you"),
            TargetKind.Player => Names.Show(use.Target),
            TargetKind.Other => use.Target,
            _ => string.Empty,
        };
        if (name.Length == 0)
            return use.Targets > 1 ? Loc.F("On {0} targets", use.Targets) : string.Empty;
        return use.Targets > 1 ? Loc.F("On {0} and {1} more", name, use.Targets - 1) : Loc.F("On {0}", name);
    }

    private static void FoldRotation(PullRecord pull)
    {
        var open = Fold(Loc.T("Rotation"), "protation");
        ImGui.TextColored(Theme.TextMuted, Loc.F("{0} actions", pull.Rotation.Count(u => u.AtSeconds >= 0 && !u.Item)));
        var potions = pull.Rotation.Count(u => u.Item);
        if (potions > 0)
        {
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(Theme.Crystal, Loc.F("{0} item(s)", potions));
        }
        if (!open)
            return;

        DrawRotation(pull);
        ImGui.TreePop();
    }

    private static void DrawRotation(PullRecord pull)
    {
        if (rotationKey != (pull.Id, Loc.Code))
        {
            rotationKey = (pull.Id, Loc.Code);
            rotationGcds = [];
            rotationOgcds = [];
            foreach (var use in pull.Rotation)
            {
                // An item (a potion) goes with the oGCDs, where it is used.
                if (use.Item)
                {
                    var (icon, name) = ActionIcons.OfItem(use.ActionId);
                    rotationOgcds.Add((use.AtSeconds, icon, name, use));
                    continue;
                }

                var mark = (use.AtSeconds, ActionIcons.OfAction(use.ActionId), GameNames.Action(use.ActionId, $"#{use.ActionId}"), use);
                (CustomOpeners.IsGcd(use.ActionId) ? rotationGcds : rotationOgcds).Add(mark);
            }
        }

        // Zoom.
        if (Theme.IconButton("rotzoomout", FontAwesomeIcon.SearchMinus, Loc.T("Zoom out")))
            rotationZoom = Math.Max(MinRotationZoom, rotationZoom / 1.25f);
        ImGui.SameLine(0, Theme.Px(2));
        if (Theme.IconButton("rotzoomin", FontAwesomeIcon.SearchPlus, Loc.T("Zoom in")))
            rotationZoom = Math.Min(MaxRotationZoom, rotationZoom * 1.25f);
        ImGui.SameLine();
        Theme.Hint(Loc.T("Wheel: move along the pull. Ctrl + wheel: zoom. Hover an icon for its action, time and target."));

        var iconSize = Theme.Px(24);
        var laneHeight = iconSize + Theme.Px(3);
        var start = Math.Min(0, pull.Rotation.Select(u => u.AtSeconds).DefaultIfEmpty(0).Min()) - 1;
        var end = Math.Max(pull.DurationSeconds, pull.Rotation.Select(u => u.AtSeconds).DefaultIfEmpty(0).Max()) + 1;
        var pps = Theme.Px(rotationZoom);
        var pad = iconSize / 2 + Theme.Px(2);
        var contentWidth = (float)(end - start) * pps + pad * 2;

        // Lane names on the left, outside the part that scrolls.
        var labelWidth = Theme.Px(44);
        var gcdRows = PackIcons(rotationGcds.Select(m => (m.At, m.Icon, m.Text)).ToList(), t => (float)(t - start) * pps + pad, iconSize);
        var ogcdRows = PackIcons(rotationOgcds.Select(m => (m.At, m.Icon, m.Text)).ToList(), t => (float)(t - start) * pps + pad, iconSize);
        var gcdHeight = Math.Max(1, gcdRows.Select(r => r.Row + 1).DefaultIfEmpty(1).Max()) * laneHeight;
        var ogcdHeight = Math.Max(1, ogcdRows.Select(r => r.Row + 1).DefaultIfEmpty(1).Max()) * laneHeight;
        var ticksHeight = ImGui.GetTextLineHeight() + Theme.Px(2);
        var height = gcdHeight + ogcdHeight + ticksHeight + Theme.Px(6);
        var scrollbar = ImGui.GetStyle().ScrollbarSize;

        var labels = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(labels + new Vector2(0, (gcdHeight - ImGui.GetTextLineHeight()) / 2 + Theme.Px(2)), Theme.U32(Theme.Gold), Loc.T("GCD"));
        dl.AddText(labels + new Vector2(0, gcdHeight + (ogcdHeight - ImGui.GetTextLineHeight()) / 2 + Theme.Px(2)), Theme.U32(Theme.Crystal), Loc.T("oGCD"));
        ImGui.Dummy(new Vector2(labelWidth, height + scrollbar));
        ImGui.SameLine(0, 0);

        Ambience.OccludeNext(new Vector2(-1, height + scrollbar));
        using var child = ImRaii.Child("##rotation", new Vector2(-1, height + scrollbar), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child)
            return;

        var io = ImGui.GetIO();
        if (ImGui.IsWindowHovered() && io.MouseWheel != 0)
        {
            if (io.KeyCtrl)
            {
                // Zoom around the time under the mouse.
                var mouseTime = start + (ImGui.GetMousePos().X - ImGui.GetWindowPos().X + ImGui.GetScrollX() - pad) / pps;
                rotationZoom = Math.Clamp(rotationZoom * MathF.Pow(1.2f, io.MouseWheel), MinRotationZoom, MaxRotationZoom);
                var newPps = Theme.Px(rotationZoom);
                ImGui.SetScrollX((float)(mouseTime - start) * newPps + pad - (ImGui.GetMousePos().X - ImGui.GetWindowPos().X));
            }
            else
            {
                ImGui.SetScrollX(ImGui.GetScrollX() - io.MouseWheel * Theme.Px(120));
            }
        }

        var origin = ImGui.GetCursorScreenPos();
        float X(double t) => origin.X + pad + (float)(t - start) * pps;
        ImGui.InvisibleButton("##rotationarea", new Vector2(contentWidth, height));
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();
        var cdl = ImGui.GetWindowDrawList();

        var top = origin.Y;
        var bottom = origin.Y + gcdHeight + ogcdHeight + Theme.Px(4);
        cdl.AddRectFilled(origin, new Vector2(origin.X + contentWidth, bottom), Theme.U32(Theme.Panel, 0.8f), Theme.Px(4));

        // Before the pull: shaded; the pull start: a line.
        if (start < 0)
        {
            cdl.AddRectFilled(new Vector2(X(start), top), new Vector2(X(0), bottom), Theme.U32(Theme.Crystal, 0.06f));
            cdl.AddLine(new Vector2(X(0), top), new Vector2(X(0), bottom), Theme.U32(Theme.Gold, 0.8f), Theme.Px(1.5f));
        }

        // A tick every 5 s (a label every 10 s, or 30 s zoomed out), a line every minute.
        var labelEvery = pps >= Theme.Px(10) ? 10 : 30;
        for (var t = (int)Math.Ceiling(start / 5) * 5; t <= end; t += 5)
        {
            var x = X(t);
            var minute = t % 60 == 0;
            cdl.AddLine(new Vector2(x, top), new Vector2(x, bottom), Theme.U32(Theme.Line, minute ? 0.7f : 0.25f), 1f);
            if (t % labelEvery == 0)
            {
                var text = t < 0 ? $"-{NumberFormat.Duration(TimeSpan.FromSeconds(-t))}" : NumberFormat.Duration(TimeSpan.FromSeconds(t));
                cdl.AddText(new Vector2(x - ImGui.CalcTextSize(text).X / 2, bottom + Theme.Px(1)), Theme.U32(Theme.TextFaint), text);
            }
        }

        cdl.AddLine(new Vector2(origin.X, top + gcdHeight + Theme.Px(2)), new Vector2(origin.X + contentWidth, top + gcdHeight + Theme.Px(2)),
                    Theme.U32(Theme.Line, 0.35f), 1f);

        string? tooltip = null;
        void DrawLane(List<(double At, uint Icon, string Text, RotationUse Use)> marks, List<(int Row, float Left)> rows, float laneTop)
        {
            for (var k = 0; k < marks.Count; k++)
            {
                var (at, icon, text, use) = marks[k];
                var pos = new Vector2(origin.X + rows[k].Left, laneTop + rows[k].Row * laneHeight + Theme.Px(2));
                if (icon != 0)
                    ActionIcons.DrawAt(cdl, icon, pos, iconSize);
                else
                    cdl.AddRectFilled(pos, pos + new Vector2(iconSize, iconSize), Theme.U32(Theme.SurfaceActive), iconSize * 0.15f);
                if (hovered && mouse.X >= pos.X && mouse.X <= pos.X + iconSize && mouse.Y >= pos.Y && mouse.Y <= pos.Y + iconSize)
                {
                    var time = at < 0 ? $"-{NumberFormat.Duration(TimeSpan.FromSeconds(-at))}" : NumberFormat.Duration(TimeSpan.FromSeconds(at));
                    var on = TargetText(use);
                    tooltip = on.Length > 0 ? $"{time}  {text}" + "\n" + on : $"{time}  {text}";
                }
            }
        }

        DrawLane(rotationGcds, gcdRows, top);
        DrawLane(rotationOgcds, ogcdRows, top + gcdHeight + Theme.Px(2));

        if (tooltip is not null)
            Theme.Tooltip(tooltip);
    }
}
