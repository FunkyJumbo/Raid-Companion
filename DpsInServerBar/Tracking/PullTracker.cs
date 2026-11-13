using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Native;
using DpsInServerBar.Ultimates;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

/// <summary>
/// Watches the snapshots of the built-in tracker, cuts them into pulls, records each pull's timeline
/// and stores finished pulls in the history. Game thread only.
/// </summary>
public sealed class PullTracker : IDisposable
{
    // ContentType sheet row for dungeons.
    private const uint DungeonContentType = 2;

    private readonly Plugin plugin;
    private readonly IDutyState dutyState;
    private readonly ICondition condition;
    private readonly ChatSummary chat;

    private CombatSnapshot? lastSeen;
    private PullInProgress? current;
    private PullOutcome pendingOutcome;
    private PullRecord? lastFinished;
    private DateTime lastFinishedUtc;

    // The chat summary waits for the duty outcome (kill / wipe), which can arrive a bit after the encounter ends.
    private PullRecord? pendingSummary;

    public PullTracker(Plugin plugin, IDutyState dutyState, ICondition condition, ChatSummary chat)
    {
        this.plugin = plugin;
        this.dutyState = dutyState;
        this.condition = condition;
        this.chat = chat;

        dutyState.DutyCompleted += OnDutyCompleted;
        dutyState.DutyWiped += OnDutyWiped;
    }

    /// <summary>The pull being fought right now, if any.</summary>
    public PullInProgress? Current => current;

    public void Dispose()
    {
        dutyState.DutyCompleted -= OnDutyCompleted;
        dutyState.DutyWiped -= OnDutyWiped;
    }

    public void Update(CombatSnapshot? snapshot)
    {
        FlushSummary(force: false);
        AnnounceDeaths();

        if (snapshot is null || ReferenceEquals(snapshot, lastSeen))
            return;
        lastSeen = snapshot;

        var seconds = snapshot.Encounter.Duration.TotalSeconds;

        if (snapshot.IsActive)
        {
            // A shorter duration than what we already have means the tracker started a new encounter.
            if (current is not null && seconds + 1 < current.LastSeconds)
                Finish(current.LastSnapshot);

            current ??= new PullInProgress(snapshot, plugin.CurrentTerritory);
            current.Add(snapshot, plugin.Configuration.IncludeLimitBreakInParty);
            return;
        }

        if (current is not null)
        {
            current.Add(snapshot, plugin.Configuration.IncludeLimitBreakInParty);
            Finish(snapshot);
        }
    }

    /// <summary>
    /// Your current pace against your best pull of the same encounter, at the same point in time
    /// (+0.05 = 5% more damage done so far). Null when there is nothing to compare to.
    /// </summary>
    public double? PaceVsBest()
    {
        if (current is null || current.LastSeconds < 10)
            return null;

        var best = plugin.History.BestFor(current.Title);
        var t = (int)current.LastSeconds;
        if (best is null || best.YourDamageTimeline.Length <= t || best.YourDamageTimeline[t] <= 0)
            return null;

        return current.LastYourDamage / best.YourDamageTimeline[t] - 1;
    }

    private void Finish(CombatSnapshot final)
    {
        var pull = current!;
        current = null;
        var outcome = pendingOutcome;
        pendingOutcome = PullOutcome.Unknown;

        var config = plugin.Configuration;
        var duration = final.Encounter.Duration.TotalSeconds;
        if (duration < config.MinPullSeconds)
            return;
        if (config.RecordOnlyInDuties && !condition[ConditionFlag.BoundByDuty])
            return;

        var uptime = plugin.Uptime.Snapshot();
        var you = final.You;
        var partyMembers = final.Combatants.Where(c => c.HasJob).ToList();
        var record = new PullRecord
        {
            StartUtc = pull.StartUtc,
            Title = pull.Title,
            Zone = final.Encounter.Zone,
            TerritoryId = pull.TerritoryId,
            InstanceRun = plugin.InstanceRun,
            DurationSeconds = duration,
            Outcome = outcome,
            Job = you?.Job ?? string.Empty,
            YourDps = you?.EncDps ?? 0,
            YourRdps = you is { HasRdps: true } ? you.RDps : null,
            Uptime = uptime?.Uptime,
            UptimeLostSeconds = uptime?.LostSeconds ?? 0,
            Gcd = uptime?.Gcd,
            Mistakes = plugin.Tracker.Mistakes.Current.ToList(),
            PartyDeaths = plugin.Tracker.PartyDeaths.Since(pull.StartUtc).ToList(),
            GaugeWaste = plugin.Gauges.Current.ToList(),
            Positionals = plugin.Positionals.Current.ToList(),
            ComboMistakes = plugin.Combos.Current.ToList(),
            DotUptimes = plugin.Dots.Current.ToList(),
            TankSwaps = plugin.Tracker.TankSwaps.Current.ToList(),
            Overheal = plugin.Tracker.Heals.Current.ToList(),
            Mitigations = plugin.Tracker.Mitigations.Current.ToList(),
            BossCasts = plugin.FightLog.CastsSince(pull.StartUtc),
            Debuffs = plugin.FightLog.DebuffsSince(pull.StartUtc),
            PhaseUptimes = uptime?.Phases.ToList() ?? [],
            UptimeAvailableBySecond = uptime?.AvailableBySecond ?? [],
            UptimeActiveBySecond = uptime?.ActiveBySecond ?? [],
            BossHpLeft = UltimateHpLeft(pull.TerritoryId) ?? plugin.Boss.Current?.HpFraction,
            PhaseSeconds = UltimateTracker.FightFor(pull.TerritoryId) is not null && plugin.Ultimates.Current is { Started: true } reached
                               ? reached.Elapsed.TotalSeconds
                               : null,
            BossName = plugin.Boss.Current?.Name ?? string.Empty,
            LastBossCast = plugin.Boss.LastCast,
            LastBossCastId = plugin.Boss.LastCastId,
            PartyDps = final.PartyDps(config.IncludeLimitBreakInParty),
            Rank = final.YourRank(),
            RdpsRank = you is { HasRdps: true } ? final.YourRank(byRdps: true) : 0,
            PartySize = partyMembers.Count,
            Deaths = you?.Deaths ?? 0,
            KilledBy = plugin.Tracker.Deaths.DeathBetween(pull.StartUtc, DateTime.UtcNow) is { } death ? ChatSummary.Describe(death) : string.Empty,
            Combatants = final.Combatants
                .OrderByDescending(c => c.EncDps)
                .Select(c => new PullCombatant
                {
                    Name = c.Name,
                    Job = c.Job,
                    Dps = c.EncDps,
                    Rdps = c.HasRdps ? c.RDps : null,
                    DamagePercent = c.DamagePercent,
                    CritPercent = c.CritPercent,
                    DirectHitPercent = c.DirectHitPercent,
                    CritDirectHitPercent = c.CritDirectHitPercent,
                    Deaths = c.Deaths,
                    IsYou = c.IsYou,
                })
                .ToList(),
            YourDamageTimeline = Timeline.Resample(pull.YourPoints, duration),
            PartyDamageTimeline = Timeline.Resample(pull.PartyPoints, duration),
        };

        if ((config.RecordedCategories & plugin.ContentCategories.Of(record.TerritoryId)) != 0)
            plugin.History.Add(record, config.MaxHistory);
        lastFinished = record;
        lastFinishedUtc = DateTime.UtcNow;

        // In a supported ultimate the phase summaries replace the pull summary.
        var ultimateSummaries = config.UltimateMode && config.ChatSummaryPhases && UltimateTracker.FightFor(record.TerritoryId) is not null;
        if (config.ChatSummary && !ultimateSummaries && (config.ChatSummaryInDungeons || !IsDungeon(record.TerritoryId)))
        {
            FlushSummary(force: true); // a previous one still waiting
            pendingSummary = record;
            FlushSummary(force: false);
        }
    }

    private void OnDutyCompleted(IDutyStateEventArgs args) => SetOutcome(PullOutcome.Kill);

    private void OnDutyWiped(IDutyStateEventArgs args) => SetOutcome(PullOutcome.Wipe);

    private void SetOutcome(PullOutcome outcome)
    {
        // The duty event can arrive slightly before or after the tracker closes the encounter.
        if (current is not null)
        {
            pendingOutcome = outcome;
        }
        else if (lastFinished is not null && (DateTime.UtcNow - lastFinishedUtc).TotalSeconds < 30)
        {
            lastFinished.Outcome = outcome;
            plugin.History.Changed(lastFinished);
            FlushSummary(force: false);
        }
    }

    private static bool IsDungeon(uint territory) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)
        && row.ContentFinderCondition.ValueNullable?.ContentType.RowId == DungeonContentType;

    /// <summary>Prints the pending summary once the outcome is known, or after a few seconds without one.</summary>
    private void FlushSummary(bool force)
    {
        if (pendingSummary is not { } pull)
            return;
        if (!force && pull.Outcome == PullOutcome.Unknown && (DateTime.UtcNow - lastFinishedUtc).TotalSeconds < 10)
            return;

        pendingSummary = null;
        // Your death can be seen, or its cause refined (a body still falling), after the encounter was closed.
        if (plugin.Tracker.Deaths.DeathBetween(pull.StartUtc, DateTime.UtcNow) is { } death && ChatSummary.Describe(death) != pull.KilledBy)
        {
            pull.KilledBy = ChatSummary.Describe(death);
            plugin.History.Changed(pull);
        }

        // Same for the party: a death closing the fight, or a cause refined since.
        var deaths = plugin.Tracker.PartyDeaths.Since(pull.StartUtc);
        if (deaths.Count >= pull.PartyDeaths.Count && DeathKey(deaths) != DeathKey(pull.PartyDeaths))
        {
            pull.PartyDeaths = deaths.ToList();
            plugin.History.Changed(pull);
        }

        chat.PrintSummary(pull);
    }

    /// <summary>Prints each party death in chat as it happens, when the option is on.</summary>
    private void AnnounceDeaths()
    {
        // Taken even with the option off, so turning it on does not print the deaths of the fight so far.
        // Waits 2 s: long enough for a death to get its final cause, and to see whether the party is wiping.
        var deaths = plugin.Tracker.PartyDeaths.TakeNew(TimeSpan.FromSeconds(2));
        if (!plugin.Configuration.ChatDeaths || deaths.Count == 0)
            return;

        // Once the party wiped (everyone dead, or the duty says so) or the pull is over, the deaths stop: the recap tells
        // the rest, rather than a line for each player killed by the wipe.
        if (current is null || pendingOutcome == PullOutcome.Wipe || UltimateTracker.PartyWiped())
            return;

        foreach (var (player, job, isYou, info) in deaths)
            chat.PrintDeath(player, job, isYou, info, info.TimeUtc - current.StartUtc);
    }

    /// <summary>
    /// In a supported ultimate, the HP left of the current phase's bosses (all of them, e.g. Brute Justice and Cruise
    /// Chaser): the biggest enemy of the fight can be an invisible one that never takes damage. In a transition there is
    /// no boss to hit: the phase counts as just reached. Null elsewhere.
    /// </summary>
    private double? UltimateHpLeft(uint territory)
    {
        if (UltimateTracker.FightFor(territory) is null || plugin.Ultimates.Current is not { Supported: true } phase)
            return null;
        if (phase.IsTransition)
            return 1;
        return phase is { Started: true, MaxHp: > 0 } ? phase.HpFraction : 1;
    }

    private static string DeathKey(IEnumerable<PartyDeath> deaths) =>
        string.Join("|", deaths.Select(d => $"{d.Player} {d.AtSeconds:0} {d.Cause} {d.Damage}"));
}

/// <summary>Data collected while a pull is ongoing.</summary>
public sealed class PullInProgress
{
    private readonly List<(double T, double Value)> yourPoints = [];
    private readonly List<(double T, double Value)> partyPoints = [];

    public PullInProgress(CombatSnapshot first, uint territoryId)
    {
        StartUtc = DateTime.UtcNow - first.Encounter.Duration;
        TerritoryId = territoryId;
        LastSnapshot = first;
    }

    public DateTime StartUtc { get; }
    public uint TerritoryId { get; }
    public string Title => LastSnapshot.Encounter.Title;
    public CombatSnapshot LastSnapshot { get; private set; }
    public double LastSeconds { get; private set; }
    public double LastYourDamage { get; private set; }
    public IReadOnlyList<(double T, double Value)> YourPoints => yourPoints;
    public IReadOnlyList<(double T, double Value)> PartyPoints => partyPoints;

    /// <summary>Bumped on every new point, so the graph knows when to rebuild its curves.</summary>
    public int Revision { get; private set; }

    public void Add(CombatSnapshot snapshot, bool includeLimitBreak)
    {
        LastSnapshot = snapshot;
        var t = snapshot.Encounter.Duration.TotalSeconds;
        if (t <= 0 || (yourPoints.Count > 0 && t <= yourPoints[^1].T))
            return;

        LastSeconds = t;
        LastYourDamage = snapshot.You?.Damage ?? 0;
        var party = snapshot.Combatants
            .Where(c => c.HasJob || (includeLimitBreak && c.IsLimitBreak))
            .Sum(c => c.Damage);

        yourPoints.Add((t, LastYourDamage));
        partyPoints.Add((t, party));
        Revision++;
    }
}
