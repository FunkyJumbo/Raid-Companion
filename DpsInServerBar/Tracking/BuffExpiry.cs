using System;
using System.Collections.Generic;

namespace DpsInServerBar.Tracking;

/// <summary>A buff of yours that should be spent before it runs out, and the actions that spend it.</summary>
/// <param name="StatusId">The buff.</param>
/// <param name="ShownAction">The action shown for it (icon and name in the list of errors).</param>
/// <param name="Consumers">Actions that spend it (one charge each).</param>
/// <param name="Charged">The buff has charges (its Param): running out with some left is the error, counted with them.</param>
/// <param name="AnyTime">Lost at any time without its action is an error too, not only when it runs out (Gunbreaker
/// continuations: the next GCD replaces them).</param>
public sealed record SpendableBuff(ushort StatusId, uint ShownAction, uint[] Consumers, bool Charged = false, bool AnyTime = false);

/// <summary>
/// Finds the buffs of yours that ran out unused (Paladin: Goring Blade Ready, Divine Might...; Warrior: Inner Release, Primal Rend Ready...). A buff counts as run out
/// when it disappears in its last second and none of its actions landed just then. Pure logic, no game access.
/// </summary>
public sealed class BuffExpiry
{
    // Every Warrior weaponskill spends a charge of Berserk. Declared before Jobs, which uses it (static fields start in order).
    private static readonly uint[] WarriorWeaponskills =
    [
        31, 37, 41, 42, 45, 46, 49, 51, 16462, FellCleave, Decimate, InnerChaos, ChaoticCyclone, PrimalRend, PrimalRuination,
    ];

    /// <summary>Buffs to spend, per job id.</summary>
    public static readonly IReadOnlyDictionary<uint, SpendableBuff[]> Jobs = new Dictionary<uint, SpendableBuff[]>
    {
        [PaladinRules.JobId] =
        [
            new(PaladinRules.RequiescatStatus, PaladinRules.Requiescat,
                [PaladinRules.Confiteor, PaladinRules.BladeOfFaith, PaladinRules.BladeOfTruth, PaladinRules.BladeOfValor,
                 PaladinRules.HolySpirit, PaladinRules.HolyCircle], Charged: true),
            new(PaladinRules.DivineMightStatus, PaladinRules.HolySpirit, [PaladinRules.HolySpirit, PaladinRules.HolyCircle]),
            new(3847, PaladinRules.GoringBlade, [PaladinRules.GoringBlade]),               // Goring Blade Ready
            new(3831, PaladinRules.BladeOfHonor, [PaladinRules.BladeOfHonor]),             // Blade of Honor Ready
            new(1902, PaladinRules.Atonement, [PaladinRules.Atonement]),                   // Atonement Ready
            new(3827, PaladinRules.Supplication, [PaladinRules.Supplication]),             // Supplication Ready
            new(3828, PaladinRules.Sepulchre, [PaladinRules.Sepulchre]),                   // Sepulchre Ready
            new(3019, PaladinRules.Confiteor, [PaladinRules.Confiteor]),                   // Confiteor Ready
        ],
        [WarriorJob] =
        [
            new(1177, InnerRelease, [FellCleave, Decimate], Charged: true),                // Inner Release
            new(86, Berserk, WarriorWeaponskills, Charged: true),                          // Berserk (Inner Release before level 70)
            new(2624, PrimalRend, [PrimalRend]),                                           // Primal Rend Ready
            new(3834, PrimalRuination, [PrimalRuination]),                                 // Primal Ruination Ready
            new(1897, InnerChaos, [InnerChaos, ChaoticCyclone]),                           // Nascent Chaos
            new(3901, PrimalWrath, [PrimalWrath]),                                         // Wrathful
        ],
        [GunbreakerRules.JobId] =
        [
            new(3886, GunbreakerRules.SonicBreak, [GunbreakerRules.SonicBreak]),           // Ready to Break
            new(3840, GunbreakerRules.ReignOfBeasts, [GunbreakerRules.ReignOfBeasts]),     // Ready to Reign
            new(1842, GunbreakerRules.JugularRip, [GunbreakerRules.JugularRip], AnyTime: true),    // Ready to Rip
            new(1843, GunbreakerRules.AbdomenTear, [GunbreakerRules.AbdomenTear], AnyTime: true),  // Ready to Tear
            new(1844, GunbreakerRules.EyeGouge, [GunbreakerRules.EyeGouge], AnyTime: true),        // Ready to Gouge
            new(2686, GunbreakerRules.Hypervelocity, [GunbreakerRules.Hypervelocity], AnyTime: true), // Ready to Blast
            new(3839, GunbreakerRules.FatedBrand, [GunbreakerRules.FatedBrand], AnyTime: true),    // Ready to Raze
        ],
        [DarkKnightRules.JobId] =
        [
            new(3837, 36932, [36932]),                                                     // Scorn: Disesteem
            new(3836, 7390, [DarkKnightRules.ScarletDelirium, DarkKnightRules.Comeuppance, // Delirium (from level 96)
                             DarkKnightRules.Torcleaver, 36931], Charged: true),
            new(1972, 7390, [7392, 7391], Charged: true),                                  // Delirium (before 96): Bloodspiller, Quietus
        ],
    };

    private const uint WarriorJob = 21;
    private const uint InnerRelease = 7389, FellCleave = 3549, Decimate = 3550, PrimalRend = 25753, PrimalRuination = 36925,
                       InnerChaos = 16465, ChaoticCyclone = 16463, PrimalWrath = 36924, Berserk = 38;

    // Time left under which a buff that disappears ran out (the game removes it a little after 0), and how close to
    // the disappearance one of its actions must land to have spent it.
    private const float LastSecond = 1.0f;
    private const double Window = 0.6;

    private SpendableBuff[] buffs = [];
    private readonly Dictionary<ushort, (float Remaining, int Param)> seen = new();
    private readonly Dictionary<ushort, (float Remaining, int Param)> now = new();
    private readonly Dictionary<uint, double> lastUse = new();

    /// <summary>Raised with the time, the buff, the charges left (0 for a buff without charges), and whether it was lost
    /// before its end (replaced) rather than run out.</summary>
    public event Action<double, SpendableBuff, int, bool>? Expired;

    /// <summary>The buffs to follow (those of the current job, or none).</summary>
    public void SetJob(uint job)
    {
        buffs = Jobs.TryGetValue(job, out var list) ? list : [];
        Reset();
    }

    /// <summary>Whether the current job has buffs to follow.</summary>
    public bool Active => buffs.Length > 0;

    /// <summary>Every frame in combat: the buffs on you (id, time left, param). <paramref name="dead"/>: buffs fall off with no fault.</summary>
    public void Update(IEnumerable<(ushort Id, float Remaining, int Param)> statuses, bool dead, double time)
    {
        if (buffs.Length == 0)
            return;

        now.Clear();
        foreach (var (id, remaining, param) in statuses)
        {
            foreach (var buff in buffs)
            {
                if (buff.StatusId == id)
                    now[id] = (remaining, param);
            }
        }

        if (!dead)
        {
            foreach (var buff in buffs)
            {
                if (!seen.TryGetValue(buff.StatusId, out var last) || now.ContainsKey(buff.StatusId)
                    || (last.Remaining > LastSecond && !buff.AnyTime))
                    continue;

                var spent = false;
                foreach (var action in buff.Consumers)
                    spent |= lastUse.TryGetValue(action, out var at) && Math.Abs(time - at) <= Window;

                // A charged buff loses its last charge by an action: spent; otherwise the charges left are lost.
                var left = buff.Charged ? Math.Max(1, last.Param) : 0;
                if (spent && (!buff.Charged || left <= 1))
                    continue;
                if (spent)
                    left--;
                Expired?.Invoke(time, buff, left, last.Remaining > LastSecond);
            }
        }

        seen.Clear();
        foreach (var (id, value) in now)
            seen[id] = value;
    }

    /// <summary>One of your actions landed.</summary>
    public void OnAction(uint actionId, double time) => lastUse[actionId] = time;

    /// <summary>A new fight.</summary>
    public void Reset()
    {
        seen.Clear();
        lastUse.Clear();
    }
}
