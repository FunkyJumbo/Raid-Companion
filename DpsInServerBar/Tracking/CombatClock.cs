using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace DpsInServerBar.Tracking;

/// <summary>
/// Whether you are in combat, read once per frame for every tracker, with the frame combat started or ended on.
/// Update it first in the framework update. Game thread only.
/// </summary>
public sealed class CombatClock
{
    private readonly ICondition condition;

    public CombatClock(ICondition condition) => this.condition = condition;

    /// <summary>In combat, as of the last frame update.</summary>
    public bool InCombat { get; private set; }

    /// <summary>Combat started on this frame.</summary>
    public bool Started { get; private set; }

    /// <summary>Combat ended on this frame.</summary>
    public bool Ended { get; private set; }

    public void Update()
    {
        var inCombat = condition[ConditionFlag.InCombat];
        Started = inCombat && !InCombat;
        Ended = !inCombat && InCombat;
        InCombat = inCombat;
    }
}
