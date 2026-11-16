using System;
using System.Collections.Generic;

namespace DpsInServerBar.Tracking;

/// <summary>A buff of yours that must stay up (reapplied before it runs out), from a level on.</summary>
/// <param name="StatusId">The buff.</param>
/// <param name="MinLevel">The level from which the job has it.</param>
/// <param name="GraceGcds">GCDs allowed without it before the one that puts it back, after the pull starts or after it fell off (opener, downtime).</param>
/// <param name="Appliers">Actions that put it up: not counted (it shows only after them). The first one stands for the buff in the list of errors.</param>
public sealed record KeptBuff(ushort StatusId, int MinLevel, int GraceGcds, uint[] Appliers);

/// <summary>
/// Counts your offensive actions (those that dealt damage) used without a buff that must stay up (Warrior: Surging
/// Tempest, Dark Knight: Darkside). Whenever the buff is missing, the next few GCDs, and the oGCDs between them, are allowed to put it back
/// (opener, downtime). Pure logic, no game access.
/// </summary>
public sealed class KeepUpBuff
{
    /// <summary>Buffs to keep up, per job id.</summary>
    public static readonly IReadOnlyDictionary<uint, KeptBuff> Jobs = new Dictionary<uint, KeptBuff>
    {
        // Grace: GCDs before the one that puts the buff up (Warrior: Heavy Swing, Maim, then Storm's Eye = 3 GCDs).
        [21] = new(2677, 50, 2, [45, 16462]),                  // Warrior: Surging Tempest (Storm's Eye, Mythril Tempest)
        // Dark Knight: Darkside (Edge / Flood of Shadow / Darkness). Read from the job gauge, not the statuses; 751 for its name.
        [32] = new(751, 30, 1, [16470, 16469, 16467, 16466]),
    };

    private KeptBuff? buff;
    private int level;
    private bool up;
    private int graceLeft;
    private int outage;

    /// <summary>Raised for each offensive action used without the buff: time, action, and the number of the time
    /// without it (the same for every action until the buff is back).</summary>
    public event Action<double, uint, int>? Missing;

    /// <summary>The buff to keep up (that of the current job, if any).</summary>
    public KeptBuff? Buff => buff;

    public void SetJob(uint job)
    {
        buff = Jobs.TryGetValue(job, out var b) ? b : null;
        Reset();
    }

    /// <summary>Every frame in combat: your level and whether the buff is on you.</summary>
    public void Update(int playerLevel, bool buffUp)
    {
        level = playerLevel;
        // Up again: the next time it falls off, the GCDs to put it back are allowed again.
        if (buffUp && buff is not null)
        {
            graceLeft = buff.GraceGcds;
            if (!up)
                outage++;
        }

        up = buffUp;
    }

    /// <summary>One of your actions dealt damage.</summary>
    public void OnOffensive(uint actionId, bool isGcd, double time)
    {
        if (buff is null || up || level < buff.MinLevel || Array.IndexOf(buff.Appliers, actionId) >= 0)
            return;

        if (graceLeft > 0)
        {
            if (isGcd)
                graceLeft--;
            return;
        }

        Missing?.Invoke(time, actionId, outage);
    }

    /// <summary>A new fight: the opener has its GCDs to put the buff up.</summary>
    public void Reset()
    {
        up = false;
        graceLeft = buff?.GraceGcds ?? 0;
        outage++;
    }
}
