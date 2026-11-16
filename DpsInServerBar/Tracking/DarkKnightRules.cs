using System;

namespace DpsInServerBar.Tracking;

/// <summary>
/// Dark Knight errors beyond the 1-2-3 combos and the buffs run out unused. Pure logic, no game access.
/// <list type="bullet">
/// <item>The Delirium combo (Scarlet Delirium, Comeuppance, Torcleaver) is broken by a step of the basic combos
/// (Hard Slash, Syphon Strike, Souleater, Unleash, Stalwart Soul). The game keeps it apart from the basic combo
/// (its steps do not break the basic one), so it is followed here.</item>
/// <item>The Blackest Night must break (which grants Dark Arts): one that runs out whole wasted it. Not judged when
/// Dark Arts was already up (a break would not show).</item>
/// <item>Salted Earth: Salt and Darkness must be used before it ends.</item>
/// </list>
/// </summary>
public sealed class DarkKnightRules
{
    public const uint JobId = 32;
    public const uint ScarletDelirium = 36928, Comeuppance = 36929, Torcleaver = 36930;
    public const uint TheBlackestNight = 7393, EdgeOfShadow = 16470;
    public const uint SaltedEarth = 3639, SaltAndDarkness = 25755;
    public const int SaltAndDarknessLevel = 86;

    private static readonly uint[] BasicSteps = [3617, 3623, 3632, 3621, 16468]; // Hard Slash ... Stalwart Soul

    // The Blackest Night lasts 7 s, Salted Earth 15 s; a little margin for the status to show.
    private const double BlackestNightSeconds = 7.5;
    private const double SaltedEarthSeconds = 15.5;

    private readonly SideCombo delirium = new([ScarletDelirium, Comeuppance, Torcleaver], BasicSteps);
    private double? blackestNightAt;
    private double? saltedEarthAt;
    private bool hadDarkArts;
    private int level;

    /// <summary>A basic combo step broke the Delirium combo: time, the step used, the Delirium step left waiting.</summary>
    public event Action<double, uint, uint>? DeliriumBroken
    {
        add => delirium.Broken += value;
        remove => delirium.Broken -= value;
    }

    /// <summary>The Blackest Night ran out without breaking (time it was used).</summary>
    public event Action<double>? ShieldUnbroken;

    /// <summary>Salted Earth ended without Salt and Darkness (time it was used).</summary>
    public event Action<double>? SaltUnused;

    /// <summary>Every frame in combat: your level, Delirium (the Scarlet Delirium one) on you, Dark Arts (on the job
    /// gauge, not a status), whether you are dead.</summary>
    public void Update(int playerLevel, bool deliriumUp, bool darkArts, bool dead, double now)
    {
        level = playerLevel;
        if (!deliriumUp)
            delirium.Cancel(); // over: no combo left to break
        if (dead)
        {
            Reset();
            return;
        }

        // Dark Arts gained: the shield broke.
        var gained = darkArts && !hadDarkArts;
        hadDarkArts = darkArts;

        if (blackestNightAt is { } shield)
        {
            if (gained)
            {
                blackestNightAt = null;
            }
            else if (now - shield > BlackestNightSeconds)
            {
                blackestNightAt = null;
                ShieldUnbroken?.Invoke(shield);
            }
        }

        if (saltedEarthAt is { } salt && now - salt > SaltedEarthSeconds)
        {
            saltedEarthAt = null;
            SaltUnused?.Invoke(salt);
        }
    }

    /// <summary>One of your actions landed.</summary>
    public void OnAction(uint actionId, double now)
    {
        delirium.OnAction(actionId, now);
        switch (actionId)
        {
            case TheBlackestNight:
                // A new one while the last is still up: the last was replaced whole.
                if (blackestNightAt is { } previous)
                    ShieldUnbroken?.Invoke(previous);
                // Dark Arts already up: its break would not show (the gauge only says up or not), so it is not judged.
                blackestNightAt = hadDarkArts ? null : now;
                return;
            case SaltedEarth when level >= SaltAndDarknessLevel:
                saltedEarthAt = now;
                return;
            case SaltAndDarkness:
                saltedEarthAt = null;
                return;
        }
    }

    /// <summary>A new fight, or a death.</summary>
    public void Reset()
    {
        delirium.Cancel();
        blackestNightAt = null;
        saltedEarthAt = null;
        hadDarkArts = false;
    }
}
