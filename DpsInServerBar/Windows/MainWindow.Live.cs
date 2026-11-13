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

/// <summary>The Live tab: the fight in progress.</summary>
public sealed partial class MainWindow
{
    private CombatSnapshot? liveRowsSnapshot;
    private List<PullCombatant> liveRows = [];

    // The pull in progress resampled for the graph: only when it got a new point.
    private (PullInProgress? Pull, int Revision, bool Party) liveCurveKey;
    private double[] liveCurve = [];

    private void DrawLive()
    {
        var config = plugin.Configuration;
        var snapshot = plugin.Tracker.Latest;

        var enc = snapshot?.Encounter;
        if (snapshot is not null && enc is not null)
        {
            ImGui.TextColored(Theme.GoldBright, string.IsNullOrEmpty(enc.Title) ? Loc.T("Encounter") : enc.Title);
            ImGui.SameLine();
            Theme.Badge(NumberFormat.Duration(enc.Duration), Theme.Crystal);
            if (!snapshot.IsActive)
            {
                ImGui.SameLine();
                Theme.Badge(Loc.T("ended"), Theme.TextMuted);
            }

            ImGui.SameLine();
        }

        DrawLiveButtons();

        if (snapshot is null || enc is null)
        {
            Theme.Hint(Loc.T("No combat data yet. Start a fight."));
            return;
        }

        var partyDps = snapshot.PartyDps(config.IncludeLimitBreakInParty);
        var threshold = config.FindThreshold(plugin.CurrentTerritory, enc.Title, plugin.Boss.Current?.BaseId ?? 0);
        DrawLiveCards(snapshot, partyDps, threshold);

        if (plugin.Ultimates.Current is { } phase)
            DrawPhase(phase);

        DrawPersonal(snapshot);

        Theme.SectionHeader(Loc.T("Party"), FontAwesomeIcon.Users);
        // The rows only change with the snapshot (about once per second).
        if (!ReferenceEquals(snapshot, liveRowsSnapshot))
        {
            liveRowsSnapshot = snapshot;
            liveRows = snapshot.Combatants.Select(c => new PullCombatant
            {
                Name = c.Name, Job = c.Job, Dps = c.EncDps, Rdps = c.HasRdps ? c.RDps : null, Breakdown = c.RdpsBreakdown, DamagePercent = c.DamagePercent,
                CritPercent = c.CritPercent, DirectHitPercent = c.DirectHitPercent, CritDirectHitPercent = c.CritDirectHitPercent, Deaths = c.Deaths, IsYou = c.IsYou,
            }).ToList();
        }

        DrawCombatantTable(liveRows);

        var liveDeaths = plugin.Tracker.PartyDeaths.Since(snapshot.StartUtc);
        if (liveDeaths.Count > 0)
            DrawDeaths(liveDeaths);

        if (plugin.Tracker.Mistakes.Current is { Count: > 0 } mistakes)
            DrawMistakes(mistakes, timeline: false);

        if (config.ShowGraph)
        {
            Theme.SectionHeader(Loc.T("Graph"), FontAwesomeIcon.ChartLine);
            DrawLiveGraph(threshold);
        }
    }

    /// <summary>Compact view and Opener practice, at the right of the line.</summary>
    private void DrawLiveButtons()
    {
        static float ChipWidth(string label) => ImGui.CalcTextSize(label).X + Theme.Px(20);
        var width = ChipWidth(Loc.T("Compact view")) + ImGui.GetStyle().ItemSpacing.X + ChipWidth(Loc.T("Opener practice"));
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine(); // no room right of the title: on their own line
        var x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (x > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(x);

        if (Theme.Chip(Loc.T("Compact view"), plugin.CompactOpen))
            plugin.SetCompact(!plugin.CompactOpen);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Only the party's DPS meter, in a small pop-up window."));

        ImGui.SameLine();
        if (Theme.Chip(Loc.T("Opener practice"), plugin.OpenerOpen))
            plugin.ToggleOpener();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Practice your job's opener: each action used in the right order is checked off."));
    }

    /// <summary>The key figures of the fight in progress, one card each.</summary>
    private void DrawLiveCards(CombatSnapshot snapshot, double partyDps, PartyDpsThreshold? threshold)
    {
        var cards = new List<Card>();
        var you = snapshot.You;

        if (you is not null)
        {
            var rank = snapshot.YourRank();
            var party = snapshot.Combatants.Count(c => c.HasJob);
            if (Shown(MainCard.Dps))
                cards.Add(new Card(Loc.T("DPS"), NumberFormat.Format(you.EncDps, NumberStyle.Grouped), Theme.Gold,
                                          party > 1 && rank > 0 ? Loc.F("#{0} of {1}",rank,party) : null));
            if (you.HasRdps && Shown(MainCard.Rdps))
                cards.Add(new Card(Loc.T("rDPS"), NumberFormat.Format(you.RDps, NumberStyle.Grouped), Theme.Crystal,
                                              party > 1 ? Loc.F("#{0} of {1}",snapshot.YourRank(byRdps: true),party) : null));
        }

        if (Shown(MainCard.Gcd) && plugin.Uptime.Snapshot() is { AvailableSeconds: >= 5 } up)
            cards.Add(new Card(Loc.T("GCD uptime"), $"{up.Uptime:0.0%}", Theme.Crystal, Loc.F("{0:0.0}s lost",up.LostSeconds)));

        var elapsed = snapshot.Encounter.Duration.TotalSeconds;
        var boss = plugin.Ultimates.Current is null ? plugin.Boss.Current : null;
        var enrage = threshold?.EnrageSeconds ?? 0;

        if (Shown(MainCard.PartyDps))
            cards.Add(PartyDpsCard(snapshot, boss, partyDps, elapsed, enrage, threshold));

        if (boss is not null && Shown(MainCard.BossHp))
        {
            string? hint = null;
            Vector4? hintColor = null;
            if (snapshot.IsActive && BossTracker.ProjectedKill(boss, snapshot.Encounter.Duration, partyDps) is { } eta)
            {
                if (enrage > 0)
                {
                    // Time to spare (or missing) between the projected kill and the enrage.
                    var margin = enrage - eta.TotalSeconds;
                    var span = NumberFormat.Duration(TimeSpan.FromSeconds(Math.Abs(margin)));
                    hint = margin >= 0 ? Loc.F("kill {0} before enrage",span) : Loc.F("kill {0} after enrage",span);
                    hintColor = margin >= 0 ? Theme.Good : Theme.Bad;
                }
                else
                {
                    hint = Loc.F("kill in {0}",NumberFormat.Duration(eta));
                }
            }

            cards.Add(new Card(boss.Name, $"{boss.HpFraction:0.0%}", Theme.Bad, hint, hintColor));
        }

        if (Shown(MainCard.Pace) && plugin.Pulls.PaceVsBest() is { } pace)
        {
            var color = pace >= 0 ? Theme.Good : Theme.Bad;
            cards.Add(new Card(Loc.T("Pace vs best"), $"{pace:+0.0%;-0.0%}", color, Loc.T("damage so far"), ValueColor: color));
        }

        DrawCards(cards, uniform: true);
    }

    /// <summary>True when this figure card is ticked in the settings (Display > Main window cards).</summary>
    private bool Shown(MainCard card) => plugin.Configuration.MainCards.HasFlag(card);

    /// <summary>Party DPS card: against the enrage when known, else against the fight's minimum DPS, else the value alone.</summary>
    private static Card PartyDpsCard(
        CombatSnapshot snapshot, BossInfo? boss, double partyDps, double elapsed, double enrage, PartyDpsThreshold? threshold)
    {
        var value = NumberFormat.Format(partyDps, NumberStyle.Grouped);
        if (enrage > 0 && PartyGapToEnrage(snapshot, boss, partyDps, elapsed, threshold!) is { } gap)
        {
            var color = gap >= 0 ? Theme.Good : Theme.Bad;
            return new Card(Loc.T("Party DPS"), value, color, Loc.F("{0:+0.0%;-0.0%} vs enrage",gap), color);
        }

        if (threshold is { EffectiveMinPartyDps: > 0 } check)
        {
            var color = partyDps >= check.EffectiveMinPartyDps ? Theme.Good : Theme.Bad;
            return new Card(Loc.T("Party DPS"), value, color,
                            Loc.F("need {0} ",NumberFormat.Format(check.EffectiveMinPartyDps, NumberStyle.Grouped))
                            + $"({partyDps / check.EffectiveMinPartyDps - 1:+0.0%;-0.0%})", color);
        }

        return new Card(Loc.T("Party DPS"), value, Theme.Gold);
    }

    /// <summary>
    /// How much more (or less) DPS the party does than the enrage asks: during the fight, against what is left of the
    /// boss in the time left; otherwise against the boss HP over the enrage time.
    /// </summary>
    private static double? PartyGapToEnrage(CombatSnapshot snapshot, BossInfo? boss, double partyDps, double elapsed, PartyDpsThreshold check)
    {
        if (partyDps <= 0)
            return null;
        if (snapshot.IsActive && boss is { CurrentHp: > 0 })
        {
            var left = check.EnrageSeconds - elapsed;
            return left <= 0 ? -1 : partyDps * left / boss.CurrentHp - 1;
        }

        return check.EffectiveMinPartyDps > 0 ? partyDps / check.EffectiveMinPartyDps - 1 : null;
    }

    /// <summary>Your own play in the fight in progress: GCD, gauge, positionals, combos, DoTs, overheal, tank swaps.</summary>
    private void DrawPersonal(CombatSnapshot snapshot)
    {
        var up = plugin.Uptime.Snapshot() is { AvailableSeconds: >= 5 } u ? u : null;
        var job = snapshot.You?.Job ?? string.Empty;
        var role = snapshot.You is null ? (JobRole?)null : JobRoles.Of(job);
        var gauge = up is not null && snapshot.You is not null && GaugeTracker.Tracks(job);
        var positionals = plugin.Positionals.Current;
        var combos = plugin.Combos.Current;
        var showCombos = combos.Count > 0 || plugin.Combos.TracksCurrentJob;
        var dots = plugin.Dots.Current is { Count: > 0 } d && d[0].AvailableSeconds >= 5 ? d : null;
        var heals = role == JobRole.Healer ? plugin.Tracker.Heals.Current : null;
        var swaps = role == JobRole.Tank ? plugin.Tracker.TankSwaps.Current : null;

        if (up is null && !gauge && positionals.Count == 0 && !showCombos && dots is null
            && heals is not { Count: > 0 } && swaps is not { Count: > 0 })
            return;

        Theme.SectionHeader(Loc.T("Personal"), FontAwesomeIcon.User);
        if (up is not null)
            DrawGcdAnalysis(up.Gcd, showGaps: false, uptime: up.Uptime, lostSeconds: up.LostSeconds);
        if (gauge)
            DrawGaugeWaste(plugin.Gauges.Current);
        DrawPositionals(positionals, names: false);
        DrawComboMistakes(combos, names: false, showNone: showCombos);
        if (dots is not null)
            DrawDotUptime(dots);
        if (heals is not null)
            DrawOverheal(heals, foldable: true);
        if (swaps is not null)
            DrawTankSwaps(swaps, showEmpty: true);
    }

    /// <summary>A figure card: see <see cref="Theme.StatCard"/>.</summary>
    private sealed record Card(string Caption, string Value, Vector4 Accent, string? Hint = null, Vector4? HintColor = null, Vector4? ValueColor = null);

    // Width of a figure card; narrower only when the window cannot fit them all.
    private const float CardWidth = 190;

    /// <summary>
    /// Lays the cards out on one line, at a fixed width. Uniform: all as tall (room for a hint) and as wide (the
    /// widest one's need) as each other.
    /// </summary>
    private static void DrawCards(List<Card> cards, bool uniform = false)
    {
        if (cards.Count == 0)
            return; // every card turned off in the settings

        var width = Math.Min(Theme.Px(CardWidth), Theme.CardWidth(cards.Count));
        if (uniform)
            width = cards.Select(c => Theme.StatCardWidth(c.Caption, c.Value, c.Hint)).Append(width).Max();

        // Too wide for one line (a narrow window): as many per line as fit, the rest below.
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var perLine = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (width + spacing)));
        if (perLine < cards.Count)
            width = Math.Max(width, Theme.CardWidth(perLine)); // the lines filled edge to edge
        for (var i = 0; i < cards.Count; i++)
        {
            if (i % perLine != 0)
                ImGui.SameLine();
            var c = cards[i];
            Theme.StatCard(c.Caption, c.Value, c.Accent, width, c.Hint, c.HintColor, c.ValueColor, fullHeight: uniform);
        }
    }

    /// <summary>Ultimate phase: phase timer, boss HP, DPS on the boss vs the DPS needed, projected kill.</summary>
    private static void DrawPhase(PhaseView phase)
    {
        Theme.SectionHeader($"{phase.Fight.Key} {phase.Label}", FontAwesomeIcon.LayerGroup);
        if (!phase.Supported)
        {
            Theme.Hint(Loc.T("Phase not supported yet."));
            return;
        }

        if (phase.IsTransition)
        {
            Theme.Hint(Loc.F("Transition, {0}.",NumberFormat.Duration(phase.Elapsed)));
            return;
        }

        if (!phase.Started)
        {
            Theme.Hint(Loc.F("{0}: waiting for the boss.",phase.Phase!.Name));
            return;
        }

        var cards = new List<Card>
        {
            new Card(phase.Phase!.Name, $"{phase.HpFraction:0.0%}", Theme.Bad,
                                Loc.F("phase time {0}",NumberFormat.Duration(phase.Elapsed))),
        };

        if (phase.RequiredDps is { } required)
        {
            var color = phase.BossDps >= required ? Theme.Good : Theme.Bad;
            cards.Add(new Card(Loc.T("DPS on boss"), NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), color,
                                          Loc.F("need {0} ",NumberFormat.Format(required, NumberStyle.Grouped))
                                          + $"({phase.BossDps / required - 1:+0.0%;-0.0%})", color));
        }
        else
        {
            cards.Add(new Card(Loc.T("DPS on boss"), NumberFormat.Format(phase.BossDps, NumberStyle.Grouped), Theme.Gold,
                                          Loc.T("no enrage set")));
        }

        if (phase.ProjectedKill is { } eta)
        {
            Vector4? color = phase.EnrageSeconds is { } enrage ? eta.TotalSeconds <= enrage ? Theme.Good : Theme.Bad : null;
            cards.Add(new Card(Loc.T("Projected kill"), NumberFormat.Duration(eta), color ?? Theme.Crystal,
                                          phase.EnrageSeconds is { } e ? Loc.F("enrage {0}",NumberFormat.Duration(TimeSpan.FromSeconds(e))) : null,
                                          color));
        }

        DrawCards(cards, uniform: true);
    }

    private void DrawLiveGraph(PartyDpsThreshold? threshold)
    {
        var window = plugin.Configuration.GraphWindowSeconds;
        graph.DrawOptions(window);

        if (graph.ShowUptime)
        {
            // Uptime of the fight in progress (or the last one).
            if (plugin.Uptime.Snapshot() is { } up)
            {
                DpsGraph.DrawUptime("##liveuptime", [(Loc.T("You"), up.AvailableBySecond, up.ActiveBySecond)], DpsGraph.Height, up.Gcd.Gaps,
                                    plugin.Tracker.Latest is { } latest ? plugin.Tracker.PartyDeaths.Since(latest.StartUtc) : null);
            }
            else
                Theme.Hint(Loc.T("No fight yet."));
            return;
        }

        var pull = plugin.Pulls.Current;
        if (pull is null)
        {
            Theme.Hint(Loc.T("The graph shows the pull in progress; finished pulls are in the History tab."));
            return;
        }

        var points = graph.ShowParty ? pull.PartyPoints : pull.YourPoints;
        var curveKey = (pull, pull.Revision, graph.ShowParty);
        if (curveKey != liveCurveKey)
        {
            liveCurveKey = curveKey;
            liveCurve = Timeline.Resample(points, pull.LastSeconds);
        }

        var cumulative = liveCurve;

        var series = new List<DpsGraph.Series>();
        graph.AddCurves(series, graph.ShowParty ? Loc.T("Party") : Loc.T("You"), cumulative, window);

        var best = plugin.History.BestFor(pull.Title);
        if (best is not null && !graph.ShowParty)
            series.Add(new DpsGraph.Series(Loc.T("Best pull average"), Timeline.Average(best.YourDamageTimeline)));

        // One id per pull: a zoom does not carry over to the next pull.
        DpsGraph.Draw($"##livegraph{pull.StartUtc.Ticks}", series, DpsGraph.Height,
                      graph.ShowParty ? threshold?.EffectiveMinPartyDps : null, plugin.Tracker.PartyDeaths.Since(pull.StartUtc));
    }
}
