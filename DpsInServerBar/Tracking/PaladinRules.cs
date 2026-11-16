using System;

namespace DpsInServerBar.Tracking;

/// <summary>
/// Paladin errors beyond the 1-2-3 combos. From level 80, the Requiescat charges go to Confiteor and its combo:
/// a Holy Spirit or Holy Circle that spends one is an error. Under Divine Might they spend Divine Might first, so they are fine.
/// Which buff one of them spent is read from the statuses (the one that loses a charge just around the action),
/// so the order in which the game reports the action and the status change does not matter. Pure logic, no game access.
/// </summary>
public sealed class PaladinRules
{
    public const uint JobId = 19;
    public const uint HolySpirit = 7384;
    public const uint HolyCircle = 16458;
    public const uint Requiescat = 7383;
    public const uint GoringBlade = 3538;
    public const uint Atonement = 16460;
    public const uint Supplication = 36918;
    public const uint Sepulchre = 36919;
    public const uint Confiteor = 16459;
    public const uint BladeOfFaith = 25748;
    public const uint BladeOfTruth = 25749;
    public const uint BladeOfValor = 25750;
    public const uint BladeOfHonor = 36922;
    public const ushort RequiescatStatus = 1368;
    public const ushort DivineMightStatus = 2673;
    public const int ConfiteorLevel = 80;

    // The spell and the next GCD 2.5 s later: a charge lost this close to one was spent by it.
    private const double Window = 0.6;

    private int level;
    private int requiescat = -1; // charges last frame, -1 before the first frame
    private double requiescatSpentAt = double.NegativeInfinity;
    private double? spellAt;
    private uint spell;

    /// <summary>Raised with the time and action of a Holy Spirit or Holy Circle that spent a Requiescat charge.</summary>
    public event Action<double, uint>? Mistake;

    /// <summary>Every frame: the player's level, Requiescat charges (0 without it) and the combat time.</summary>
    public void Update(int playerLevel, int requiescatCharges, double now)
    {
        level = playerLevel;

        // A charge spent: one less, or the buff gone with its last charge (not when it ran out with charges left).
        if (requiescat > 0 && (requiescatCharges == requiescat - 1 || (requiescatCharges == 0 && requiescat == 1)))
            requiescatSpentAt = now;
        requiescat = requiescatCharges;

        if (spellAt is { } at)
        {
            if (Math.Abs(requiescatSpentAt - at) <= Window)
            {
                spellAt = null;
                Mistake?.Invoke(at, spell);
            }
            else if (now - at > Window)
            {
                spellAt = null;
            }
        }
    }

    /// <summary>One of your actions landed.</summary>
    public void OnAction(uint actionId, double now)
    {
        if (actionId is HolySpirit or HolyCircle && level >= ConfiteorLevel)
        {
            spellAt = now;
            spell = actionId;
        }
    }

    /// <summary>A new fight.</summary>
    public void Reset()
    {
        spellAt = null;
        requiescatSpentAt = double.NegativeInfinity;
    }
}
