using System;
using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Tracking;

/// <summary>One of your actions in a pull: when (seconds from the pull start, negative before it) and which.</summary>
/// <param name="Item">An item used (a potion...): <paramref name="ActionId"/> is then an item id, not an action id.</param>
/// <param name="Target">The name of what it landed on first (empty for you, or when unknown).</param>
/// <param name="TargetKind">You, a player (shown under the anonymous names), or anything else.</param>
/// <param name="Targets">How many it hit (0 when unknown: recorded before this was kept).</param>
public sealed record RotationUse(double AtSeconds, uint ActionId, bool Item = false, string Target = "",
                                 TargetKind TargetKind = TargetKind.None, int Targets = 0);

public enum TargetKind : byte
{
    None,
    Self,
    Player,
    Other,
}

/// <summary>What an action landed on: its first target and how many it hit.</summary>
public readonly record struct ActionTarget(string Name, TargetKind Kind, int Count);

/// <summary>
/// Your recent actions, with when they landed, those just before the pull included: the rotation of a pull, and what
/// the mitigation plan checks. Game thread only.
/// </summary>
public sealed class ActionLog
{
    private const int MaxUses = 4000;

    // Auto-attacks are not actions you press.
    private static readonly HashSet<uint> AutoAttacks = [7, 8];

    private readonly List<(DateTime At, uint Id, bool Item, ActionTarget Target)> uses = [];

    // When the last fights ended: what is before a pull goes back no further than the end of the fight before it.
    private readonly List<DateTime> combatEnds = [];

    /// <summary>One of your actions landed, on this (from the action effect hook).</summary>
    public void OnActionUsed(uint actionId, ActionTarget target = default)
    {
        if (AutoAttacks.Contains(actionId))
            return;
        Add(actionId, false, target);
    }

    /// <summary>One of your items was used (a potion...; from the action effect hook). A high quality item counts as its item.</summary>
    public void OnItemUsed(uint itemId) => Add(itemId % 1_000_000, true, default);

    /// <summary>A fight ended (from the combat clock).</summary>
    public void OnCombatEnded() => OnCombatEnded(DateTime.UtcNow);

    public void OnCombatEnded(DateTime at)
    {
        if (combatEnds.Count >= 20)
            combatEnds.RemoveAt(0);
        combatEnds.Add(at);
    }

    private void Add(uint id, bool item, ActionTarget target)
    {
        if (uses.Count >= MaxUses)
            uses.RemoveRange(0, MaxUses / 2);
        uses.Add((DateTime.UtcNow, id, item, target));
    }

    /// <summary>
    /// Your actions from <paramref name="before"/> seconds before <paramref name="startUtc"/> on, timed from it. What is
    /// before the pull goes back no further than the end of the fight before it (the last actions of the previous pull
    /// are not this one's pre-pull).
    /// </summary>
    public List<RotationUse> Since(DateTime startUtc, double before = 0)
    {
        var from = startUtc.AddSeconds(-before);
        foreach (var end in combatEnds)
        {
            if (end < startUtc && end > from)
                from = end;
        }

        return uses.Where(u => u.At >= from)
                   .Select(u => new RotationUse((u.At - startUtc).TotalSeconds, u.Id, u.Item, u.Target.Name ?? string.Empty,
                                                u.Target.Kind, u.Target.Count))
                   .ToList();
    }
}
