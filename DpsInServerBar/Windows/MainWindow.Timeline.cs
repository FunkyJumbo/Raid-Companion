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
                              .Select(c => (c.AtSeconds, true, GameNames.Action(c.ActionId, c.Action), string.Empty, string.Empty, c.Caster,
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
    private void DrawTimeline(PullRecord pull)
    {
        var config = plugin.Configuration;
        Theme.SectionHeader(Loc.T("Timeline"), FontAwesomeIcon.Stream);

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

        config.TimelineDeaths = Toggle(Loc.F("Deaths ({0})",pull.PartyDeaths.Count), config.TimelineDeaths, Theme.Bad, first: true);
        config.TimelineTankSwaps = Toggle(Loc.F("Tank swaps ({0})",pull.TankSwaps.Count), config.TimelineTankSwaps, Theme.Tank);
        config.TimelineMistakes = Toggle(Loc.F("Failed mechanics ({0})",pull.Mistakes.Count), config.TimelineMistakes, Theme.Warn);
        config.TimelineMitigation = Toggle(Loc.F("Mitigation ({0})",pull.Mitigations.Count), config.TimelineMitigation, Theme.Crystal);

        // On the right: the table of the enemy casts and party debuffs, below the timeline.
        if (pull.BossCasts.Count > 0 || pull.Debuffs.Count > 0)
        {
            var label = Loc.T("Casts & debuffs");
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(label).X - Theme.Px(20));
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
        float X(double seconds) => origin.X + gutter + pad + (float)(seconds / duration) * (width - gutter - pad * 2);

        // Icons that would overlap in a player's lane go one row lower: the lane grows.
        var rows = lanes.Select(l => l.Mark == TimelineMark.Icon ? PackIcons(l.Marks, X, iconSize) : null).ToList();
        var heights = lanes.Select((l, i) => rows[i] is { } r ? Math.Max(1, r.Max(x => x.Row) + 1) * l.Height : l.Height).ToList();
        var height = heights.Sum() + Theme.Px(4);

        ImGui.InvisibleButton("##timeline", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();

        dl.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.Panel, 0.8f), Theme.Px(4));
        for (var minute = 60; minute < duration; minute += 60)
            dl.AddLine(new Vector2(X(minute), origin.Y + Theme.Px(2)), new Vector2(X(minute), origin.Y + height - Theme.Px(2)), Theme.U32(Theme.Line, 0.6f), 1f);

        var tooltip = new List<string>();
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
            var laneHovered = hovered && mouse.Y >= top && mouse.Y < bottom;
            if (lane.Mark != TimelineMark.Icon)
                dl.AddLine(new Vector2(X(0), mid), new Vector2(X(duration), mid), Theme.U32(Theme.Line, 0.5f), 1f);

            var radius = Theme.Px(4);
            for (var k = 0; k < lane.Marks.Count; k++)
            {
                var (at, icon, text) = lane.Marks[k];
                var x = X(at);
                switch (lane.Mark)
                {
                    case TimelineMark.Dot:
                        dl.AddCircleFilled(new Vector2(x, mid), radius, Theme.U32(lane.Color));
                        if (laneHovered && Math.Abs(mouse.X - x) <= Theme.Px(6))
                            tooltip.Add($"{NumberFormat.Duration(TimeSpan.FromSeconds(at))}  {text}");
                        break;

                    case TimelineMark.Diamond:
                        dl.AddQuadFilled(new Vector2(x, mid - radius), new Vector2(x + radius, mid), new Vector2(x, mid + radius),
                                         new Vector2(x - radius, mid), Theme.U32(lane.Color));
                        if (laneHovered && Math.Abs(mouse.X - x) <= Theme.Px(6))
                            tooltip.Add($"{NumberFormat.Duration(TimeSpan.FromSeconds(at))}  {text}");
                        break;

                    default:
                        var pos = new Vector2(x - iconSize / 2, top + rows[i]![k].Row * lane.Height + Theme.Px(1));
                        if (icon != 0)
                            ActionIcons.DrawAt(dl, icon, pos, iconSize);
                        else
                            dl.AddRectFilled(pos, pos + new Vector2(iconSize, iconSize), Theme.U32(Theme.SurfaceActive), iconSize * 0.15f);
                        if (hovered && mouse.X >= pos.X && mouse.X <= pos.X + iconSize && mouse.Y >= pos.Y && mouse.Y <= pos.Y + iconSize)
                            tooltip.Add($"{NumberFormat.Duration(TimeSpan.FromSeconds(at))}  {text}");
                        break;
                }
            }

            y = bottom;
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + gutter);
        ImGui.TextColored(Theme.TextFaint, "0:00");
        var end = NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds));
        ImGui.SameLine(width - ImGui.CalcTextSize(end).X);
        ImGui.TextColored(Theme.TextFaint, end);

        if (tooltip.Count > 0)
            ImGui.SetTooltip(string.Join("\n", tooltip));
    }

    private enum TimelineMark { Dot, Diamond, Icon }

    /// <summary>A row of the timeline: a glyph or a job at its left, then its marks (time, icon, tooltip).</summary>
    private sealed record TimelineLane(
        FontAwesomeIcon? Glyph, string? Job, float Height, Vector4 Color, TimelineMark Mark, List<(double At, uint Icon, string Text)> Marks);

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
