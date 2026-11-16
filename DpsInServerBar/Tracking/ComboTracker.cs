using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DpsInServerBar.Tracking;

public enum ComboMistakeKind
{
    /// <summary>A combo step used outside its combo: it lost its combo bonus.</summary>
    NoCombo,

    /// <summary>A combo left unfinished: another combo was started before its next step.</summary>
    Dropped,

    /// <summary>A combo left unfinished: its timer ran out before the next step.</summary>
    TimedOut,

    /// <summary>A charge of a buff spent by the wrong action (e.g. Paladin: Holy Spirit or Holy Circle on a Requiescat charge from level 80).</summary>
    WrongCharge,

    /// <summary>A buff of yours ran out unused (or with charges left).</summary>
    BuffExpired,

    /// <summary>Offensive actions used without a buff that must stay up (Warrior: Surging Tempest), one per time without it.</summary>
    WithoutBuff,

    /// <summary>A shield that had to break ran out whole (Dark Knight: The Blackest Night, no Dark Arts).</summary>
    ShieldUnbroken,

    /// <summary>An action left unused while it could be (Dark Knight: Salt and Darkness during Salted Earth).</summary>
    FollowUpMissed,
}

/// <summary>One combo error in a pull.</summary>
/// <param name="ActionId">The action used; TimedOut: the last step done.</param>
/// <param name="ExpectedId">NoCombo: the step that should have come before; Dropped: the combo step left unfinished; TimedOut: the last step done; WrongCharge: the action whose charge was spent.</param>
/// <param name="Expired">NoCombo: the combo had timed out (often downtime), rather than a wrong button.</param>
/// <param name="Count">BuffExpired: the charges left on the buff (0 for a buff without charges).</param>
/// <remarks>BuffExpired: ActionId is the action shown for the buff, ExpectedId the buff (status id), Expected its name,
/// Expired true when it was lost before its end (replaced by the next GCD) rather than run out.
/// WithoutBuff: one per time without the buff. ActionId is the action that puts it up (its icon), ExpectedId the buff
/// (status id), Expected its name, Count the offensive actions used without it, AtSeconds to EndSeconds the first to the last.
/// ShieldUnbroken: ActionId is the shield, ExpectedId the action it would have made free.
/// FollowUpMissed: ActionId is the action not used, ExpectedId the one that allowed it.</remarks>
public sealed record ComboMistake(
    double AtSeconds, ComboMistakeKind Kind, uint ActionId, string Action, uint ExpectedId, string Expected, bool Expired,
    int Count = 0, double EndSeconds = 0);

/// <summary>
/// Finds your combo errors (1-2-3 combos): a step used without the one before it (wrong button), a combo restarted
/// before it was finished, a combo whose timer ran out before its next step, a buff charge spent by the wrong action (Paladin), or a buff run out unused. The combos come from the game data (each step names the action
/// before it) and the combo in progress from the game. Actions the game lets you use without breaking a combo
/// are ignored. Game thread only.
/// </summary>
public sealed unsafe class ComboTracker
{
    private const int MaxMistakes = 300;

    private readonly IObjectTable objectTable;
    private readonly CombatClock combat;
    private readonly IDataManager dataManager;
    private readonly IJobGauges gauges;

    // Per action of the job: the step it follows (0 for a combo start), and the steps that can follow it.
    private readonly Dictionary<uint, uint> previous = new();
    private readonly Dictionary<uint, List<uint>> next = new();
    private readonly HashSet<uint> preserves = new();
    private readonly Dictionary<uint, string> names = new();
    private uint job;

    // The combo in progress as of the last frame (the action hook sees the game state after the action).
    private uint comboAction;
    private float comboTimer;
    private bool actionSinceLastFrame;

    // Upgraded actions standing for their base action in combos (e.g. Raiden Thrust for True Thrust), this frame.
    private readonly Dictionary<uint, uint> baseOf = new();

    // Job errors beyond the combos.
    private readonly PaladinRules paladin = new();
    private readonly BuffExpiry buffs = new();
    private readonly KeepUpBuff keepUp = new();
    private int lastOutage = -1;
    private int outageIndex = -1;
    private readonly DarkKnightRules darkKnight = new();
    private readonly SideCombo gnashingFang = GunbreakerRules.GnashingFangCombo();
    private readonly Dictionary<uint, bool> gcds = new();
    private readonly List<(ushort Id, float Remaining, int Param)> statuses = [];
    private readonly Dictionary<uint, string> statusNames = new();

    private readonly List<ComboMistake> mistakes = [];
    private DateTime combatStartUtc;
    private int revision;
    private int publishedRevision = -1;
    private IReadOnlyList<ComboMistake> published = [];

    public ComboTracker(IObjectTable objectTable, IJobGauges gauges, CombatClock combat, IDataManager dataManager)
    {
        this.gauges = gauges;
        this.objectTable = objectTable;
        this.combat = combat;
        this.dataManager = dataManager;
        paladin.Mistake += (at, spell) =>
        {
            if (mistakes.Count < MaxMistakes)
                Add(new ComboMistake(at, ComboMistakeKind.WrongCharge, spell, Name(spell),
                                     PaladinRules.Requiescat, Name(PaladinRules.Requiescat), false));
        };
        keepUp.Missing += (at, _, outage) =>
        {
            if (keepUp.Buff is not { } kept)
                return;

            // The same time without the buff: one more action on its line.
            if (outage == lastOutage && outageIndex >= 0 && outageIndex < mistakes.Count
                && mistakes[outageIndex].Kind == ComboMistakeKind.WithoutBuff)
            {
                var line = mistakes[outageIndex];
                mistakes[outageIndex] = line with { Count = line.Count + 1, EndSeconds = at };
                revision++;
                return;
            }

            if (mistakes.Count >= MaxMistakes)
                return;
            var shown = kept.Appliers[0];
            lastOutage = outage;
            outageIndex = mistakes.Count;
            Add(new ComboMistake(at, ComboMistakeKind.WithoutBuff, shown, Name(shown), kept.StatusId, StatusName(kept.StatusId), false,
                                 1, at));
        };
        darkKnight.DeliriumBroken += (at, action, step) => Add(at, ComboMistakeKind.Dropped, action, step);
        darkKnight.ShieldUnbroken += at => Add(at, ComboMistakeKind.ShieldUnbroken, DarkKnightRules.TheBlackestNight, DarkKnightRules.EdgeOfShadow);
        darkKnight.SaltUnused += at => Add(at, ComboMistakeKind.FollowUpMissed, DarkKnightRules.SaltAndDarkness, DarkKnightRules.SaltedEarth);
        gnashingFang.Broken += (at, action, step) => Add(at, ComboMistakeKind.Dropped, action, step);
        buffs.Expired += (at, buff, left, early) =>
        {
            if (mistakes.Count < MaxMistakes)
                Add(new ComboMistake(at, ComboMistakeKind.BuffExpired, buff.ShownAction, Name(buff.ShownAction),
                                     buff.StatusId, StatusName(buff.StatusId), early, left));
        };
    }

    /// <summary>Combo errors of the fight in progress, or of the last one, oldest first.</summary>
    public IReadOnlyList<ComboMistake> Current
    {
        get
        {
            if (publishedRevision != revision)
            {
                publishedRevision = revision;
                published = mistakes.ToList();
            }

            return published;
        }
    }

    /// <summary>Whether this job has combos to follow.</summary>
    public bool TracksCurrentJob => next.Count > 0 || buffs.Active || keepUp.Buff is not null;

    // A job error between two actions (the second is the expected one, or the one it is about).
    private void Add(double at, ComboMistakeKind kind, uint action, uint other)
    {
        if (mistakes.Count < MaxMistakes)
            Add(new ComboMistake(at, kind, action, Name(action), other, Name(other), false));
    }

    /// <summary>Called every frame: remembers the combo in progress.</summary>
    public void Update()
    {
        var inCombat = combat.InCombat;
        if (combat.Started)
        {
            mistakes.Clear();
            combatStartUtc = DateTime.UtcNow;
            paladin.Reset();
            buffs.Reset();
            keepUp.Reset();
            darkKnight.Reset();
            gnashingFang.Cancel();
            revision++;
        }

        var player = objectTable.LocalPlayer;
        if (player is null)
            return;
        if (player.ClassJob.RowId != job)
            Load(player.ClassJob.RowId);

        if (inCombat && (buffs.Active || keepUp.Buff is not null || job == DarkKnightRules.JobId))
        {
            statuses.Clear();
            foreach (var status in player.StatusList)
            {
                if (status.StatusId != 0)
                    statuses.Add(((ushort)status.StatusId, status.RemainingTime, status.Param));
            }

            var now = (DateTime.UtcNow - combatStartUtc).TotalSeconds;
            buffs.Update(statuses, player.IsDead, now);

            if (keepUp.Buff is { } kept)
            {
                // Darkside is not a status: it lights the Dark Knight gauge.
                var up = false;
                if (job == DarkKnightRules.JobId)
                {
                    up = gauges.Get<Dalamud.Game.ClientState.JobGauge.Types.DRKGauge>().DarksideTimeRemaining > 0;
                }
                else
                {
                    foreach (var (id, _, _) in statuses)
                        up |= id == kept.StatusId;
                }

                keepUp.Update(player.Level, up);
            }

            if (job == DarkKnightRules.JobId)
            {
                var delirium = false;
                foreach (var (id, _, _) in statuses)
                    delirium |= id == 3836;

                // Dark Arts, like Darkside, is on the job gauge.
                var darkArts = gauges.Get<Dalamud.Game.ClientState.JobGauge.Types.DRKGauge>().HasDarkArts;
                darkKnight.Update(player.Level, delirium, darkArts, player.IsDead, now);
            }

            if (job == PaladinRules.JobId)
            {
                var charges = 0;
                foreach (var (id, _, param) in statuses)
                {
                    if (id == PaladinRules.RequiescatStatus)
                        charges = Math.Max(1, param);
                }

                paladin.Update(player.Level, charges, now);
            }
        }

        var am = ActionManager.Instance();
        if (am == null)
            return;

        // The timer ran out with a step still to come (and no action ended the combo): the combo is lost.
        var action = am->Combo.Action;
        var timer = am->Combo.Timer;
        if (inCombat && !player.IsDead && !actionSinceLastFrame && comboTimer > 0 && timer <= 0 && comboAction != 0
            && next.ContainsKey(baseOf.GetValueOrDefault(comboAction, comboAction)) && mistakes.Count < MaxMistakes)
        {
            var step = baseOf.GetValueOrDefault(comboAction, comboAction);
            Add(new ComboMistake((DateTime.UtcNow - combatStartUtc).TotalSeconds, ComboMistakeKind.TimedOut, comboAction,
                                 Name(comboAction), step, Name(step), true));
        }

        actionSinceLastFrame = false;
        comboAction = action;
        comboTimer = timer;

        baseOf.Clear();
        foreach (var step in next.Keys)
        {
            var adjusted = am->GetAdjustedActionId(step);
            if (adjusted != step)
                baseOf[adjusted] = step;
        }
    }

    /// <summary>One of your actions landed (from the action effect hook).</summary>
    public void OnActionUsed(uint actionId)
    {
        actionSinceLastFrame = true;
        if (!combat.InCombat || mistakes.Count >= MaxMistakes)
            return;

        var time = (DateTime.UtcNow - combatStartUtc).TotalSeconds;
        buffs.OnAction(actionId, time);
        if (job == PaladinRules.JobId)
            paladin.OnAction(actionId, time);
        else if (job == DarkKnightRules.JobId)
            darkKnight.OnAction(actionId, time);
        else if (job == GunbreakerRules.JobId)
            gnashingFang.OnAction(actionId, time);

        if (preserves.Contains(actionId))
            return;

        var isStep = previous.TryGetValue(actionId, out var before);
        if (!isStep)
            return; // not part of a combo

        var active = comboTimer > 0 ? comboAction : 0u;
        var current = baseOf.GetValueOrDefault(active, active);
        var at = (DateTime.UtcNow - combatStartUtc).TotalSeconds;

        if (before != 0)
        {
            // A step: the combo in progress must be the one before it.
            // After a timed-out combo (already counted), the step itself is not counted again.
            var timedOut = comboTimer <= 0 && baseOf.GetValueOrDefault(comboAction, comboAction) == before;
            if (before != current && before != active && !timedOut)
                Add(new ComboMistake(at, ComboMistakeKind.NoCombo, actionId, Name(actionId), before, Name(before), false));
            return;
        }

        // A combo start, while a combo was waiting for its next step.
        if (current != 0 && next.ContainsKey(current) && !next[current].Contains(actionId))
            Add(new ComboMistake(at, ComboMistakeKind.Dropped, actionId, Name(actionId), current, Name(current), false));
    }

    private void Add(ComboMistake mistake)
    {
        mistakes.Add(mistake);
        revision++;
    }

    // The combos of a job (and of its base class) from the game data.
    private void Load(uint classJob)
    {
        job = classJob;
        buffs.SetJob(classJob);
        keepUp.SetJob(classJob);
        previous.Clear();
        next.Clear();
        preserves.Clear();

        var jobs = new HashSet<uint> { classJob };
        if (dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(classJob, out var cj) && cj.ClassJobParent.RowId != 0)
            jobs.Add(cj.ClassJobParent.RowId);

        var actions = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                                 .Where(a => a.IsPlayerAction && jobs.Contains(a.ClassJob.RowId))
                                 .ToList();
        foreach (var a in actions.Where(a => a.ActionCombo.RowId != 0))
        {
            previous[a.RowId] = a.ActionCombo.RowId;
            if (!next.TryGetValue(a.ActionCombo.RowId, out var list))
                next[a.ActionCombo.RowId] = list = [];
            list.Add(a.RowId);
        }

        // Combo starts: actions with a next step but none before them.
        foreach (var start in next.Keys.Where(k => !previous.ContainsKey(k)).ToList())
            previous[start] = 0;

        foreach (var a in actions.Where(a => a.PreservesCombo))
            preserves.Add(a.RowId);
    }

    /// <summary>One of your actions dealt damage (from the action effect hook): action id, potency bonus, target.</summary>
    public void OnDamage(uint actionId, byte bonus, uint target)
    {
        // Auto-attacks (7, 8) are not actions you choose.
        if (!combat.InCombat || keepUp.Buff is null || actionId is 7 or 8)
            return;

        keepUp.OnOffensive(actionId, IsGcd(actionId), (DateTime.UtcNow - combatStartUtc).TotalSeconds);
    }

    private bool IsGcd(uint actionId)
    {
        if (!gcds.TryGetValue(actionId, out var gcd))
        {
            gcd = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row)
                  && (row.CooldownGroup == 58 || row.AdditionalCooldownGroup == 58);
            gcds[actionId] = gcd;
        }

        return gcd;
    }

    private string StatusName(uint statusId)
    {
        if (!statusNames.TryGetValue(statusId, out var name))
        {
            name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().TryGetRow(statusId, out var row) ? row.Name.ExtractText() : $"#{statusId}";
            statusNames[statusId] = name;
        }

        return name;
    }

    private string Name(uint actionId)
    {
        if (!names.TryGetValue(actionId, out var name))
        {
            name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row) ? row.Name.ExtractText() : $"#{actionId}";
            names[actionId] = name;
        }

        return name;
    }
}
