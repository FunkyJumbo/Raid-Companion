using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DpsInServerBar.Gear;

/// <summary>Where the check of a relic's stats stands.</summary>
public enum RelicState
{
    /// <summary>Its stats are the ones of the set.</summary>
    Right,
    /// <summary>Its stats differ from the set's.</summary>
    Wrong,
    /// <summary>It must be equipped to be checked.</summary>
    NotEquipped,
    /// <summary>You must be on the set's job (level 100, outside a duty: no level or item level sync) to check it.</summary>
    CannotCheck,
}

/// <summary>
/// Checks the stats chosen on a relic you own. The game shows them nowhere the plugin can read, so they are worked out
/// from your character: each secondary stat of the character window, less the level's base, the food's bonus and
/// everything else you wear (items and materia); what is left comes from the relic. Only possible with the relic
/// equipped, on the set's job at level 100, outside a duty. Game thread only.
/// </summary>
public static unsafe class GearRelic
{
    private const uint WellFed = 48;

    public static (RelicState State, Dictionary<uint, int> Actual) Check(GearSet set, GearPiece piece, OwnedItem? copy)
    {
        var none = new Dictionary<uint, int>();
        if (copy is null || copy.Place != GearPlace.Equipped)
            return (RelicState.NotEquipped, none);

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || GearCatalog.CurrentJob() != set.Job || player.Level < 100
            || Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56])
            return (RelicState.CannotCheck, none);

        var state = PlayerState.Instance();
        if (state == null)
            return (RelicState.CannotCheck, none);

        // Everything else you wear: items (as they are, normal or high quality) and their materia.
        var others = new Dictionary<uint, int>();
        var skipped = false;
        foreach (var item in GearInventory.Owned.Values.SelectMany(l => l).Where(o => o.Place == GearPlace.Equipped))
        {
            if (!skipped && item == copy)
            {
                skipped = true;
                continue;
            }

            foreach (var s in GearStats.OfItem(item.ItemId, item.HighQuality).Stats)
                others[s.Param] = others.GetValueOrDefault(s.Param) + s.Value;
            foreach (var (_, gives, _) in GearStats.Melds(new GearPiece { ItemId = item.ItemId, Materia = item.Materia }))
                others[gives.Param] = others.GetValueOrDefault(gives.Param) + gives.Value;
        }

        // The food eaten: its row (high quality from 10 000).
        var food = new List<FoodBonus>();
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == WellFed && status.Param > 0)
                food = GearStats.OfFoodRow((uint)(status.Param % 10_000), status.Param >= 10_000);
        }

        var actual = new Dictionary<uint, int>();
        foreach (var param in GearStats.Secondary)
        {
            var shown = state->GetAttributeByIndex((PlayerAttribute)param);
            var before = WithoutFood(shown, food.FirstOrDefault(f => f.Param == param));
            var relic = before - GearStats.BaseOf(param) - others.GetValueOrDefault(param);
            if (relic > 0)
                actual[param] = relic;
        }

        var right = GearStats.Secondary.All(p => actual.GetValueOrDefault(p) == piece.Relic.GetValueOrDefault(p));
        return (right ? RelicState.Right : RelicState.Wrong, actual);
    }

    // The stat before the food: the value whose bonus (a share of it, capped) gives the one shown.
    private static int WithoutFood(int shown, FoodBonus bonus)
    {
        if (bonus.Param == 0)
            return shown;
        if (!bonus.Relative)
            return shown - bonus.Percent;

        for (var before = Math.Max(0, shown - bonus.Max); before <= shown; before++)
        {
            if (before + Math.Min(before * bonus.Percent / 100, bonus.Max) == shown)
                return before;
        }

        return shown - bonus.Max;
    }
}
