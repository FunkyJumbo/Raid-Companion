using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using DpsInServerBar.Display;
using DpsInServerBar.Native;
using DpsInServerBar.Ultimates;

namespace DpsInServerBar.Tracking;

/// <summary>
/// What the plugin prints in chat: the recap of each pull and of each ultimate phase, the party deaths as they
/// happen, and the preview of the settings. Game thread only.
/// </summary>
public sealed class ChatSummary : IDisposable
{
    private static readonly Vector4 Good = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Muted = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly Vector4 Link = new(0.4f, 0.75f, 1f, 1f);

    // Chat link ids: one per printed summary, each opening its own pull.
    private uint nextLinkId = 1;

    // The links of the last summaries stay clickable; older ones are dropped.
    private const int MaxLinks = 50;
    private readonly Queue<uint> linkIds = new();

    private readonly Plugin plugin;
    private readonly IChatGui chat;

    public ChatSummary(Plugin plugin, IChatGui chat)
    {
        this.plugin = plugin;
        this.chat = chat;
    }

    public void Dispose()
    {
        foreach (var id in linkIds)
            chat.RemoveChatLinkHandler(id);
        linkIds.Clear();
    }

    /// <summary>One party member's death, as it happens.</summary>
    public void PrintDeath(string player, string job, bool isYou, DeathInfo death, TimeSpan? at)
    {
        var sb = new Lumina.Text.SeStringBuilder();
        sb.PushColorRgba(Muted).Append(Loc.T("[Death] "));
        if (at is { } time && time >= TimeSpan.Zero)
            sb.Append(NumberFormat.Duration(time)).Append(" ");
        sb.PopColor();
        sb.Append(player);
        if (job.Length > 0)
            sb.PushColorRgba(Muted).Append($" ({job})").PopColor();
        sb.Append(Loc.T(": ")).PushColorRgba(Bad).Append(Describe(death)).PopColor();
        if (plugin.Configuration.ChatDeathsOverkill && death.Overkill > 0)
            sb.PushColorRgba(Muted)
              .Append(Loc.F(" overkill {0}", death.Overkill.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)))
              .PopColor();
        chat.Print(sb.ToReadOnlySeString().ToDalamudString());
    }

    /// <summary>Prints made-up summaries with the values ticked in the settings, to check what they look like.</summary>
    public void PrintPreview()
    {
        var config = plugin.Configuration;
        var job = plugin.Tracker.Latest?.You is { HasJob: true } you ? you.Job : "DRK";
        PrintSummary(new PullRecord
        {
            StartUtc = DateTime.UtcNow,
            Title = Loc.T("Preview Boss"),
            DurationSeconds = 272,
            Outcome = PullOutcome.Kill,
            Job = job,
            YourDps = 21_234,
            YourRdps = 20_511,
            Uptime = 0.987,
            PartyDps = 182_345,
            Rank = 2,
            RdpsRank = 3,
            PartySize = 8,
            Deaths = 1,
            KilledBy = Loc.T("Auto-attack (Preview Boss, 45,210)"),
            Mistakes =
            [
                new MechanicMistake(64, Loc.T("You"), job, true, Loc.T("Vulnerability Up"), Loc.T("Preview Cleave"), Loc.T("Preview Boss")),
                new MechanicMistake(131, Loc.T("You"), job, true, Loc.T("Vulnerability Up"), Loc.T("Preview Cleave"), Loc.T("Preview Boss")),
                new MechanicMistake(131, Loc.T("Party member"), Loc.T("WHM"), false, Loc.T("Damage Down"), Loc.T("Preview Tower"), Loc.T("Preview Boss")),
            ],
        }, preview: true);

        // The ultimate phase summaries too, when they are on: a cleared phase and a wipe.
        if (config.UltimateMode && config.ChatSummaryPhases && UltimateDefinitions.All.FirstOrDefault() is { Phases.Count: > 0 } fight)
        {
            var phases = fight.Phases.Where(p => !p.IsTransition).ToList();
            PrintPhaseSummary(new PhaseResult(fight, phases[0], 1, TimeSpan.FromSeconds(118), 8_000_000, 0, 125, Cleared: true), preview: true);
            if (phases.Count > 1)
                PrintPhaseSummary(new PhaseResult(fight, phases[1], 3, TimeSpan.FromSeconds(95), 20_000_000, 3_100_000, 180, Cleared: false), preview: true);
        }
    }

    private DalamudLinkPayload PullLink(Guid pullId)
    {
        var id = nextLinkId++;
        linkIds.Enqueue(id);
        if (linkIds.Count > MaxLinks)
            chat.RemoveChatLinkHandler(linkIds.Dequeue());
        return chat.AddChatLinkHandler(id, (_, _) => plugin.ShowPull(pullId));
    }

    /// <summary>The recap of a finished pull, with the values ticked in the settings.</summary>
    public void PrintSummary(PullRecord pull, bool preview = false)
    {
        var config = plugin.Configuration;
        var shown = config.ChatSummaryElements;
        var sb = new Lumina.Text.SeStringBuilder();

        sb.PushColorRgba(Muted).Append(Loc.T("[Recap] ")).PopColor();
        sb.Append(string.IsNullOrEmpty(pull.Title) ? Loc.T("Pull") : pull.Title)
          .Append($" ({NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds))})");
        switch (pull.Outcome)
        {
            case PullOutcome.Kill:
                sb.Append(" ").PushColorRgba(Good).Append(Loc.T("Kill")).PopColor();
                break;
            case PullOutcome.Wipe:
                sb.Append(" ").PushColorRgba(Bad).Append(Loc.T("Wipe")).PopColor();
                break;
        }

        // Each selected value is a part, separated by " | ".
        var parts = new List<System.Action>();

        if (shown.HasFlag(SummaryElement.Job) && pull.Job.Length > 0)
            parts.Add(() => sb.Append(pull.Job));

        if (shown.HasFlag(SummaryElement.Dps))
        {
            parts.Add(() =>
            {
                sb.Append(Loc.F("DPS {0}",NumberFormat.Format(pull.YourDps, config.NumberStyle)));
                AppendRank(sb, pull.Rank, pull.PartySize);
                AppendBest(sb, pull, pull.YourDps, p => p.YourDps, isRate: false, preview ? 1.021 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.Rdps) && pull.YourRdps is { } rdps)
        {
            parts.Add(() =>
            {
                sb.Append(Loc.F("rDPS {0}",NumberFormat.Format(rdps, config.NumberStyle)));
                AppendRank(sb, pull.RdpsRank, pull.PartySize);
                AppendBest(sb, pull, rdps, p => p.YourRdps, isRate: false, preview ? 0.988 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.Uptime) && pull.Uptime is { } gcd)
        {
            parts.Add(() =>
            {
                sb.Append(Loc.F("GCD {0:0.0%}",gcd));
                AppendBest(sb, pull, gcd, p => p.Uptime, isRate: true, preview ? -0.004 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.PartyDps))
        {
            parts.Add(() =>
            {
                sb.Append(Loc.F("pDPS {0}",NumberFormat.Format(pull.PartyDps, config.NumberStyle)));
                var threshold = config.FindThreshold(pull.TerritoryId, pull.Title);
                if (preview)
                    sb.Append(" ").PushColorRgba(Good).Append(Loc.T("(check OK)")).PopColor();
                else if (threshold is not null)
                {
                    var ok = pull.PartyDps >= threshold.EffectiveMinPartyDps;
                    sb.Append(" ").PushColorRgba(ok ? Good : Bad)
                      .Append(ok ? Loc.T("(check OK)") : Loc.F("(check {0})",NumberFormat.Format(threshold.EffectiveMinPartyDps, config.NumberStyle)))
                      .PopColor();
                }

                AppendBest(sb, pull, pull.PartyDps, p => p.PartyDps, isRate: false, preview ? 1.008 : null);
            });
        }

        if (shown.HasFlag(SummaryElement.Deaths) && pull.Deaths > 0)
            parts.Add(() => sb.PushColorRgba(Bad).Append(Loc.F("Deaths {0}",pull.Deaths)).PopColor());

        if (shown.HasFlag(SummaryElement.KilledBy) && pull.KilledBy.Length > 0)
            parts.Add(() => sb.Append(Loc.T("Killed by ")).PushColorRgba(Bad).Append(pull.KilledBy).PopColor());

        // Your failed mechanics by debuff, then the party's total: "Mechanics: Vulnerability Up x2 (party 5)".
        if (shown.HasFlag(SummaryElement.Mistakes) && pull.Mistakes.Count > 0)
        {
            var yours = pull.Mistakes.Where(m => m.IsYou).GroupBy(m => m.Status)
                            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key).ToList();
            parts.Add(() =>
            {
                sb.Append(Loc.T("Mechanics: "));
                if (yours.Count > 0)
                    sb.PushColorRgba(Bad).Append(string.Join(", ", yours)).PopColor();
                else
                    sb.PushColorRgba(Good).Append(Loc.T("none")).PopColor();
                if (pull.PartySize > 1)
                    sb.PushColorRgba(Muted).Append(Loc.F(" (party {0})",pull.Mistakes.Count)).PopColor();
            });
        }

        foreach (var part in parts)
        {
            sb.PushColorRgba(Muted).Append(" | ").PopColor();
            part();
        }

        // A link opening the main window on this pull in History.
        var message = sb.ToReadOnlySeString().ToDalamudString();
        // (Only for a pull kept in the history, not for the content left out of it in the settings.)
        if (preview || plugin.History.Pulls.Any(p => p.Id == pull.Id))
        {
            var label = new Lumina.Text.SeStringBuilder().Append(" ").PushColorRgba(Link).Append(Loc.T("[Details]")).PopColor();
            message.Payloads.Add(PullLink(pull.Id));
            message.Payloads.AddRange(label.ToReadOnlySeString().ToDalamudString().Payloads);
            message.Payloads.Add(RawPayload.LinkTerminator);
        }

        chat.Print(message);
    }

    /// <summary>
    /// Chat summary of an ultimate phase: phase time, DPS on the phase's bosses against the DPS needed for the enrage,
    /// enrage margin and GCD uptime of the phase. For a phase not cleared, the boss HP left instead of the margin.
    /// </summary>
    public void PrintPhaseSummary(PhaseResult result, bool preview = false)
    {
        var config = plugin.Configuration;
        var sb = new Lumina.Text.SeStringBuilder();

        sb.PushColorRgba(Muted).Append(Loc.T("[Recap] ")).PopColor();
        sb.Append($"{result.Fight.Key} {result.Phase.Label} {result.Phase.Name} ({NumberFormat.Duration(result.Elapsed)})");
        if (result.Cleared)
            sb.Append(" ").PushColorRgba(Good).Append(Loc.T("Cleared")).PopColor();
        else
            sb.Append(" ").PushColorRgba(Bad).Append(Loc.T("Wipe")).PopColor();

        var parts = new List<System.Action>();

        if (!result.Cleared && result.MaxHp > 0)
            parts.Add(() => sb.Append(Loc.F("HP left {0:0.0%}",result.HpLeft / result.MaxHp)));

        if (result.BossDps > 0)
        {
            parts.Add(() =>
            {
                sb.Append(Loc.F("Boss DPS {0}",NumberFormat.Format(result.BossDps, config.NumberStyle)));
                if (result.RequiredDps is { } required)
                {
                    sb.Append(" ").PushColorRgba(result.BossDps >= required ? Good : Bad)
                      .Append(Loc.F("(need {0}, {1:+0.0%;-0.0%})",NumberFormat.Format(required, config.NumberStyle),result.BossDps / required - 1))
                      .PopColor();
                }
            });
        }

        if (result.Cleared && result.EnrageSeconds is { } enrage)
        {
            var margin = enrage - result.Elapsed.TotalSeconds;
            parts.Add(() => sb.Append(Loc.F("enrage {0}, ",NumberFormat.Duration(TimeSpan.FromSeconds(enrage))))
                              .PushColorRgba(margin >= 0 ? Good : Bad).Append(Loc.F("{0:+0;-0}s margin",margin)).PopColor());
        }

        var phaseGcd = preview
                           ? 0.975
                           : plugin.Uptime.Snapshot()?.Phases.FirstOrDefault(p => p.Number == result.Number) is { AvailableSeconds: >= 5 } gcd
                               ? gcd.Uptime
                               : (double?)null;
        if (phaseGcd is { } phaseUptime)
            parts.Add(() => sb.Append(Loc.F("GCD {0:0.0%}",phaseUptime)));

        // What killed you during this phase, if you died.
        var death = preview
                        ? result.Cleared ? null : new DeathInfo(DateTime.UtcNow, Loc.T("Hydrothermal Missile"), Loc.T("Living Liquid"), 98_432)
                        : plugin.Tracker.Deaths.DeathBetween(DateTime.UtcNow - result.Elapsed - TimeSpan.FromSeconds(1), DateTime.UtcNow);
        if (config.ChatSummaryElements.HasFlag(SummaryElement.KilledBy) && death is not null)
            parts.Add(() => sb.Append(Loc.T("Killed by ")).PushColorRgba(Bad).Append(Describe(death)).PopColor());

        foreach (var part in parts)
        {
            sb.PushColorRgba(Muted).Append(" | ").PopColor();
            part();
        }

        chat.Print(sb.ToReadOnlySeString().ToDalamudString());
    }

    /// <summary>"Hydrothermal Missile (Living Liquid, 98,432)".</summary>
    public static string Describe(DeathInfo death)
    {
        var details = new List<string>();
        if (death.Source.Length > 0)
            details.Add(death.Source);
        if (death.Damage > 0)
            details.Add(death.Damage.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        return details.Count > 0 ? $"{death.Ability} ({string.Join(", ", details)})" : death.Ability;
    }

    private static void AppendRank(Lumina.Text.SeStringBuilder sb, int rank, int partySize)
    {
        if (partySize > 1 && rank > 0)
            sb.Append($" #{rank}/{partySize}");
    }

    /// <summary>
    /// "NEW BEST" when the value beats your best kill of the fight, the gap otherwise. Only kills are compared:
    /// a wipe or a prog pull ending early says nothing. Rates (GCD uptime) are compared in points.
    /// </summary>
    /// <param name="previewGain">Preview only: made-up gain over the best (ratio, or points for a rate) instead of the history.</param>
    private void AppendBest(
        Lumina.Text.SeStringBuilder sb, PullRecord pull, double value, Func<PullRecord, double?> metric, bool isRate,
        double? previewGain = null)
    {
        if (!plugin.Configuration.ChatSummaryCompareBest || pull.Outcome != PullOutcome.Kill)
            return;

        double best;
        if (previewGain is { } gain)
            best = isRate ? value - gain : value / gain;
        else if (plugin.History.BestOf(pull.Title, metric, pull.Id) is { } recorded)
            best = recorded;
        else
            return;

        var gap = isRate ? Loc.F("{0:+0.0;-0.0} pts",(value - best) * 100) : $"{value / best - 1:+0.0%;-0.0%}";
        if (value >= best)
            sb.Append(" ").PushColorRgba(Good).Append(Loc.F("NEW BEST ({0})",gap)).PopColor();
        else
            sb.Append(" ").PushColorRgba(Muted).Append(Loc.F("(best {0})",gap)).PopColor();
    }
}
