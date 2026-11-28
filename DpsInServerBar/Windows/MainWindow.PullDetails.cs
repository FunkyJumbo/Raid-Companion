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

/// <summary>The details of a pull (History) and the sections shared with Live.</summary>
public sealed partial class MainWindow
{
    private Guid phasesPull;
    private float phasesAt = -100f;

    // An ultimate's pull: one segment per phase, filled one after the other up to the phase reached (red where it
    // ended, all green on a kill).
    private void DrawPhasesReached(PullRecord pull)
    {
        if (UltimateTracker.FightFor(pull.TerritoryId) is not { Phases.Count: > 1 } fight)
            return;
        if (phasesPull != pull.Id)
        {
            phasesPull = pull.Id;
            phasesAt = Fx.Enabled ? Fx.Now : -100f;
        }

        var reached = Math.Clamp(Progression.Phase(pull), 1, fight.Phases.Count);
        var kill = pull.Outcome == PullOutcome.Kill;
        var shown = Fx.EaseOut((Fx.Now - phasesAt) / (0.25f + 0.12f * reached)) * reached;
        var count = fight.Phases.Count;
        var gap = Theme.Px(3);
        var width = Math.Min(ImGui.GetContentRegionAvail().X, Theme.Px(70) * count);
        var segment = (width - gap * (count - 1)) / count;
        var height = Theme.Px(7);
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, Theme.Px(2));
        var dl = ImGui.GetWindowDrawList();
        for (var i = 0; i < count; i++)
        {
            var min = pos + new Vector2(i * (segment + gap), 0);
            var max = min + new Vector2(segment, height);
            dl.AddRectFilled(min, max, Theme.U32(Theme.Surface), height / 2);
            var fill = Fx.Clamp01(shown - i);
            if (fill > 0)
            {
                var color = kill ? Theme.Good : i == reached - 1 ? Theme.Bad : Theme.Gold;
                dl.AddRectFilled(min, new Vector2(min.X + segment * fill, max.Y), Theme.U32(color, 0.85f), height / 2);
                if (fill < 1)
                    Fx.Glow(dl, new Vector2(min.X + segment * fill, min.Y + height / 2), Theme.Px(2.5f), Theme.GoldBright, 0.9f);
            }

            var label = fight.Phases[i].Label;
            var labelWidth = ImGui.CalcTextSize(label).X;
            if (labelWidth < segment)
                dl.AddText(new Vector2((min.X + max.X - labelWidth) / 2, max.Y + Theme.Px(2)), Theme.U32(i < reached ? Theme.TextMuted : Theme.TextFaint), label);
        }

        ImGui.Dummy(new Vector2(width, height + Theme.Px(4) + ImGui.GetTextLineHeight()));
        if (ImGui.IsItemHovered())
            Theme.Tooltip(Loc.F("Reached: {0}", UltimateDefinitions.Label(pull.TerritoryId, reached)));
    }

    private void DrawPullDetails(PullRecord pull, PullRecord? compare)
    {
        var config = plugin.Configuration;
        var top = ImGui.GetCursorPosY();

        ImGui.TextColored(Theme.GoldBright, FightName(pull));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, $"{pull.StartUtc.ToLocalTime():g}");
        DrawPhasesReached(pull);

        DrawPullCards(pull, compare);
        DrawPullPersonal(pull, compare);

        if (Theme.Section(Loc.T("Party"), FontAwesomeIcon.Users, "pull.party"))
            DrawCombatantTable(pull.Combatants);

        // Pulls recorded before deaths and failed mechanics were tracked have no GCD analysis either: nothing to say for them.
        if (pull.Gcd is not null)
        {
            DrawDeaths(pull.PartyDeaths, pull.Mitigations, pull.Debuffs);
            if (pull.Mitigations.Count > 0)
                DrawMitigations(pull.Mitigations);
            DrawMistakes(pull.Mistakes, timeline: true);
            if (pull.PartyDeaths.Count > 0 || pull.Mistakes.Count > 0 || pull.TankSwaps.Count > 0 || pull.Mitigations.Count > 0
                || pull.BossCasts.Count > 0 || pull.Debuffs.Count > 0)
            {
                DrawTimeline(pull);
                if (showFightLog)
                    DrawFightLog(pull);
            }
        }

        if (!Theme.Section(Loc.T("Graph"), FontAwesomeIcon.ChartLine, "pull.graph"))
        {
            detailsHeight = ImGui.GetCursorPosY() - top;
            return;
        }

        var window = config.GraphWindowSeconds;
        graph.DrawOptions(window);

        if (graph.ShowUptime)
        {
            var uptimes = new List<(string, float[], float[])> { (Loc.T("This pull"), pull.UptimeAvailableBySecond, pull.UptimeActiveBySecond) };
            if (compare is { UptimeAvailableBySecond.Length: > 0 })
                uptimes.Add((Loc.T("Compared"), compare.UptimeAvailableBySecond, compare.UptimeActiveBySecond));
            DpsGraph.DrawUptime($"##historyuptime{pull.Id}", uptimes, DpsGraph.Height, pull.Gcd?.Gaps, pull.PartyDeaths);
            detailsHeight = ImGui.GetCursorPosY() - top;
            return;
        }

        var series = new List<DpsGraph.Series>();
        graph.AddCurves(series, Loc.T("This pull"), graph.ShowParty ? pull.PartyDamageTimeline : pull.YourDamageTimeline, window);
        if (compare is not null)
            graph.AddCurves(series, Loc.T("Compared"), graph.ShowParty ? compare.PartyDamageTimeline : compare.YourDamageTimeline,
                            window);

        var threshold = graph.ShowParty ? config.FindThreshold(pull.TerritoryId, pull.Title)?.EffectiveMinPartyDps : null;
        DpsGraph.Draw($"##historygraph{pull.Id}", series, DpsGraph.Height, threshold, pull.PartyDeaths);
        detailsHeight = ImGui.GetCursorPosY() - top;
    }

    /// <summary>The pull's figures, with the gap to the compared pull under each.</summary>
    private void DrawPullCards(PullRecord pull, PullRecord? compare)
    {
        static (string?, Vector4?) Gap(double value, double? other, bool rate = false)
        {
            if (other is not > 0)
                return (null, null);
            var diff = rate ? (value - other.Value) * 100 : value / other.Value - 1;
            var text = rate ? Loc.F("{0:+0.0;-0.0} pts vs compared",diff) : Loc.F("{0:+0.0%;-0.0%} vs compared",diff);
            return (text, diff >= 0 ? Theme.Good : Theme.Bad);
        }

        var cards = new List<Card>();

        var (dpsHint, dpsColor) = Gap(pull.YourDps, compare?.YourDps);
        if (dpsHint is null && pull.PartySize > 1 && pull.Rank > 0)
            dpsHint = Loc.F("#{0} of {1}",pull.Rank,pull.PartySize);
        if (Shown(MainCard.Dps))
            cards.Add(new Card(Loc.T("DPS"), NumberFormat.Format(pull.YourDps, NumberStyle.Grouped), Theme.Gold, dpsHint, dpsColor));

        if (pull.YourRdps is { } rdps && Shown(MainCard.Rdps))
        {
            var (hint, color) = Gap(rdps, compare?.YourRdps);
            if (hint is null && pull.PartySize > 1 && pull.RdpsRank > 0)
                hint = Loc.F("#{0} of {1}",pull.RdpsRank,pull.PartySize);
            cards.Add(new Card(Loc.T("rDPS"), NumberFormat.Format(rdps, NumberStyle.Grouped), Theme.Crystal, hint, color));
        }

        if (pull.Uptime is { } uptime && Shown(MainCard.Gcd))
        {
            var (hint, color) = Gap(uptime, compare?.Uptime, rate: true);
            cards.Add(new Card(Loc.T("GCD uptime"), $"{uptime:0.0%}", Theme.Crystal, hint ?? Loc.F("{0:0.0}s lost",pull.UptimeLostSeconds), color));
        }

        var (partyHint, partyColor) = Gap(pull.PartyDps, compare?.PartyDps);
        if (Shown(MainCard.PartyDps))
            cards.Add(new Card(Loc.T("Party DPS"), NumberFormat.Format(pull.PartyDps, NumberStyle.Grouped), Theme.Gold, partyHint, partyColor));

        // In an ultimate, the time in the phase reached, the whole pull below.
        var durationHint = compare is not null ? Loc.F("vs {0}",NumberFormat.Duration(TimeSpan.FromSeconds(compare.PhaseSeconds ?? compare.DurationSeconds)))
                           : pull.PhaseSeconds is not null ? Loc.F("whole pull {0}", NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds)))
                           : null;
        var durationCaption = pull.PhaseSeconds is not null
                                  ? Loc.F("{0} duration", UltimateDefinitions.Label(pull.TerritoryId, Progression.Phase(pull)))
                                  : Loc.T("Duration");
        if (Shown(MainCard.Duration))
            cards.Add(new Card(durationCaption, NumberFormat.Duration(TimeSpan.FromSeconds(pull.PhaseSeconds ?? pull.DurationSeconds)),
                                      pull.Outcome switch { PullOutcome.Kill => Theme.Good, PullOutcome.Wipe => Theme.Bad, _ => Theme.TextMuted },
                                      durationHint));

        // Boss HP left when the pull ended (of the phase reached, in an ultimate), or Kill.
        if ((pull.Outcome == PullOutcome.Kill || pull.BossHpLeft is not null) && Shown(MainCard.BossHp))
        {
            var ultimate = Progression.IsUltimate(pull.TerritoryId);
            var value = pull.Outcome == PullOutcome.Kill ? Loc.T("Kill") : Loc.F("{0:0.0%} left", pull.BossHpLeft!.Value);
            string? hint = ultimate ? UltimateDefinitions.Label(pull.TerritoryId, Progression.Phase(pull))
                           : pull.BossName.Length > 0 ? pull.BossName : null;
            Vector4? hintColor = null;
            if (compare is not null && pull.Outcome != PullOutcome.Kill && Progression.Score(pull) is { } score && Progression.Score(compare) is { } other)
            {
                // Progress compared: boss HP points (or phases) further or not as far.
                hint = ultimate ? hint : Loc.F("{0:+0.0;-0.0} pts vs compared", score - other);
                hintColor = score >= other ? Theme.Good : Theme.Bad;
            }

            cards.Add(new Card(Loc.T("Boss HP"), value, Theme.Bad, hint, hintColor,
                               pull.Outcome == PullOutcome.Kill ? Theme.Good : null));
        }

        DrawCards(cards, uniform: true);
    }

    /// <summary>
    /// Your own play in a pull, one foldable line each: the figures on the line, the detail inside.
    /// </summary>
    // The lines of Personal the player wants (Settings), read by the static drawing below.
    private static PersonalElement personalShown = PersonalElement.All;

    private static void DrawPullPersonal(PullRecord pull, PullRecord? compare)
    {
        bool Shown(PersonalElement element) => personalShown.HasFlag(element);
        var role = JobRoles.Of(pull.Job);
        var uptime = Shown(PersonalElement.Gcd) ? pull.Uptime : null;
        var rotation = Shown(PersonalElement.Rotation) && pull.Rotation.Count > 0;
        var gauge = Shown(PersonalElement.Gauge) && pull.Gcd is not null && GaugeTracker.Tracks(pull.Job);
        var positionals = Shown(PersonalElement.Positionals) && pull.Positionals.Count > 0;
        var combos = Shown(PersonalElement.RotationErrors) && pull.ComboMistakes.Count > 0;
        var plan = Shown(PersonalElement.MitigationPlan) ? pull.PlanResults : null;
        var dots = Shown(PersonalElement.Dots) && pull.DotUptimes is { Count: > 0 } d && d[0].AvailableSeconds >= 5 ? d : null;
        var heals = Shown(PersonalElement.Overheal) && role == JobRole.Healer
                        ? pull.Overheal.Where(h => JobRoles.Of(h.Job) == JobRole.Healer).ToList()
                        : [];
        var losses = Shown(PersonalElement.DamageLoss) ? DamageDownLoss.Of(pull) : [];
        var mitigation = Shown(PersonalElement.Mitigation) && pull.Mitigations.Count > 0;
        var tank = Shown(PersonalElement.TankSwaps) && role == JobRole.Tank;
        if (uptime is null && !rotation && !gauge && !positionals && !combos && plan is null && dots is null
            && heals.Count == 0 && !(tank && pull.TankSwaps.Count > 0) && !mitigation && losses.Count == 0)
            return;

        if (!Theme.Section(Loc.T("Personal"), FontAwesomeIcon.User, "pull.personal"))
            return;
        if (uptime is { } gcdUptime)
            FoldGcd(pull, compare, gcdUptime);
        if (rotation)
            FoldRotation(pull);
        if (losses.Count > 0)
            FoldDamageLoss(pull, losses);
        if (gauge)
            FoldGauge(pull.GaugeWaste);
        if (positionals)
            FoldPositionals(pull.Positionals);
        if (combos)
            FoldCombos(pull.ComboMistakes);
        if (plan is not null)
            FoldPlanMisses(plan, pull.PlanName);
        if (dots is not null)
            FoldDots(dots);
        if (heals.Count > 0)
            FoldOverheal(heals);
        if (mitigation)
            FoldMitigations(pull.Mitigations.Where(m => m.IsYou).ToList());
        if (tank)
            FoldTankSwaps(pull.TankSwaps);
    }

    // A foldable line: its title, then the figures drawn by the caller on the same line.
    private static bool Fold(string title, string id)
    {
        var open = ImGui.TreeNode($"{title}###{id}");
        ImGui.SameLine(0, Theme.Px(8));
        return open;
    }

    // A detail table in a foldable line: fixed-width columns get a width, the others stretch.
    private static Theme.ThemeTable DetailTable(string id, params (string Name, float Width)[] columns)
    {
        // As wide as its content: a column with a width keeps it, the others fit what they show.
        var table = Theme.Table(id, columns.Length, ImGuiTableFlags.RowBg | Theme.FitTable);
        if (!table)
            return table;
        foreach (var (name, width) in columns)
        {
            if (width > 0)
                ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed, Theme.Px(width));
            else
                ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed);
        }

        ImGui.TableHeadersRow();
        return table;
    }

    private static void TimeCell(double seconds)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(seconds)));
    }

    private static void IconCell(uint icon, string name, Vector4? color = null)
    {
        ImGui.TableNextColumn();
        ActionIcons.Draw(icon, ImGui.GetTextLineHeight());
        ImGui.SameLine(0, Theme.Px(4));
        ImGui.TextColored(color ?? Theme.Text, name);
    }

    /// <summary>DPS lost to Damage Down (failed mechanics) and to Weakness / Brink of Death (after a raise).</summary>
    private static void FoldDamageLoss(PullRecord pull, IReadOnlyList<DamageLoss> losses)
    {
        var duration = Math.Max(1, pull.DurationSeconds);
        double Dps(IEnumerable<DamageLoss> l) => l.Sum(x => x.LostDamage) / duration;
        string Format(double dps) => NumberFormat.Format(dps, NumberStyle.Grouped);

        var open = Fold(Loc.T("Damage Down & Weakness"), "pdown");
        var down = losses.Where(l => !l.Revive).ToList();
        var revive = losses.Where(l => l.Revive).ToList();
        ImGui.TextColored(Theme.Warn, Loc.T("(experimental)"));
        if (ImGui.IsItemHovered())
            Theme.Tooltip(Loc.T("Experimental: an estimate, the strength of Damage Down is not always known. To be checked against your logs."));
        // Not checked yet against logs: the figures only once unfolded.
        if (!open)
            return;

        ImGui.TextColored(Theme.Bad, Loc.F("-{0} DPS", Format(Dps(losses))));
        if (down.Count > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.TextColored(Theme.TextFaint, Loc.F("Damage Down -{0} ({1}x)", Format(Dps(down)), down.Count));
        }

        if (revive.Count > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.TextColored(Theme.TextFaint, Loc.F("Weakness -{0} ({1}x)", Format(Dps(revive)), revive.Count));
        }

        var dealt = pull.YourDamageTimeline.Length > 0 ? pull.YourDamageTimeline[^1] : 0;
        if (dealt > 0)
        {
            Theme.Hint(Loc.F("Without them you would have done {0} DPS instead of {1} (+{2:0.0%}).",
                             Format((dealt + losses.Sum(l => l.LostDamage)) / duration), Format(dealt / duration),
                             losses.Sum(l => l.LostDamage) / dealt));
        }

        using (var table = DetailTable("##pdowntable", (Loc.T("Time"), 60), (Loc.T("Debuff"), -3), (Loc.T("Length"), 60),
                                       (Loc.T("Strength"), 70), (Loc.T("Lost"), -2)))
        {
            if (table)
            {
                foreach (var l in losses)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    TimeCell(l.Start);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(l.Revive ? Theme.Warn : Theme.Bad, l.Status);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, $"{l.End - l.Start:0}s");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(l.Guessed ? Theme.TextFaint : Theme.Text, $"{(l.Guessed ? "~" : "")}-{l.Reduction:0%}");
                    if (l.Guessed && ImGui.IsItemHovered())
                        Theme.Tooltip(Loc.T("The game did not send this debuff's strength: the usual one is used (-90% in an ultimate, -25% elsewhere)."));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, Loc.F("{0} damage (-{1} DPS)", Format(l.LostDamage), Format(l.LostDamage / duration)));
                }
            }
        }

        Theme.Hint(Loc.T("Experimental estimate: your damage while the debuff was on, scaled back up by its strength. Weakness -25% and Brink of Death -50% (main stats lowered by that much)."));
        ImGui.TreePop();
    }

    private static void FoldGcd(PullRecord pull, PullRecord? compare, double uptime)
    {
        var open = Fold(Loc.T("GCD uptime"), "pgcd");
        ImGui.TextColored(Theme.Crystal, $"{uptime:0.0%}");
        if (compare?.Uptime is { } other)
        {
            var diff = (uptime - other) * 100;
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(diff >= 0 ? Theme.Good : Theme.Bad, Loc.F("{0:+0.0;-0.0} pts vs compared",diff));
        }

        ImGui.SameLine(0, Theme.Px(8));
        ImGui.TextColored(Theme.TextFaint, pull.Gcd is { GcdCount: > 0 } g
                                               ? Loc.F("{0} GCDs, lost {1:0.0}s",g.GcdCount,pull.UptimeLostSeconds)
                                               : Loc.F("lost {0:0.0}s",pull.UptimeLostSeconds));
        if (!open)
            return;

        if (pull.PhaseUptimes.Count > 0)
        {
            Theme.Hint(Loc.T("Per phase: ") + string.Join("   ", pull.ReachedPhases.Select(
                                         p => $"{UltimateDefinitions.Label(pull.TerritoryId, p.Number)} {p.Uptime:0.0%}")));
        }

        if (pull.Gcd is { } analysis)
        {
            DrawGcdAnalysis(analysis, showGaps: false);
            DrawClipTimeline(analysis, pull.DurationSeconds, folded: false);
        }

        ImGui.TreePop();
    }

    private static void FoldGauge(IReadOnlyList<GaugeWaste> waste)
    {
        var open = Fold(Loc.T("Gauge overcap"), "pgauge");
        if (waste.Count == 0)
        {
            ImGui.TextColored(Theme.Good, Loc.T("none"));
        }
        else
        {
            for (var i = 0; i < waste.Count; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0, Theme.Px(10));
                ActionIcons.Draw(ActionIcons.OfGauge(waste[i].Resource), ImGui.GetTextLineHeight());
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(Theme.Bad, GaugeAmount(waste[i]));
            }
        }

        if (!open)
            return;

        if (waste.Count == 0)
        {
            Theme.Hint(Loc.T("No gauge lost to overcapping."));
        }
        else
        {
            using (var table = DetailTable("##pgaugetable", (Loc.T("Gauge"), -2), (Loc.T("Lost"), 60), (Loc.T("Times"), 50), (Loc.T("Overcapped by"), -4)))
            {
                if (table)
                {
                    foreach (var w in waste)
                    {
                        ImGui.TableNextRow();
                        Theme.RowHover();
                        IconCell(ActionIcons.OfGauge(w.Resource), w.Resource);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Theme.Bad, GaugeAmount(w));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{w.Count}×");
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Theme.TextMuted, w.Actions);
                    }
                }
            }
        }

        ImGui.TreePop();
    }

    private static string GaugeAmount(GaugeWaste w) => w.Max <= 5 ? $"{w.Wasted:0.#}" : $"{w.Wasted:0}";

    private static void FoldPositionals(IReadOnlyList<PositionalUse> uses)
    {
        var hits = uses.Count(u => u.Hit);
        var share = (double)hits / uses.Count;
        var open = Fold(Loc.T("Positionals"), "ppos");
        ImGui.TextColored(share >= 1 ? Theme.Good : Theme.Bad, $"{hits}/{uses.Count} ({share:0%})");
        foreach (var action in uses.GroupBy(u => u.ActionId))
        {
            var landed = action.Count(u => u.Hit);
            ImGui.SameLine(0, Theme.Px(10));
            var hovered = ActionIcons.Draw(ActionIcons.OfAction(action.Key), ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(landed == action.Count() ? Theme.Good : Theme.Bad, $"{landed}/{action.Count()}");
            if (hovered || ImGui.IsItemHovered())
                Theme.Tooltip(PositionalTooltip(action.ToList()));
        }

        if (!open)
            return;

        var missed = uses.Where(u => !u.Hit).ToList();
        if (missed.Count == 0)
        {
            Theme.Hint(Loc.T("Every positional landed."));
        }
        else
        {
            using var table = DetailTable("##ppostable", (Loc.T("Time"), 60), (Loc.T("Action"), -3), (Loc.T("Needed"), -2));
            if (table)
            {
                foreach (var u in missed)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    TimeCell(u.AtSeconds);
                    IconCell(ActionIcons.OfAction(u.ActionId), GameNames.Action(u.ActionId, u.Action));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, u.Rear ? Loc.T("rear") : Loc.T("flank"));
                }
            }
        }

        ImGui.TreePop();
    }

    private static void FoldCombos(IReadOnlyList<ComboMistake> mistakes)
    {
        var open = Fold(Loc.T("Rotation errors"), "pcombo");
        ImGui.TextColored(Theme.Bad, mistakes.Count.ToString());
        foreach (var group in mistakes.GroupBy(m => m.ActionId).OrderByDescending(g => g.Count()))
        {
            ImGui.SameLine(0, Theme.Px(10));
            ActionIcons.Draw(ActionIcons.OfAction(group.Key), ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.Bad, $"×{group.Count()}");
        }

        if (!open)
            return;

        using (var table = DetailTable("##pcombotable", (Loc.T("Time"), 60), (Loc.T("Action"), -2), (Loc.T("Error"), -5)))
        {
            if (table)
            {
                foreach (var m in mistakes)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    TimeCell(m.AtSeconds);
                    IconCell(ActionIcons.OfAction(m.ActionId), ComboMistakeName(m));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, ComboMistakeDetail(m));
                }
            }
        }

        ImGui.TreePop();
    }

    private static void FoldPlanMisses(IReadOnlyList<PlanMiss> misses, string plan)
    {
        var open = Fold(Loc.T("Mitigation plan"), "pplan");
        PlanMissesSummary(misses, plan);
        if (!open)
            return;

        if (misses.Count == 0)
        {
            Theme.Hint(Loc.T("Nothing of your column was checked in this pull."));
        }
        else
        {
            using var table = DetailTable("##pplantable", (Loc.T("Time"), 60), (Loc.T("Skill"), -2), (Loc.T("Cast"), -3), (Loc.T("Result"), -1));
            if (table)
            {
                foreach (var m in misses)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    TimeCell(m.AtSeconds);
                    IconCell(ActionIcons.OfAction(m.ActionId), GameNames.Action(m.ActionId, m.Action));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, m.Cast);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(m.Used ? Theme.Good : Theme.Bad, m.Used ? Loc.T("in time") : Loc.T("missed"));
                }
            }
        }

        ImGui.TreePop();
    }

    // Like the positionals: how many of the checks passed, then each skill with its uses in time out of those planned,
    // green when all were, red otherwise.
    private static void PlanMissesSummary(IReadOnlyList<PlanMiss> checks, string plan)
    {
        if (checks.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, Loc.T("nothing checked yet"));
        }
        else
        {
            var used = checks.Count(c => c.Used);
            ImGui.TextColored(used == checks.Count ? Theme.Good : Theme.Bad, $"{used}/{checks.Count} ({(double)used / checks.Count:0%})");
            foreach (var group in checks.GroupBy(m => m.ActionId))
            {
                var list = group.ToList();
                var inTime = list.Count(c => c.Used);
                ImGui.SameLine(0, Theme.Px(10));
                var hovered = ActionIcons.Draw(ActionIcons.OfAction(group.Key), ImGui.GetTextLineHeight());
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(inTime == list.Count ? Theme.Good : Theme.Bad, $"{inTime}/{list.Count}");
                if (hovered || ImGui.IsItemHovered())
                    Theme.Tooltip(string.Join("\n", list.Select(PlanMissText)));
            }
        }

        if (plan.Length > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.TextColored(Theme.TextFaint, plan);
        }
    }

    private static string PlanMissText(PlanMiss m) =>
        m.Used
            ? Loc.F("{0}  {1} used for {2}", NumberFormat.Duration(TimeSpan.FromSeconds(Math.Max(0, m.AtSeconds))),
                    GameNames.Action(m.ActionId, m.Action), m.Cast)
            : Loc.F("{0}  {1} not used for {2}", NumberFormat.Duration(TimeSpan.FromSeconds(Math.Max(0, m.AtSeconds))),
                    GameNames.Action(m.ActionId, m.Action), m.Cast);

    /// <summary>One line with the skills of your column of the mitigation plan not used in time; hover for details.</summary>
    private static void DrawPlanMisses(IReadOnlyList<PlanMiss> misses, string plan)
    {
        ImGui.TextColored(Theme.TextMuted, Loc.T("Mitigation plan"));
        ImGui.SameLine(0, Theme.Px(6));
        PlanMissesSummary(misses, plan);
        Theme.HelpMarker(Loc.T("The skills of your column of the active mitigation plan, each with how many times it was used in time out of those planned: between 15 s before the cast landed and 1 s after (the pre-pull: around its time). Green when all were, red otherwise; hover a skill for each cast. Choose your column and the plan in the Mitigation plans window."));
    }

    private static void FoldDots(IReadOnlyList<DotUptime> dots)
    {
        var open = Fold(Loc.T("DoT uptime"), "pdot");
        for (var i = 0; i < dots.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.Px(10));
            ActionIcons.Draw(dots[i].Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(DotColor(dots[i].Uptime), $"{dots[i].Uptime:0.0%}");
        }

        if (!open)
            return;

        foreach (var dot in dots)
        {
            ActionIcons.Draw(dot.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(DotColor(dot.Uptime), $"{dot.NameText} {dot.Uptime:0.0%}");
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(Theme.TextFaint, Loc.F("off the boss {0:0.0}s ",dot.AvailableSeconds - dot.ActiveSeconds)
                                               + Loc.F("of {0}",NumberFormat.Duration(TimeSpan.FromSeconds(dot.AvailableSeconds))));
        }

        var drops = dots.SelectMany(dot => (dot.Drops ?? []).Select(x => (Dot: dot, Drop: x))).OrderBy(x => x.Drop.AtSeconds).ToList();
        if (dots.All(dot => dot.Drops is null))
        {
            Theme.Hint(Loc.T("The drops were not recorded for this pull."));
        }
        else if (drops.Count == 0)
        {
            Theme.Hint(Loc.T("Never off the boss for a second or more."));
        }
        else
        {
            using var table = DetailTable("##pdottable", (Loc.T("Time"), 60), (Loc.T("DoT"), -3), (Loc.T("Off for"), -2));
            if (table)
            {
                foreach (var (dot, drop) in drops)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    TimeCell(drop.AtSeconds);
                    IconCell(dot.Icon, dot.NameText);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(drop.Seconds >= 3 ? Theme.Bad : Theme.Warn, $"{drop.Seconds:0.0}s");
                }
            }
        }

        ImGui.TreePop();
    }

    private static Vector4 DotColor(double uptime) => uptime >= 0.95 ? Theme.Good : uptime >= 0.85 ? Theme.Warn : Theme.Bad;

    private static void FoldOverheal(IReadOnlyList<HealerOverheal> healers)
    {
        var mine = healers.FirstOrDefault(h => h.IsYou);
        var open = Fold(Loc.T("Overheal"), "pheal");
        ImGui.TextColored(Theme.Text, mine is null ? "-" : $"{mine.Share:0%}");
        ImGui.SameLine(0, Theme.Px(8));
        ImGui.TextColored(Theme.TextFaint, Loc.T("for information"));
        if (!open)
            return;

        DrawOverheal(healers);
        if (mine?.Sources is { Length: > 0 } sources)
        {
            using var table = DetailTable("##phealtable", (Loc.T("Your heals"), -3), (Loc.T("Healing"), 80), (Loc.T("Overheal"), 80), ("%", 50));
            if (table)
            {
                foreach (var s in sources)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    IconCell(s.Icon, s.IsHot ? Loc.F("{0} (HoT)",s.Name) : s.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(NumberFormat.Format(s.Healing, NumberStyle.Compact));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(NumberFormat.Format(s.Overheal, NumberStyle.Compact));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{s.Share:0%}");
                }
            }
        }
        else if (mine is not null)
        {
            Theme.Hint(Loc.T("The detail per heal was not recorded for this pull."));
        }

        ImGui.TreePop();
    }

    /// <summary>When each party member used each of their mitigations and defensive buffs: tanks, healers, then DPS.</summary>
    private static void DrawMitigations(IReadOnlyList<MitigationUse> uses)
    {
        if (!Theme.Section(Loc.T("Mitigation"), FontAwesomeIcon.ShieldAlt, "pull.mitigation", Loc.T("Mitigations (on the party or on the boss) and defensive buffs used by each party member, with the time of each use. GCD shields (Succor, Eukrasian Prognosis...) are left out. Party members far from you may be missed.")))
            return;

        static int RoleOrder(string job) => JobRoles.Of(job) switch { JobRole.Tank => 0, JobRole.Healer => 1, _ => 2 };
        var players = uses.GroupBy(u => u.Player)
                          .OrderBy(g => RoleOrder(g.First().Job))
                          .ThenByDescending(g => g.First().IsYou)
                          .ThenBy(g => g.Key)
                          .ToList();

        // One foldable line per player (job, name, number of uses), the detail inside.
        foreach (var player in players)
        {
            var p = player.First();
            var lineStart = ImGui.GetCursorPosX();
            var open = ImGui.TreeNode($"###mit{player.Key}");
            ImGui.SameLine(lineStart + ImGui.GetTreeNodeToLabelSpacing()); // after the arrow
            JobInfo.DrawIcon(p.Job, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(p.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(p.Player));
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(Theme.TextFaint, player.Count() == 1 ? Loc.T("1 use") : Loc.F("{0} uses",player.Count()));
            if (!open)
                continue;

            using (var table = DetailTable($"##mittable{player.Key}", (Loc.T("Mitigation"), -2), (Loc.T("Uses"), 40), (Loc.T("Times"), 380)))
            {
                if (table)
                {
                    foreach (var action in player.GroupBy(u => GameNames.Action(u.ActionId, u.Action)).OrderBy(g => g.First().AtSeconds))
                    {
                        ImGui.TableNextRow();
                        Theme.RowHover();
                        IconCell(ActionIcons.OfAction(action.First().ActionId), action.Key);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(action.Count().ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(string.Join("   ", action.Select(u => NumberFormat.Duration(TimeSpan.FromSeconds(u.AtSeconds)))));
                    }
                }
            }

            ImGui.TreePop();
        }
    }

    private static void FoldMitigations(IReadOnlyList<MitigationUse> mine)
    {
        var open = Fold(Loc.T("Mitigation"), "pmit");
        ImGui.TextColored(mine.Count == 0 ? Theme.TextFaint : Theme.Text, mine.Count == 0 ? Loc.T("none") : Loc.F("{0} uses",mine.Count));
        if (!open)
            return;

        if (mine.Count == 0)
        {
            Theme.Hint(Loc.T("You used no mitigation or defensive buff."));
        }
        else
        {
            using var table = DetailTable("##pmittable", (Loc.T("Action"), -3), (Loc.T("Uses"), 60));
            if (table)
            {
                foreach (var g in mine.GroupBy(m => GameNames.Action(m.ActionId, m.Action)).OrderByDescending(g => g.Count()))
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    IconCell(ActionIcons.OfAction(g.First().ActionId), g.Key);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(g.Count().ToString());
                }
            }
        }

        ImGui.TreePop();
    }

    private static void FoldTankSwaps(IReadOnlyList<TankSwap> swaps)
    {
        var open = Fold(Loc.T("Tank swaps"), "pswaps");
        ImGui.TextColored(Theme.Text, swaps.Count.ToString());
        if (!open)
            return;

        if (swaps.Count == 0)
            Theme.Hint(Loc.T("The boss never changed tanks."));
        else
            DrawTankSwapTable(swaps);
        ImGui.TreePop();
    }

    /// <summary>One line with the uptime of each of your DoTs on the boss, by icon and name.</summary>
    private static void DrawDotUptime(IReadOnlyList<DotUptime> dots)
    {
        if (dots.Count == 0 || dots[0].AvailableSeconds < 5)
            return;

        ImGui.TextColored(Theme.TextMuted, Loc.T("DoT uptime"));
        foreach (var dot in dots)
        {
            ImGui.SameLine(0, Theme.Px(8));
            ActionIcons.Draw(dot.Icon, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(dot.Uptime >= 0.95 ? Theme.Good : dot.Uptime >= 0.85 ? Theme.Warn : Theme.Bad, $"{dot.NameText} {dot.Uptime:0.0%}");
            if (ImGui.IsItemHovered())
                Theme.Tooltip(Loc.F("Off the boss for {0:0.0}s of {1}",dot.AvailableSeconds - dot.ActiveSeconds,NumberFormat.Duration(TimeSpan.FromSeconds(dot.AvailableSeconds))));
        }

        Theme.HelpMarker(Loc.T("Share of the time your DoT was on the boss, while the boss could be attacked (jumps, transitions and cutscenes left out)."));
    }

    /// <summary>One line with the overheal of each healer of the party (for information: no color, it has many causes).</summary>
    /// <param name="foldable">One foldable line with your overheal, the healers' detail inside.</param>
    private static void DrawOverheal(IReadOnlyList<HealerOverheal> heals, bool foldable = false)
    {
        var healers = heals.Where(h => JobRoles.Of(h.Job) == JobRole.Healer).ToList();
        if (healers.Count == 0)
            return;

        if (foldable)
        {
            var mine = healers.FirstOrDefault(h => h.IsYou);
            var open = ImGui.TreeNode(mine is null ? Loc.T("Overheal###overheal") : Loc.F("Overheal ({0:0%})###overheal",mine.Share));
            ImGui.SameLine();
            Theme.HelpMarker(Loc.T("Share of each healer's healing (pets and HoTs included) that went over full HP. For information only: pre-healing a raidwide or a HoT running out its time also overheals."));
            if (!open)
                return;
        }
        else
        {
            ImGui.TextColored(Theme.TextMuted, Loc.T("Overheal"));
        }

        foreach (var h in healers.OrderByDescending(h => h.IsYou))
        {
            if (!foldable)
                ImGui.SameLine(0, Theme.Px(8));
            JobInfo.DrawIcon(h.Job, ImGui.GetTextLineHeight());
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(h.IsYou ? Theme.GoldBright : Theme.Text, $"{(h.IsYou ? Loc.T("You") : Names.Show(h.Player))} {h.Share:0%}");
            if (ImGui.IsItemHovered())
                Theme.Tooltip(Loc.F("{0} of {1} healing went over full HP",NumberFormat.Format(h.Overheal, NumberStyle.Compact),NumberFormat.Format(h.Healing, NumberStyle.Compact)));
        }

        if (foldable)
        {
            ImGui.TreePop();
            return;
        }

        Theme.HelpMarker(Loc.T("Share of each healer's healing (pets and HoTs included) that went over full HP. For information only: pre-healing a raidwide or a HoT running out its time also overheals."));
    }

    /// <summary>Foldable list of the boss going from one tank to another, with why.</summary>
    private static void DrawTankSwaps(IReadOnlyList<TankSwap> swaps, bool showEmpty = false)
    {
        if (swaps.Count == 0 && showEmpty)
        {
            ImGui.TextColored(Theme.TextMuted, Loc.T("Tank swaps"));
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.TextFaint, "0");
            return;
        }

        if (swaps.Count == 0 || !ImGui.TreeNode(Loc.F("Tank swaps ({0})###tankswaps",swaps.Count)))
            return;

        DrawTankSwapTable(swaps);
        ImGui.TreePop();
    }

    private static void DrawTankSwapTable(IReadOnlyList<TankSwap> swaps)
    {
        using (var table = Theme.Table("##tankswaps", 3, ImGuiTableFlags.RowBg | Theme.FitTable))
        {
            if (table)
            {
                ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(60));
                ImGui.TableSetupColumn(Loc.T("Swap"), ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn(Loc.T("Why"), ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableHeadersRow();
                foreach (var s in swaps)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(s.AtSeconds)));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{Names.Show(s.From)} → {Names.Show(s.To)}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(s.Reason == "Death" ? Theme.Bad : Theme.TextMuted, s.Reason);
                }
            }
        }

    }

    /// <summary>
    /// One line with your positionals hit, then the actions that missed some, by icon (with their name in the
    /// history); hover one for when.
    /// </summary>
    private static void DrawPositionals(IReadOnlyList<PositionalUse> uses, bool names)
    {
        if (uses.Count == 0)
            return;

        var hits = uses.Count(u => u.Hit);
        var share = (double)hits / uses.Count;
        ImGui.TextColored(Theme.TextMuted, Loc.T("Positionals"));
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.TextColored(share >= 1 ? Theme.Good : Theme.Bad, $"{hits}/{uses.Count} ({share:0%})");

        var iconSize = ImGui.GetTextLineHeight();
        // Every positional action, with how many of its uses landed: green while none missed, red after the first miss.
        foreach (var action in uses.GroupBy(u => u.ActionId))
        {
            var list = action.ToList();
            var landed = list.Count(u => u.Hit);
            ImGui.SameLine(0, Theme.Px(10));
            var hovered = ActionIcons.Draw(ActionIcons.OfAction(action.Key), iconSize);
            ImGui.SameLine(0, Theme.Px(4));
            var count = $"{landed}/{list.Count}";
            ImGui.TextColored(landed == list.Count ? Theme.Good : Theme.Bad,
                              names ? $"{GameNames.Action(list[0].ActionId, list[0].Action)} {count}" : count);
            if (hovered || ImGui.IsItemHovered())
                Theme.Tooltip(PositionalTooltip(list));
        }

        Theme.HelpMarker(
            Loc.T("Your positional actions whose positional bonus landed, read from the bonus the game gave each hit (True North and bosses without positionals count as hits).\nHover an action for when you missed it."));
    }

    /// <summary>An action's positionals: how many landed, and when the others were missed.</summary>
    private static string PositionalTooltip(IReadOnlyList<PositionalUse> uses)
    {
        var name = GameNames.Action(uses[0].ActionId, uses[0].Action);
        var side = uses[0].Rear ? Loc.T("rear") : Loc.T("flank");
        var missed = uses.Where(u => !u.Hit).ToList();
        var text = Loc.F("{0} ({1}): {2} of {3} landed", name, side, uses.Count - missed.Count, uses.Count);
        if (missed.Count > 0)
            text += Loc.F("\nMissed at {0}", string.Join(", ", missed.Select(u => NumberFormat.Duration(TimeSpan.FromSeconds(u.AtSeconds)))));
        return text;
    }

    /// <summary>One line with your combo errors, by icon of the action used (with its name in the history); hover for details.</summary>
    private static void DrawComboMistakes(IReadOnlyList<ComboMistake> mistakes, bool names, bool showNone)
    {
        if (mistakes.Count == 0 && !showNone)
            return;

        ImGui.TextColored(Theme.TextMuted, Loc.T("Rotation errors"));
        ImGui.SameLine(0, Theme.Px(6));
        if (mistakes.Count == 0)
        {
            ImGui.TextColored(Theme.Good, Loc.T("none"));
        }
        else
        {
            ImGui.TextColored(Theme.Bad, mistakes.Count.ToString());
            var iconSize = ImGui.GetTextLineHeight();
            foreach (var group in mistakes.GroupBy(m => m.ActionId).OrderByDescending(g => g.Count()))
            {
                var list = group.ToList();
                ImGui.SameLine(0, Theme.Px(10));
                var hovered = ActionIcons.Draw(ActionIcons.OfAction(group.Key), iconSize);
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(Theme.Bad, names ? $"{ComboMistakeName(list[0])} ×{list.Count}" : $"×{list.Count}");
                if (hovered || ImGui.IsItemHovered())
                    Theme.Tooltip(string.Join("\n", list.Select(ComboMistakeText)));
            }
        }

        Theme.HelpMarker(Loc.T("Combo steps used without the step before them (they lose their combo bonus), combos restarted before they were finished, combos whose timer ran out before the next step, and job errors: buffs that ran out unused (Paladin: Goring Blade, Divine Might, Requiescat, Blade of Honor, Atonement, Confiteor; Warrior: Inner Release, Berserk, Primal Rend, Primal Ruination, Nascent Chaos, Wrathful; Dark Knight: Scorn, Delirium; Gunbreaker: Ready to Break, Ready to Reign, and each continuation, also when the next GCD replaces it), offensive actions used without a buff that must stay up (Warrior: Surging Tempest, after the 3 GCDs allowed to put it back at the pull or after downtime; Dark Knight: Darkside, after 1 GCD), and a Paladin Holy Spirit or Holy Circle spending a Requiescat charge from level 80, Paladin Royal Authority replacing an Atonement combo not finished, Dark Knight Delirium combo broken by a basic combo step, Gunbreaker Gnashing Fang combo broken (basic combos, Sonic Break, Double Down) and Reign of Beasts combo broken (basic combos, Gnashing Fang combo, Fated Circle), The Blackest Night running out without breaking, and Salted Earth ending without Salt and Darkness. Actions the game lets you use without breaking a combo are ignored."));
    }

    // The buff for a time without it (its line shows the buff, not the action that puts it up), else the action.
    private static string ComboMistakeName(ComboMistake m) =>
        m.Kind == ComboMistakeKind.WithoutBuff ? GameNames.Status(m.ExpectedId, m.Expected) : GameNames.Action(m.ActionId, m.Action);

    private static string ComboMistakeText(ComboMistake m) =>
        $"{NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds))}  {ComboMistakeDetail(m)}";

    private static string ComboMistakeDetail(ComboMistake m) => m.Kind switch
    {
        ComboMistakeKind.NoCombo => Loc.F("{0} without its combo (needs {1} before)",GameNames.Action(m.ActionId, m.Action),GameNames.Action(m.ExpectedId, m.Expected)),
        ComboMistakeKind.WrongCharge => Loc.F("{0} spent a charge of {1}: from level 80 they go to Confiteor and its combo",GameNames.Action(m.ActionId, m.Action),GameNames.Action(m.ExpectedId, m.Expected)),
        ComboMistakeKind.BuffExpired when m.Expired => Loc.F("{0} lost unused: the next GCD came first",GameNames.Status(m.ExpectedId, m.Expected)),
        ComboMistakeKind.BuffExpired when m.Count > 1 => Loc.F("{0} ran out with {1} charges unused",GameNames.Status(m.ExpectedId, m.Expected),m.Count),
        ComboMistakeKind.BuffExpired when m.Count == 1 => Loc.F("{0} ran out with 1 charge unused",GameNames.Status(m.ExpectedId, m.Expected)),
        ComboMistakeKind.WithoutBuff when m.Count > 1 => Loc.F("{0} missing: {1} offensive actions without it, for {2:0}s",GameNames.Status(m.ExpectedId, m.Expected),m.Count,m.EndSeconds - m.AtSeconds),
        ComboMistakeKind.WithoutBuff => Loc.F("{0} missing: 1 offensive action without it",GameNames.Status(m.ExpectedId, m.Expected)),
        ComboMistakeKind.SideComboBroken => Loc.F("{0} combo broken by {1} after {2}",GameNames.Action(m.ActionId, m.Action),GameNames.Action(m.ExpectedId, m.Expected),GameNames.Action(m.StepId, m.Step)),
        ComboMistakeKind.ComboOverwritten => Loc.F("{0} combo replaced by {1}: {2} was still to use",GameNames.Action(m.ActionId, m.Action),GameNames.Action(m.ExpectedId, m.Expected),GameNames.Action(m.StepId, m.Step)),
        ComboMistakeKind.ShieldUnbroken => Loc.F("{0} ran out without breaking: no free {1}",GameNames.Action(m.ActionId, m.Action),GameNames.Action(m.ExpectedId, m.Expected)),
        ComboMistakeKind.FollowUpMissed => Loc.F("{0} not used before the end of {1}",GameNames.Action(m.ActionId, m.Action),GameNames.Action(m.ExpectedId, m.Expected)),
        ComboMistakeKind.BuffExpired => Loc.F("{0} ran out unused",GameNames.Status(m.ExpectedId, m.Expected)),
        ComboMistakeKind.TimedOut => Loc.F("combo lost after {0}: its timer ran out before the next step",GameNames.Action(m.ActionId, m.Action)),
        _ => Loc.F("{0} restarted the combo: {1} was waiting for its next step",GameNames.Action(m.ActionId, m.Action),GameNames.Action(m.ExpectedId, m.Expected)),
    };

    /// <summary>A foldable strip of the pull with each clip at its time, shown by the icon of the oGCD that caused it.</summary>
    private static void DrawClipTimeline(GcdAnalysis gcd, double pullSeconds, bool folded = true)
    {
        var clips = gcd.Gaps.Where(g => g.IsClip).ToList();
        if (clips.Count == 0 || (folded && !ImGui.TreeNode(Loc.F("Clipping timeline ({0})###clipping",clips.Count))))
            return;

        var marks = clips.Select(g =>
        {
            var locked = g.LockedSeconds > 0 ? g.LockedSeconds : g.Seconds;
            var cause = g.ActionId != 0 ? Loc.F(" by {0}",ActionIcons.NameOf(g.ActionId)) : string.Empty;
            return (g.AtSeconds, ActionIcons.OfAction(g.ActionId),
                    Loc.F("{0}  GCD clipped {1:0.00}s{2}",NumberFormat.Duration(TimeSpan.FromSeconds(g.AtSeconds)),locked,cause));
        }).ToList();
        DrawIconStrip("clipping", marks, [], pullSeconds);
        Theme.Hint(Loc.T("Each icon is the oGCD that landed when your GCD was held by an animation lock."));
        DrawClipTable(clips);
        if (folded)
            ImGui.TreePop();
    }

    /// <summary>
    /// The clips one per row: when, how long the GCD waited, the GCD before, the oGCDs woven after it and the GCD that came
    /// late, and the time between the two GCDs. Scrolls past a few rows.
    /// </summary>
    private static void DrawClipTable(IReadOnlyList<GcdGap> clips)
    {
        var icon = ImGui.GetTextLineHeight() * 1.5f;
        var rowHeight = icon + ImGui.GetStyle().CellPadding.Y * 2;
        const int visibleRows = 8;
        var height = clips.Count > visibleRows
                         ? ImGui.GetTextLineHeightWithSpacing() + visibleRows * rowHeight
                         : 0;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp
                    | (clips.Count > visibleRows ? ImGuiTableFlags.ScrollY : ImGuiTableFlags.None);
        using var table = Theme.Table("##cliptable", 4, flags, new Vector2(-1, height));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(55));
        ImGui.TableSetupColumn(Loc.T("GCD delay"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(80));
        ImGui.TableSetupColumn(Loc.T("Actions"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(Loc.T("Info"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        // An icon with its name on hover; a GCD framed in gold, an oGCD in red (it caused the clip).
        void ActionIcon(uint actionId, float size, Vector4 frame)
        {
            var pos = ImGui.GetCursorScreenPos();
            ActionIcons.Draw(ActionIcons.OfAction(actionId), size);
            ImGui.GetWindowDrawList().AddRect(pos, pos + new Vector2(size, size), Theme.U32(frame, 0.8f), Theme.Px(3), ImDrawFlags.None, Theme.Px(1.5f));
            if (ImGui.IsItemHovered())
                Theme.Tooltip(ActionIcons.NameOf(actionId));
        }

        void TextCell(string text, Vector4 color)
        {
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (icon - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(color, text);
        }

        foreach (var g in clips)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
            Theme.RowHover();
            TextCell(NumberFormat.Duration(TimeSpan.FromSeconds(g.AtSeconds)), Theme.TextFaint);
            TextCell($"{g.Seconds:0.00}s", g.Seconds >= 0.5 ? Theme.Bad : Theme.Warn);

            // GCD before -> oGCDs woven -> GCD that came late. Pulls recorded before this was kept: the oGCD only.
            ImGui.TableNextColumn();
            var weaves = g.Weaves ?? (g.ActionId != 0 ? [g.ActionId] : []);
            var first = true;
            void Next()
            {
                if (!first)
                    ImGui.SameLine(0, Theme.Px(3));
                first = false;
            }

            if (g.PrevGcdId != 0)
            {
                Next();
                ActionIcon(g.PrevGcdId, icon, Theme.Gold);
            }

            foreach (var w in weaves)
            {
                Next();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + icon * 0.15f);
                ActionIcon(w, icon * 0.75f, Theme.Bad);
            }

            if (g.NextGcdId != 0)
            {
                Next();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (icon - ImGui.GetTextLineHeight()) / 2);
                Theme.Icon(FontAwesomeIcon.AngleRight, Theme.TextFaint);
                Next();
                ActionIcon(g.NextGcdId, icon, Theme.Gold);
            }

            TextCell(g.BetweenSeconds > 0
                         ? Loc.F("{0:0.00}s between GCDs", g.BetweenSeconds)
                         : Loc.F("held {0:0.00}s by an animation lock", g.LockedSeconds > 0 ? g.LockedSeconds : g.Seconds),
                     Theme.TextMuted);
        }
    }

    /// <summary>
    /// The pull as a strip: optional ticks at the top (green good, red bad), then icons at their time, in lanes so
    /// none overlap, each red-framed; hovering one shows its tooltip.
    /// </summary>
    private static void DrawIconStrip(
        string id, IReadOnlyList<(double At, uint Icon, string Tooltip)> marks, IReadOnlyList<(double At, bool Good)> ticks,
        double pullSeconds)
    {
        var duration = Math.Max(1, Math.Max(pullSeconds, marks.Select(m => m.At).Concat(ticks.Select(t => t.At)).DefaultIfEmpty(0).Max()));
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var size = Theme.Px(24);
        var pad = size / 2 + Theme.Px(2);
        float X(double seconds) => origin.X + pad + (float)(seconds / duration) * (width - pad * 2);

        // Icons that would overlap go one lane lower.
        var lanes = new List<float>();
        var placed = new List<(int Index, float Left, int Lane)>();
        foreach (var (mark, index) in marks.Select((m, i) => (m, i)).OrderBy(p => p.m.At))
        {
            var left = X(mark.At) - size / 2;
            var lane = lanes.FindIndex(end => end <= left);
            if (lane < 0)
            {
                lanes.Add(0);
                lane = lanes.Count - 1;
            }

            lanes[lane] = left + size + Theme.Px(2);
            placed.Add((index, left, lane));
        }

        var tickHeight = ticks.Count > 0 ? Theme.Px(6) : 0;
        var tickGap = ticks.Count > 0 ? Theme.Px(4) : 0;
        var height = Theme.Px(3) + tickHeight + tickGap + Math.Max(1, lanes.Count) * (size + Theme.Px(3)) + Theme.Px(2);
        ImGui.InvisibleButton($"##{id}", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        dl.AddRectFilled(origin, origin + new Vector2(width, height), Theme.U32(Theme.Panel, 0.8f), Theme.Px(4));
        for (var minute = 60; minute < duration; minute += 60)
            dl.AddLine(new Vector2(X(minute), origin.Y + Theme.Px(2)), new Vector2(X(minute), origin.Y + height - Theme.Px(2)),
                       Theme.U32(Theme.Line, 0.6f), 1f);

        var tickTop = origin.Y + Theme.Px(3);
        foreach (var (at, good) in ticks)
        {
            dl.AddLine(new Vector2(X(at), tickTop), new Vector2(X(at), tickTop + tickHeight),
                       Theme.U32(good ? Theme.Good : Theme.Bad, good ? 0.5f : 1f), Theme.Px(1.5f));
        }

        var iconsTop = tickTop + tickHeight + tickGap;
        string? tooltip = null;
        var mouse = ImGui.GetMousePos();
        foreach (var (index, left, lane) in placed)
        {
            var pos = new Vector2(left, iconsTop + lane * (size + Theme.Px(3)));
            var icon = marks[index].Icon;
            if (icon != 0)
                ActionIcons.DrawAt(dl, icon, pos, size);
            else
                dl.AddRectFilled(pos, pos + new Vector2(size, size), Theme.U32(Theme.SurfaceActive), size * 0.15f);
            dl.AddRect(pos, pos + new Vector2(size, size), Theme.U32(Theme.Bad), size * 0.15f, ImDrawFlags.None, Theme.Px(1.5f));
            if (hovered && mouse.X >= pos.X && mouse.X <= pos.X + size && mouse.Y >= pos.Y && mouse.Y <= pos.Y + size)
                tooltip = marks[index].Tooltip;
        }

        ImGui.TextColored(Theme.TextFaint, "0:00");
        var end = NumberFormat.Duration(TimeSpan.FromSeconds(pullSeconds));
        ImGui.SameLine(width - ImGui.CalcTextSize(end).X);
        ImGui.TextColored(Theme.TextFaint, end);

        if (tooltip is not null)
            Theme.Tooltip(tooltip);
    }

    /// <summary>One line with the gauge lost to overcapping, per gauge, and the actions that overcapped.</summary>
    private static void DrawGaugeWaste(IReadOnlyList<GaugeWaste> waste)
    {
        ImGui.TextColored(Theme.TextMuted, Loc.T("Gauge overcap"));
        if (waste.Count == 0)
        {
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.Good, Loc.T("none"));
        }

        foreach (var w in waste)
        {
            ImGui.SameLine(0, Theme.Px(8));
            var iconHovered = ActionIcons.Draw(ActionIcons.OfGauge(w.Resource), ImGui.GetTextLineHeight());
            if (iconHovered && w.Actions.Length > 0)
                Theme.Tooltip(Loc.F("Overcapped by: {0}",w.Actions));
            ImGui.SameLine(0, Theme.Px(4));
            var amount = w.Max <= 5 ? $"{w.Wasted:0.#}" : $"{w.Wasted:0}";
            ImGui.TextColored(Theme.Bad, $"{w.Resource} {amount}");
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(Theme.TextFaint, w.Count > 1 ? $"({w.Count}×)" : "(1×)");
            if (ImGui.IsItemHovered() && w.Actions.Length > 0)
                Theme.Tooltip(Loc.F("Overcapped by: {0}",w.Actions));
        }

        Theme.HelpMarker(
            Loc.T("Gauge lost because it was too full when it was filled. Spend it before using the actions that fill it.\nThe gain of each action is learned from your own uses while the gauge had room, so the first uses of an action are not counted. Lilies, Addersgall and Polyglot: time spent full, in stacks lost.\nHover a gauge for the actions that overcapped it."));
    }

    /// <summary>Every death of the party in time order, with its cause; hover one for the hits taken just before.</summary>
    private static void DrawDeaths(IReadOnlyList<PartyDeath> deaths, IReadOnlyList<MitigationUse> mitigations, IReadOnlyList<DebuffSpan> debuffs)
    {
        if (!Theme.Section(Loc.T("Deaths"), FontAwesomeIcon.Skull, "deaths"))
            return;
        if (deaths.Count == 0)
            ImGui.TextColored(Theme.Good, Loc.T("No death."));
        else
            ImGui.TextColored(Theme.Bad, deaths.Count == 1 ? Loc.T("1 death") : Loc.F("{0} deaths",deaths.Count));
        Theme.HelpMarker(Loc.T("What killed each party member: the hit that took their last HP, or a guess when there was none (a debuff running out, a fall from the arena). Hover a death for the hits taken just before.\nParty members far from you may be missed."));
        if (deaths.Count == 0)
            return;

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | Theme.FitTable | ImGuiTableFlags.PadOuterX;
        using var table = Theme.Table("##deaths", 3, flags);
        if (!table)
            return;

        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00:00").X + Theme.Px(12));
        ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn(Loc.T("Killed by"), ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();

        for (var i = 0; i < deaths.Count; i++)
        {
            var d = deaths[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextFaint))
                ImGui.Selectable($"{NumberFormat.Duration(TimeSpan.FromSeconds(d.AtSeconds))}##death{i}", false,
                                 ImGuiSelectableFlags.SpanAllColumns);
            if (ImGui.IsItemHovered())
                DeathTooltip(d, mitigations, debuffs);

            ImGui.TableNextColumn();
            ImGui.TextColored(d.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(d.Player));
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextFaint, d.Job);
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.Bad, d.CauseText);
            if (DeathDetails(d) is { Length: > 0 } details)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextMuted, details);
            }
        }
    }

    private static string DeathDetails(PartyDeath d)
    {
        var details = new List<string>();
        if (d.Source.Length > 0)
            details.Add(d.Source);
        if (d.Damage > 0)
            details.Add(d.Damage.ToString("N0", CultureInfo.InvariantCulture));
        return details.Count > 0 ? $"({string.Join(", ", details)})" : string.Empty;
    }

    /// <summary>A death's recap: HP left, then the last hits taken, oldest first.</summary>
    private static void DeathTooltip(PartyDeath d, IReadOnlyList<MitigationUse> mitigations, IReadOnlyList<DebuffSpan> debuffs)
    {
        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(d.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(d.Player));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, Loc.F("died at {0}",NumberFormat.Duration(TimeSpan.FromSeconds(d.AtSeconds))));
        if (d.HpBefore > 0)
            ImGui.TextColored(Theme.TextMuted, Loc.F("HP before the last hit: {0}",d.HpBefore.ToString("N0", CultureInfo.InvariantCulture)));

        if (d.Recap.Count == 0)
            ImGui.TextColored(Theme.TextFaint, Loc.T("No hit recorded just before."));

        foreach (var h in d.Recap)
        {
            ImGui.TextColored(Theme.TextFaint, $"-{h.SecondsBefore:0.0}s");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, h.Damage.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.SameLine();
            ImGui.TextUnformatted(h.AbilityText);
            if (h.Source.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextMuted, $"({h.Source})");
            }
        }

        DrawDeathVulnerabilities(d, debuffs);
        DrawDeathMitigations(d, mitigations);
    }

    private static readonly Dictionary<uint, bool> VulnerabilityStatuses = new();

    // Vulnerability Up (physical, magic...): the same few names, with ids differing from one fight to another.
    private static bool IsVulnerability(uint statusId)
    {
        if (!VulnerabilityStatuses.TryGetValue(statusId, out var is_))
        {
            var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>(Dalamud.Game.ClientLanguage.English)
                             .TryGetRow(statusId, out var row) ? row.Name.ExtractText() : string.Empty;
            is_ = name.EndsWith("Vulnerability Up", StringComparison.OrdinalIgnoreCase);
            VulnerabilityStatuses[statusId] = is_;
        }

        return is_;
    }

    // The vulnerabilities on a player when they died (the debuffs go when they die: those up until then), with their
    // icon, stacks and what put them.
    private static void DrawDeathVulnerabilities(PartyDeath d, IReadOnlyList<DebuffSpan> debuffs)
    {
        var on = debuffs.Where(b => b.Player == d.Player && IsVulnerability(b.StatusId) && b.AppliedSeconds <= d.AtSeconds + 0.1
                                    && (b.EndSeconds is not { } end || end >= d.AtSeconds - 0.5))
                        .ToList();
        if (on.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextColored(Theme.Bad, Loc.T("Vulnerabilities on them:"));
        var size = ImGui.GetTextLineHeight();
        foreach (var b in on)
        {
            ImGui.Dummy(new Vector2(Theme.Px(6), size));
            ImGui.SameLine(0, 0);
            var icon = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(b.StatusId, out var row) ? row.Icon : 0u;
            ActionIcons.Draw(icon, size);
            ImGui.SameLine(0, Theme.Px(5));
            var stacks = b.Param > 1 && b.Param < 16 ? $" x{b.Param}" : string.Empty;
            ImGui.TextUnformatted(GameNames.Status(b.StatusId, b.Status) + stacks);
            ImGui.SameLine(0, Theme.Px(5));
            var from = b.Source.Length > 0 ? $"{b.Source}, " : string.Empty;
            ImGui.TextColored(Theme.TextMuted, $"({from}{NumberFormat.Duration(TimeSpan.FromSeconds(b.AppliedSeconds))})");
        }
    }

    // The mitigations up when a player died: on the party (or the enemy), then on the player alone.
    private static void DrawDeathMitigations(PartyDeath d, IReadOnlyList<MitigationUse> mitigations)
    {
        if (mitigations.Count == 0)
            return;

        var up = MitigationDurations.ActiveAt(mitigations, d.AtSeconds, u => EnglishName(u.ActionId));
        var onParty = up.Where(u => MitigationDurations.ProtectedOne(u, EnglishName(u.ActionId)) is null).ToList();
        var onPlayer = up.Where(u => MitigationDurations.ProtectedOne(u, EnglishName(u.ActionId)) == d.Player).ToList();

        ImGui.Separator();
        ImGui.TextColored(Theme.TextMuted, onParty.Count > 0 ? Loc.T("Party mitigation up:") : Loc.T("No party mitigation up."));
        foreach (var u in onParty)
            DrawMitigationLine(u);
        ImGui.TextColored(Theme.TextMuted, onPlayer.Count > 0 ? Loc.F("On {0}:", Names.Show(d.Player)) : Loc.F("Nothing on {0}.", Names.Show(d.Player)));
        foreach (var u in onPlayer)
            DrawMitigationLine(u);
    }

    private static void DrawMistakes(IReadOnlyList<MechanicMistake> mistakes, bool timeline)
    {
        if (!Theme.Section(Loc.T("Failed mechanics"), FontAwesomeIcon.ExclamationTriangle, "mistakes"))
            return;
        if (mistakes.Count == 0)
            ImGui.TextColored(Theme.Good, Loc.T("No failed mechanic."));
        else
            ImGui.TextColored(Theme.Bad, mistakes.Count == 1 ? Loc.T("1 failed mechanic") : Loc.F("{0} failed mechanics",mistakes.Count));
        Theme.HelpMarker(Loc.T("Penalty debuffs put on party members by enemy actions: Vulnerability Up, Damage Down, Down for the Count. Tankbuster vulnerabilities on tanks can be ignored in Settings > Pulls history."));
        if (mistakes.Count == 0)
            return;

        var players = mistakes.GroupBy(m => m.Player)
                              .Select(g => (Player: g.Key, g.First().Job, g.First().IsYou, Count: g.Count(), List: g.ToList(),
                                            Debuffs: string.Join(", ", g.GroupBy(m => m.StatusText)
                                                                        .OrderByDescending(s => s.Count())
                                                                        .Select(s => s.Count() > 1 ? $"{s.Key} ×{s.Count()}" : s.Key))))
                              .OrderByDescending(p => p.Count)
                              .ToList();

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | Theme.FitTable | ImGuiTableFlags.PadOuterX;
        using (var table = Theme.Table("##mistakes", 3, flags))
        {
            if (table)
            {
                ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn(Loc.T("Count"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Count")).X + Theme.Px(8));
                ImGui.TableSetupColumn(Loc.T("Debuffs"), ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableHeadersRow();

                foreach (var p in players)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    ImGui.TableNextColumn();
                    // The whole row is hoverable, for the detail of each failed mechanic.
                    var start = ImGui.GetCursorPos();
                    ImGui.Selectable($"##mistakerow{p.Player}", false,
                                     ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                                     new Vector2(0, ImGui.GetTextLineHeight()));
                    if (ImGui.IsItemHovered())
                        Theme.Tooltip(string.Join("\n", p.List.Select(MistakeText)));
                    ImGui.SetCursorPos(start);
                    ImGui.TextColored(p.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(p.Player));
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.TextFaint, p.Job);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, p.Count.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, p.Debuffs);
                }
            }
        }

        if (!timeline)
            return;

        using var node = ImRaii.TreeNode(Loc.F("Timeline ({0})###mistaketimeline",mistakes.Count));
        if (!node)
            return;

        foreach (var m in mistakes)
        {
            ImGui.TextColored(Theme.TextFaint, NumberFormat.Duration(TimeSpan.FromSeconds(m.AtSeconds)));
            ImGui.SameLine();
            ImGui.TextColored(m.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(m.Player));
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, m.StatusText);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextMuted, m.Source.Length > 0 ? Loc.F("from {0} ({1})",m.AbilityText,m.Source) : Loc.F("from {0}",m.AbilityText));
        }
    }

    /// <summary>
    /// One line splitting the lost GCD time by cause, with a help tooltip; in the history, the longest gaps too.
    /// </summary>
    private static void DrawGcdAnalysis(GcdAnalysis gcd, bool showGaps, double? uptime = null, double lostSeconds = 0)
    {
        if (uptime is { } share)
        {
            // Live: a ring filling to the uptime.
            var ring = ImGui.GetTextLineHeight();
            var ringPos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(ring, ring));
            Ring.Draw(ImGui.GetWindowDrawList(), ringPos + new Vector2(ring / 2), ring / 2 - Theme.Px(1), (float)share, Theme.Crystal, ImGui.GetID("gcdring"));
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.TextMuted, Loc.T("GCD uptime"));
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(Theme.Crystal, $"{share:0.0%}");
            if (ImGui.IsItemHovered())
                Theme.Tooltip(Loc.F("{0} GCDs used\n{1:0.0}s lost with the boss targetable and your GCD ready",gcd.GcdCount,lostSeconds));
            return; // Live: the overall value only, the breakdown is in the history
        }

        if (gcd.GcdCount == 0)
            return;

        void Part(string label, double seconds, string? extra = null, bool first = false)
        {
            if (!first)
            {
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(Theme.TextFaint, "·");
                ImGui.SameLine(0, Theme.Px(4));
            }

            ImGui.TextColored(Theme.TextMuted, label);
            ImGui.SameLine(0, Theme.Px(4));
            ImGui.TextColored(seconds >= 0.05 ? Theme.Text : Theme.TextFaint, $"{seconds:0.0}s{extra}");
        }

        Part(Loc.T("Clipping"), gcd.ClipSeconds, gcd.ClipCount > 0 ? $" ({gcd.ClipCount}×)" : null, first: true);
        // Late presses and whole GCDs missed are one cause for players: time free to act without a GCD rolling.
        Part(Loc.T("No GCD"), gcd.LateSeconds + gcd.NoGcdSeconds);
        Part(Loc.T("Dead"), gcd.DeadSeconds);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(gcd.LostGcds >= 1 ? Theme.Warn : Theme.Good, Loc.F("~{0:0.0} GCD lost",gcd.LostGcds));
        Theme.HelpMarker(
            Loc.F("{0} GCDs used, {1:0.00}s each on average. Time with the boss targetable and your GCD ready but unused:\n",gcd.GcdCount,gcd.AverageRecast)
            + Loc.T("- Clipping: held by an oGCD's animation lock. Weave fewer oGCDs per GCD, or earlier.\n")
            + Loc.T("- No GCD: free to act, but no GCD rolling: a GCD pressed late (reaction, ping, a short step) or a whole GCD or more without one (movement, mechanics). Press the next GCD as soon as it is ready, slidecast, use instant GCDs while moving.\n")
            + Loc.T("- Dead: time spent dead."));

        if (!showGaps || gcd.Gaps.Count == 0)
            return;

        var longest = gcd.Gaps.OrderByDescending(g => g.Seconds).Take(5).OrderBy(g => g.AtSeconds);
        Theme.Hint(Loc.T("Longest gaps: ") + string.Join("   ", longest.Select(g =>
            $"{NumberFormat.Duration(TimeSpan.FromSeconds(g.AtSeconds))} {DpsGraph.GapLabel(g.Kind)} {g.Seconds:0.0}s")));
    }

    // ---------------------------------------------------------------- Shared

    /// <summary>
    /// Player table, sized to its rows: job icon, name over a damage bar in the role's color, then the figures.
    /// </summary>
    // Live: the snapshot shown, for the bars' animations (none in the history).
    private void DrawCombatantTable(IEnumerable<PullCombatant> combatants, CombatSnapshot? live = null)
    {
        var rows = combatants.ToList();
        var showRdps = rows.Any(c => c.Rdps is not null);

        // Sorting only matters when there is an rDPS column to sort by.
        var config = plugin.Configuration;
        if (showRdps)
        {
            ImGui.TextColored(Theme.TextMuted, Loc.T("Sort by"));
            ImGui.SameLine();
            if (Theme.Chip(Loc.T("DPS"), !config.SortByRdps) && config.SortByRdps)
            {
                config.SortByRdps = false;
                config.Save();
            }

            ImGui.SameLine(0, Theme.Px(4));
            if (Theme.Chip(Loc.T("rDPS"), config.SortByRdps) && !config.SortByRdps)
            {
                config.SortByRdps = true;
                config.Save();
            }
        }

        var byRdps = showRdps && config.SortByRdps;
        rows = byRdps
                   ? rows.OrderByDescending(c => c.Rdps ?? c.Dps).ToList()
                   : rows.OrderByDescending(c => c.Dps).ToList();
        var top = rows.Count > 0 ? rows.Max(c => byRdps ? c.Rdps ?? c.Dps : c.Dps) : 0;

        // Share of the party's DPS and of its rDPS (the Limit Break counts in the DPS, like the usual meters' damage %).
        var dpsTotal = rows.Sum(c => c.Dps);
        var rdpsTotal = rows.Sum(c => c.Rdps ?? 0);
        static string Share(double value, double total) => total > 0 ? $"{value / total:0.0%}" : "-";

        // Without scrolling and with a height of 0, the table takes exactly the height of its rows. In a narrow window it
        // keeps a readable width and scrolls sideways instead, which needs an explicit height.
        var iconSize = ImGui.GetTextLineHeight() + Theme.Px(4);
        var minWidth = Theme.Px(showRdps ? 780 : 660);
        var narrow = ImGui.GetContentRegionAvail().X < minWidth;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        var height = 0f;
        if (narrow)
        {
            flags |= ImGuiTableFlags.ScrollX;
            var cell = ImGui.GetStyle().CellPadding.Y * 2;
            height = ImGui.GetTextLineHeight() + cell + rows.Count * (iconSize + Theme.Px(4) + cell)
                     + ImGui.GetStyle().ScrollbarSize + Theme.Px(2);
        }

        using var table = Theme.Table("##combatants", showRdps ? 10 : 8, flags, new Vector2(-1, height), narrow ? minWidth : 0);
        if (!table)
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Theme.Px(22));
        ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthStretch, 3.4f);
        ImGui.TableSetupColumn(Loc.T("DPS"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.T("DPS %"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        if (showRdps)
        {
            ImGui.TableSetupColumn(Loc.T("rDPS"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn(Loc.T("rDPS %"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        }

        ImGui.TableSetupColumn(Loc.T("Crit"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn(Loc.T("DH"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn(Loc.T("Crit DH"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(Loc.T("Deaths"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Deaths")).X + Theme.Px(8));
        ImGui.TableHeadersRow();

        var rank = 0;
        foreach (var c in rows)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, iconSize + Theme.Px(4));
            Theme.RowHover();
            var hasJob = c.Job.Length > 0;
            if (hasJob)
                rank++;

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.TextFaint, hasJob ? rank.ToString() : "");

            // Damage bar behind the name, as wide as the share of the top player's value.
            ImGui.TableNextColumn();
            var value = byRdps ? c.Rdps ?? c.Dps : c.Dps;
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var (dead, sinceRaise) = live is not null ? liveBars.Life(live.StartUtc, c.Name, plugin.Tracker.IsDead(c.Name)) : (0f, 100f);
            if (top > 0)
            {
                var barColor = BarAnimator.LifeColor(JobInfo.RoleColor(c.Job), dead, sinceRaise);
                var share = (float)Math.Clamp(value / top, 0, 1);
                var flash = 0f;
                if (live is not null)
                    (share, flash) = liveBars.Bar(live.StartUtc, c.Name, share, hasJob ? rank : 0, start, width, iconSize, c.IsYou);
                var end = start + new Vector2(width * share, iconSize);
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(start, end, Theme.U32(barColor, c.IsYou ? 0.34f : 0.2f), Theme.Px(3));
                if (live is not null)
                {
                    BarAnimator.Overlay(dl, start, end, flash, c.IsYou && dead <= 0);
                    BarAnimator.Broken(dl, start, end, dead);
                }
            }

            // Live: a player who just died shakes, a red ring out of the icon.
            var sinceDeath = live is not null ? liveBars.SinceDeath(live.StartUtc, c.Name, c.Deaths) : 100f;
            var shake = BarAnimator.Shake(sinceDeath);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + shake);
            JobInfo.DrawIcon(c.Job, iconSize);
            if (live is not null)
                BarAnimator.DeathOverlay(ImGui.GetWindowDrawList(), start + new Vector2(shake + iconSize / 2, iconSize / 2), start,
                                         start + new Vector2(width, iconSize), sinceDeath);
            BarAnimator.Raise(ImGui.GetWindowDrawList(), start, start + new Vector2(width, iconSize), sinceRaise);
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(dead > 0 ? Theme.TextFaint : c.IsYou ? Theme.GoldBright : Theme.Text, Names.Show(c.Name));

            void Cell(string text, Vector4? color = null)
            {
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextColored(color ?? (c.IsYou ? Theme.GoldBright : Theme.Text), text);
            }

            Cell(NumberFormat.Format(c.Dps, NumberStyle.Grouped));
            Cell(Share(c.Dps, dpsTotal), Theme.TextMuted);
            if (showRdps)
            {
                Cell(c.Rdps is { } r ? NumberFormat.Format(r, NumberStyle.Grouped) : "-");
                if (c.Breakdown is { } b && ImGui.IsItemHovered())
                {
                    Theme.Tooltip(
                        Loc.F("Given to others' buffs: -{0} DPS\n",NumberFormat.Format(b.GivenDps, NumberStyle.Grouped))
                        + Loc.F("Received from own buffs: +{0} DPS\n",NumberFormat.Format(b.ReceivedDps, NumberStyle.Grouped))
                        + Loc.F("Crit rate used: {0:0.0%}   DH rate used: {1:0.0%}\n",b.CritRate,b.DirectHitRate)
                        + Loc.F("({0} hits observed; exact stats are used for you at level 100)",b.Samples));
                }

                Cell(c.Rdps is { } own ? Share(own, rdpsTotal) : "-", Theme.TextMuted);
            }

            Cell($"{c.CritPercent:0}%", Theme.TextMuted);
            Cell($"{c.DirectHitPercent:0}%", Theme.TextMuted);
            // Hits that were both a crit and a direct hit (pulls recorded before it was kept have none).
            Cell(c.CritDirectHitPercent is { } cdh ? $"{cdh:0}%" : "-", Theme.TextMuted);
            Cell(c.Deaths.ToString(), c.Deaths > 0 ? Theme.Bad : Theme.TextFaint);
        }
    }
}
