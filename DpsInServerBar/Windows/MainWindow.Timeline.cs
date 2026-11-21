using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Native;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;
using DpsInServerBar.Ultimates;

namespace DpsInServerBar.Windows;

/// <summary>The timeline and the fight log of a pull.</summary>
public sealed partial class MainWindow
{
    /// <summary>The enemy casts and party debuffs of a pull, by time, in a scrolling table.</summary>
    private void DrawFightLog(PullRecord pull)
    {
        ImGui.Dummy(new Vector2(0, Theme.Px(4)));
        if (Theme.Chip(Loc.F("Boss casts ({0})", pull.BossCasts.Count), fightLogCasts))
            fightLogCasts = !fightLogCasts;
        ImGui.SameLine(0, Theme.Px(4));
        if (Theme.Chip(Loc.F("Debuffs ({0})", pull.Debuffs.Count), fightLogDebuffs))
            fightLogDebuffs = !fightLogDebuffs;
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.SetNextItemWidth(Theme.Px(220));
        ImGui.InputTextWithHint("##fightlogsearch", Loc.T("Search an action, a debuff or a player"), ref fightLogSearch, 64);

        bool Match(params string[] texts) =>
            fightLogSearch.Length == 0 || texts.Any(t => t.Contains(fightLogSearch, StringComparison.OrdinalIgnoreCase));

        // One row per cast or debuff: time, kind, name, on whom, from whom, end, length.
        var rows = new List<(double At, bool Cast, string Name, string On, string Job, string From, double? End, double? Length)>();
        if (fightLogCasts)
        {
            rows.AddRange(pull.BossCasts.Where(c => Match(GameNames.Action(c.ActionId, c.Action), c.Caster))
                              .Select(c => (c.AtSeconds, true, CastName(c), string.Empty, string.Empty, c.Caster,
                                            c.CastSeconds > 0 ? c.AtSeconds + c.CastSeconds : (double?)null,
                                            c.CastSeconds > 0 ? c.CastSeconds : (double?)null)));
        }

        if (fightLogDebuffs)
        {
            rows.AddRange(pull.Debuffs.Where(d => Match(GameNames.Status(d.StatusId, d.Status), d.Player, Names.Show(d.Player), d.Source))
                              .Select(d => (d.AppliedSeconds, false, GameNames.Status(d.StatusId, d.Status), Names.Show(d.Player), d.Job, d.Source, d.EndSeconds,
                                            d.EndSeconds is { } end ? end - d.AppliedSeconds : (double?)null)));
        }

        if (rows.Count == 0)
        {
            Theme.Hint(pull.BossCasts.Count == 0 && pull.Debuffs.Count == 0
                           ? Loc.T("Casts and debuffs were not recorded for this pull.")
                           : Loc.T("Nothing matches."));
            return;
        }

        rows.Sort((a, b) => a.At.CompareTo(b.At));
        var height = Math.Min(Theme.Px(420), (rows.Count + 1) * ImGui.GetTextLineHeightWithSpacing() + Theme.Px(12));
        using var table = ImRaii.Table("##fightlog", 7,
                                       ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp
                                       | ImGuiTableFlags.BordersOuterH, new Vector2(0, height));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(50));
        ImGui.TableSetupColumn(Loc.T("Type"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
        ImGui.TableSetupColumn(Loc.T("Name"), ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn(Loc.T("On"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Loc.T("From"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Loc.T("Ends"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(80));
        ImGui.TableSetupColumn(Loc.T("Length"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            Theme.RowHover();
            TimeCell(row.At);

            ImGui.TableNextColumn();
            ImGui.TextColored(row.Cast ? Theme.Warn : Theme.Bad, row.Cast ? Loc.T("Cast") : Loc.T("Debuff"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);

            ImGui.TableNextColumn();
            if (row.On.Length > 0)
            {
                ImGui.TextUnformatted(row.On);
                if (row.Job.Length > 0)
                {
                    ImGui.SameLine(0, Theme.Px(6));
                    ImGui.TextColored(Theme.TextFaint, row.Job);
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextMuted, row.From);

            ImGui.TableNextColumn();
            if (row.End is { } end)
                ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(end)));
            else if (!row.Cast)
                ImGui.TextColored(Theme.TextFaint, Loc.T("end of pull"));

            ImGui.TableNextColumn();
            if (row.Length is { } length)
                ImGui.TextColored(Theme.TextFaint, Loc.F("{0:0.0}s", length));
        }
    }

    /// <summary>
    /// The pull as lanes, chosen with the boxes above it: deaths (red), tank swaps (blue), failed mechanics (orange) and
    /// mitigation (one lane per player, by the icon of each action). Minute ticks; hover a mark for its detail.
    /// </summary>
    // A cast by its name; an attack with no cast bar marked as instant.
    private static string CastName(BossCast cast) =>
        cast.CastSeconds > 0 ? GameNames.Action(cast.ActionId, cast.Action) : Loc.F("{0} (instant)", GameNames.Action(cast.ActionId, cast.Action));

    // The timeline's zoom (1 = the whole pull in the width) and the time at its left edge. Static, like the rotation's.
    private static float timelineZoom = 1f;
    private static double timelineStart;
    private const float MaxTimelineZoom = 16f;

    private void DrawTimeline(PullRecord pull)
    {
        var config = plugin.Configuration;
        if (!Theme.Section(Loc.T("Timeline"), FontAwesomeIcon.Stream, "pull.timeline"))
            return;

        var changed = false;
        bool Toggle(string label, bool value, Vector4 color, bool first = false)
        {
            if (!first)
                ImGui.SameLine(0, Theme.Px(14));
            using (ImRaii.PushColor(ImGuiCol.Text, color))
            {
                if (ImGui.Checkbox(label, ref value))
                    changed = true;
            }

            return value;
        }

        config.TimelineBossAttacks = Toggle(Loc.F("Boss attacks ({0})", pull.BossCasts.Count), config.TimelineBossAttacks, BossColor, first: true);
        config.TimelineDeaths = Toggle(Loc.F("Deaths ({0})",pull.PartyDeaths.Count), config.TimelineDeaths, Theme.Bad);
        config.TimelineTankSwaps = Toggle(Loc.F("Tank swaps ({0})",pull.TankSwaps.Count), config.TimelineTankSwaps, Theme.Tank);
        config.TimelineMistakes = Toggle(Loc.F("Failed mechanics ({0})",pull.Mistakes.Count), config.TimelineMistakes, Theme.Warn);
        config.TimelineMitigation = Toggle(Loc.F("Mitigation ({0})",pull.Mitigations.Count), config.TimelineMitigation, Theme.Crystal);
        if (pull.PlanResults is { } planChecks)
        {
            config.TimelinePlanMisses = Toggle(Loc.F("Plan misses ({0})", planChecks.Count(c => !c.Used)), config.TimelinePlanMisses, Theme.Bad);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.F("The skills of your column of the mitigation plan ({0}) not used in time, at the time their cast landed.", pull.PlanName));
        }

        // On the right: a mitigation plan from the casts, and the table of the enemy casts and party debuffs.
        var planLabel = Loc.T("New mitigation plan");
        var logLabel = Loc.T("Casts & debuffs");
        var right = ImGui.GetWindowContentRegionMax().X;
        if (pull.BossCasts.Count > 0)
        {
            var planX = right - ImGui.CalcTextSize(planLabel).X - Theme.Px(20)
                        - (ImGui.CalcTextSize(logLabel).X + Theme.Px(20) + ImGui.GetStyle().ItemSpacing.X);
            ImGui.SameLine(planX);
            if (Theme.Chip(planLabel, false))
                plugin.NewMitigationPlan(pull);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("A mitigation plan for this fight, made from the casts of this pull: pick the casts, then write which role uses which mitigation."));
        }

        if (pull.BossCasts.Count > 0 || pull.Debuffs.Count > 0)
        {
            var label = logLabel;
            ImGui.SameLine(right - ImGui.CalcTextSize(label).X - Theme.Px(20));
            if (Theme.Chip(label, showFightLog))
                showFightLog = !showFightLog;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("The casts of the enemies and the debuffs of the party members, with when they were applied and ran out, in a table under the timeline."));
        }
        if (changed)
            config.Save();

        // The lanes, top to bottom.
        var dotLane = Theme.Px(18);
        var iconSize = Theme.Px(20);
        var iconLane = iconSize + Theme.Px(2);
        var lanes = new List<TimelineLane>();

        // At the top, the enemies' attacks when they landed, each with the mitigations up then.
        if (config.TimelineBossAttacks && pull.BossCasts.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.Crosshairs, null, dotLane, BossColor, TimelineMark.Dot,
                                       pull.BossCasts.Select(c => (c.AtSeconds + c.CastSeconds, 0u, BossAttackTitle(c))).ToList())
            {
                DrawTip = k => DrawBossAttackTip(pull, pull.BossCasts[k]),
            });
        }

        if (config.TimelineDeaths && pull.PartyDeaths.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.Skull, null, dotLane, Theme.Bad, TimelineMark.Dot,
                                       pull.PartyDeaths.Select(d => (d.AtSeconds, 0u, DeathText(d))).ToList()));
        }

        if (config.TimelineTankSwaps && pull.TankSwaps.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.ExchangeAlt, null, dotLane, Theme.Tank, TimelineMark.Diamond,
                                       pull.TankSwaps.Select(s => (s.AtSeconds, 0u, Loc.F("Tank swap {0} → {1} ({2})",Names.Show(s.From),Names.Show(s.To),s.Reason))).ToList()));
        }

        if (config.TimelineMistakes && pull.Mistakes.Count > 0)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.ExclamationTriangle, null, dotLane, Theme.Warn, TimelineMark.Dot,
                                       pull.Mistakes.Select(m => (m.AtSeconds, 0u, $"{Names.Show(m.Player)}: {MistakeText(m)}")).ToList()));
        }

        if (config.TimelinePlanMisses && pull.PlanResults?.Where(c => !c.Used).ToList() is { Count: > 0 } misses)
        {
            lanes.Add(new TimelineLane(FontAwesomeIcon.ClipboardList, null, iconLane, Theme.Bad, TimelineMark.Icon,
                                       misses.Select(m => (Math.Max(0, m.AtSeconds), ActionIcons.OfAction(m.ActionId),
                                                           Loc.F("Plan: {0} not used for {1}", GameNames.Action(m.ActionId, m.Action), m.Cast))).ToList()));
        }

        if (config.TimelineMitigation)
        {
            static int RoleOrder(string job) => JobRoles.Of(job) switch { JobRole.Tank => 0, JobRole.Healer => 1, _ => 2 };
            foreach (var player in pull.Mitigations.GroupBy(u => u.Player)
                                       .OrderBy(g => RoleOrder(g.First().Job)).ThenByDescending(g => g.First().IsYou).ThenBy(g => g.Key))
            {
                var p = player.First();
                lanes.Add(new TimelineLane(null, p.Job, iconLane, Theme.Crystal, TimelineMark.Icon,
                                           player.Select(u => (u.AtSeconds, ActionIcons.OfAction(u.ActionId), $"{Names.Show(u.Player)}: {GameNames.Action(u.ActionId, u.Action)}")).ToList()));
            }
        }

        if (lanes.Count == 0)
        {
            Theme.Hint(Loc.T("Nothing to show: tick what the timeline should show."));
            return;
        }

        var duration = Math.Max(1, Math.Max(pull.DurationSeconds, lanes.SelectMany(l => l.Marks).Select(m => m.At).DefaultIfEmpty(0).Max()));
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var gutter = Theme.Px(26);
        var pad = iconSize / 2 + Theme.Px(2);

        // The part of the pull shown: all of it, or a slice of it when zoomed in.
        timelineZoom = Math.Clamp(timelineZoom, 1f, MaxTimelineZoom);
        var span = duration / timelineZoom;
        timelineStart = Math.Clamp(timelineStart, 0, duration - span);
        var inner = width - gutter - pad * 2;
        float X(double seconds) => origin.X + gutter + pad + (float)((seconds - timelineStart) / span) * inner;
        double TimeAt(float x) => timelineStart + (x - origin.X - gutter - pad) / inner * span;
        var left = origin.X + gutter;
        var rightEdge = origin.X + width;

        // Icons that would overlap in a player's lane go one row lower: the lane grows.
        var rows = lanes.Select(l => l.Mark == TimelineMark.Icon ? PackIcons(l.Marks, X, iconSize) : null).ToList();
        var heights = lanes.Select((l, i) => rows[i] is { } r ? Math.Max(1, r.Max(x => x.Row) + 1) * l.Height : l.Height).ToList();
        var height = heights.Sum() + Theme.Px(4);

        ImGui.InvisibleButton("##timeline", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();

        // Ctrl + wheel zooms around the time under the mouse; the wheel or a drag moves along the pull once zoomed.
        var io = ImGui.GetIO();
        if (hovered && (io.KeyCtrl || timelineZoom > 1f))
            ImGuiP.SetItemUsingMouseWheel(); // the wheel is the timeline's, not the window's scroll
        if (hovered && io.MouseWheel != 0 && (io.KeyCtrl || timelineZoom > 1f))
        {
            if (io.KeyCtrl)
            {
                var mouseTime = TimeAt(mouse.X);
                timelineZoom = Math.Clamp(timelineZoom * MathF.Pow(1.25f, io.MouseWheel), 1f, MaxTimelineZoom);
                span = duration / timelineZoom;
                timelineStart = mouseTime - (mouse.X - origin.X - gutter - pad) / inner * span;
            }
            else
            {
                timelineStart -= io.MouseWheel * span * 0.15;
            }

            timelineStart = Math.Clamp(timelineStart, 0, duration - span);
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f) && timelineZoom > 1f)
        {
            timelineStart = Math.Clamp(timelineStart - io.MouseDelta.X / inner * span, 0, duration - span);
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        }

        dl.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.Panel, 0.8f), Theme.Px(4));
        dl.PushClipRect(new Vector2(left, origin.Y), new Vector2(rightEdge, origin.Y + height), true);
        // A line every minute; zoomed in enough, a fainter one every 10 s.
        var step = span <= 90 ? 10 : 60;
        for (var t = step; t < duration; t += step)
        {
            if (X(t) < left || X(t) > rightEdge)
                continue;
            var minute = t % 60 == 0;
            dl.AddLine(new Vector2(X(t), origin.Y + Theme.Px(2)), new Vector2(X(t), origin.Y + height - Theme.Px(2)), Theme.U32(Theme.Line, minute ? 0.6f : 0.3f), 1f);
        }

        dl.PopClipRect();

        var tooltip = new List<System.Action>();
        var y = origin.Y + Theme.Px(2);
        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var top = y;
            var bottom = y + heights[i];
            if (i > 0)
                dl.AddLine(new Vector2(origin.X + Theme.Px(4), top), new Vector2(origin.X + width - Theme.Px(4), top), Theme.U32(Theme.Line, 0.35f), 1f);

            // What the lane is: a glyph, or the player's job.
            var labelSize = Math.Min(lane.Height - Theme.Px(4), Theme.Px(16));
            var labelPos = new Vector2(origin.X + Theme.Px(5), top + (lane.Height - labelSize) / 2);
            if (lane.Job is { } job)
            {
                ImGui.SetCursorScreenPos(labelPos);
                JobInfo.DrawIcon(job, labelSize);
            }
            else if (lane.Glyph is { } glyph)
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    dl.AddText(labelPos, Theme.U32(lane.Color, 0.8f), glyph.ToIconString());
            }

            var mid = top + lane.Height / 2;
            var laneHovered = hovered && mouse.Y >= top && mouse.Y < bottom && mouse.X >= left;
            dl.PushClipRect(new Vector2(left, top), new Vector2(rightEdge, bottom), true);
            if (lane.Mark != TimelineMark.Icon)
                dl.AddLine(new Vector2(Math.Max(left, X(0)), mid), new Vector2(Math.Min(rightEdge, X(duration)), mid), Theme.U32(Theme.Line, 0.5f), 1f);

            var radius = Theme.Px(4);
            for (var k = 0; k < lane.Marks.Count; k++)
            {
                var (at, icon, text) = lane.Marks[k];
                var x = X(at);
                if (x < left - iconSize || x > rightEdge + iconSize)
                    continue;
                switch (lane.Mark)
                {
                    case TimelineMark.Dot:
                        dl.AddCircleFilled(new Vector2(x, mid), radius, Theme.U32(lane.Color));
                        if (laneHovered && Math.Abs(mouse.X - x) <= Theme.Px(6))
                        {
                            if (lane.DrawTip is { } draw)
                            {
                                var index = k;
                                tooltip.Add(() => draw(index));
                            }
                            else
                            {
                                tooltip.Add(TipText(at, text));
                            }
                        }
                        break;

                    case TimelineMark.Diamond:
                        dl.AddQuadFilled(new Vector2(x, mid - radius), new Vector2(x + radius, mid), new Vector2(x, mid + radius),
                                         new Vector2(x - radius, mid), Theme.U32(lane.Color));
                        if (laneHovered && Math.Abs(mouse.X - x) <= Theme.Px(6))
                            tooltip.Add(TipText(at, text));
                        break;

                    default:
                        var pos = new Vector2(x - iconSize / 2, top + rows[i]![k].Row * lane.Height + Theme.Px(1));
                        if (icon != 0)
                            ActionIcons.DrawAt(dl, icon, pos, iconSize);
                        else
                            dl.AddRectFilled(pos, pos + new Vector2(iconSize, iconSize), Theme.U32(Theme.SurfaceActive), iconSize * 0.15f);
                        if (hovered && mouse.X >= Math.Max(left, pos.X) && mouse.X <= pos.X + iconSize && mouse.Y >= pos.Y && mouse.Y <= pos.Y + iconSize)
                            tooltip.Add(TipText(at, text));
                        break;
                }
            }

            dl.PopClipRect();
            y = bottom;
        }

        // A line following the mouse across the lanes, with its time: what happened at that moment, lane by lane.
        if (hovered && mouse.X >= Math.Max(left, X(0)) && mouse.X <= Math.Min(rightEdge, X(duration)))
        {
            dl.AddLine(new Vector2(mouse.X, origin.Y), new Vector2(mouse.X, origin.Y + height), Theme.U32(Theme.Text, 0.5f), 1f);
            var at = TimeAt(mouse.X);
            var label = NumberFormat.Duration(TimeSpan.FromSeconds(at));
            var size = ImGui.CalcTextSize(label);
            var labelPos = new Vector2(Math.Min(mouse.X + Theme.Px(4), origin.X + width - size.X - Theme.Px(2)), origin.Y + height - size.Y - Theme.Px(1));
            dl.AddRectFilled(labelPos - Theme.Px(new Vector2(2, 0)), labelPos + size + Theme.Px(new Vector2(2, 0)), Theme.U32(Theme.Panel, 0.9f), Theme.Px(3));
            dl.AddText(labelPos, Theme.U32(Theme.Text), label);
        }

        // Zoomed in: a bar under the lanes showing which part of the pull is in view.
        if (timelineZoom > 1f)
        {
            var barY = origin.Y + height + Theme.Px(2);
            var barLeft = left + pad;
            var barWidth = inner;
            dl.AddRectFilled(new Vector2(barLeft, barY), new Vector2(barLeft + barWidth, barY + Theme.Px(3)), Theme.U32(Theme.Line, 0.5f), Theme.Px(2));
            var from = barLeft + (float)(timelineStart / duration) * barWidth;
            dl.AddRectFilled(new Vector2(from, barY), new Vector2(from + barWidth / timelineZoom, barY + Theme.Px(3)), Theme.U32(Theme.Gold, 0.8f), Theme.Px(2));
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height + Theme.Px(5) + ImGui.GetStyle().ItemSpacing.Y));
        var rowX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(rowX + gutter);
        ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(timelineStart)));

        // Zoom, in the middle of the time row.
        var buttons = ImGui.GetFrameHeight() * 3 + Theme.Px(4);
        ImGui.SameLine(rowX + gutter + (width - gutter - buttons) / 2);
        if (Theme.IconButton("tlzoomout", FontAwesomeIcon.SearchMinus, Loc.T("Zoom out")))
            timelineZoom = Math.Max(1f, timelineZoom / 1.5f);
        ImGui.SameLine(0, Theme.Px(2));
        if (Theme.IconButton("tlzoomin", FontAwesomeIcon.SearchPlus, Loc.T("Zoom in (Ctrl + wheel). Zoomed in, the wheel or a drag moves along the pull.")))
        {
            var centre = timelineStart + span / 2;
            timelineZoom = Math.Min(MaxTimelineZoom, timelineZoom * 1.5f);
            timelineStart = centre - duration / timelineZoom / 2;
        }

        ImGui.SameLine(0, Theme.Px(2));
        if (Theme.IconButton("tlzoomreset", FontAwesomeIcon.Expand, Loc.T("Whole pull")))
            timelineZoom = 1f;
        var end = NumberFormat.Duration(TimeSpan.FromSeconds(Math.Min(duration, timelineStart + span)));
        ImGui.SameLine(rowX + width - ImGui.CalcTextSize(end).X);
        ImGui.TextColored(Theme.TextFaint, end);

        if (tooltip.Count > 0)
        {
            using var tip = ImRaii.Tooltip();
            foreach (var draw in tooltip)
                draw();
        }
    }

    private static System.Action TipText(double at, string text) =>
        () => ImGui.TextUnformatted($"{NumberFormat.Duration(TimeSpan.FromSeconds(at))}  {text}");

    private enum TimelineMark { Dot, Diamond, Icon }

    // The enemies' attacks: purple.
    private static readonly Vector4 BossColor = new(0.72f, 0.48f, 0.98f, 1f);

    private static readonly Dictionary<uint, string> EnglishActionNames = new();

    private static string EnglishName(uint actionId)
    {
        if (!EnglishActionNames.TryGetValue(actionId, out var name))
        {
            name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(Dalamud.Game.ClientLanguage.English)
                         .TryGetRow(actionId, out var row) ? row.Name.ExtractText() : string.Empty;
            EnglishActionNames[actionId] = name;
        }

        return name;
    }

    // An enemy attack: its name and who cast it.
    private static string BossAttackTitle(BossCast cast) =>
        cast.Caster.Length > 0 ? $"{CastName(cast)} ({cast.Caster})" : CastName(cast);

    // An enemy attack in a tooltip: its time and name, then the mitigations up when it landed, each with its icon.
    private static void DrawBossAttackTip(PullRecord pull, BossCast cast)
    {
        var landed = cast.AtSeconds + cast.CastSeconds;
        ImGui.TextUnformatted($"{NumberFormat.Duration(TimeSpan.FromSeconds(landed))}  {BossAttackTitle(cast)}");
        var up = MitigationDurations.ActiveAt(pull.Mitigations, landed, u => EnglishName(u.ActionId));
        if (up.Count == 0)
        {
            if (pull.Mitigations.Count > 0)
                ImGui.TextColored(Theme.TextMuted, Loc.T("No mitigation up when it landed."));
            return;
        }

        ImGui.TextColored(Theme.TextMuted, Loc.T("Mitigations up when it landed:"));
        foreach (var u in up)
            DrawMitigationLine(u);
    }

    // A mitigation in a tooltip: its icon, its name, who used it (and on whom, when on another party member).
    private static void DrawMitigationLine(MitigationUse u)
    {
        var size = ImGui.GetTextLineHeight();
        ImGui.Dummy(new Vector2(Theme.Px(6), size));
        ImGui.SameLine(0, 0);
        ActionIcons.Draw(ActionIcons.OfAction(u.ActionId), size);
        ImGui.SameLine(0, Theme.Px(5));
        ImGui.TextUnformatted(GameNames.Action(u.ActionId, u.Action));
        ImGui.SameLine(0, Theme.Px(5));
        var who = Names.Show(u.Player);
        if (u.Target.Length > 0 && u.Target != u.Player)
            who += $" → {Names.Show(u.Target)}";
        ImGui.TextColored(Theme.TextMuted, who);
    }

    /// <summary>A row of the timeline: a glyph or a job at its left, then its marks (time, icon, tooltip).</summary>
    private sealed record TimelineLane(
        FontAwesomeIcon? Glyph, string? Job, float Height, Vector4 Color, TimelineMark Mark, List<(double At, uint Icon, string Text)> Marks)
    {
        /// <summary>Draws the tooltip of a mark (by its index) instead of its text: icons and all.</summary>
        public System.Action<int>? DrawTip { get; init; }
    }

    // Places icons along a lane, one row lower when they would overlap the one before; the row of each, in order.
    private static List<(int Row, float Left)> PackIcons(List<(double At, uint Icon, string Text)> marks, Func<double, float> x, float size)
    {
        var ends = new List<float>();
        var placed = new (int Row, float Left)[marks.Count];
        foreach (var (mark, index) in marks.Select((m, i) => (m, i)).OrderBy(p => p.m.At))
        {
            var left = x(mark.At) - size / 2;
            var row = ends.FindIndex(e => e <= left);
            if (row < 0)
            {
                ends.Add(0);
                row = ends.Count - 1;
            }

            ends[row] = left + size + 1;
            placed[index] = (row, left);
        }

        return placed.ToList();
    }

    // A death in one line: who, of what, and the detail.
    private static string DeathText(PartyDeath d) =>
        Loc.F("{0} died: {1}",Names.Show(d.Player),d.CauseText) + (DeathDetails(d) is { Length: > 0 } details ? $" {details}" : string.Empty);

    /// <summary>
    /// Failed mechanics: per player, the penalty debuffs taken (most first); in the history, every one in time order.
    /// </summary>
    // One failed mechanic: when, the debuff, and the action that put it.
    private static string MistakeText(MechanicMistake m) =>
        Loc.F("{0}  {1}  from {2}",NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds)),m.StatusText,m.AbilityText)
        + (m.Source.Length > 0 ? $" ({m.Source})" : string.Empty);
}
