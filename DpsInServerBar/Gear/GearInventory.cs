using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DpsInServerBar.Gear;

/// <summary>Where an item you own is.</summary>
public enum GearPlace { Equipped, Armoury, Inventory, Saddlebag }

/// <summary>An item you own: where, and the materia melded into it (materia item ids, in slot order).</summary>
public sealed record OwnedItem(uint ItemId, GearPlace Place, List<uint> Materia, bool HighQuality = false);

/// <summary>
/// What you own of a gear set, read from the game: equipped, in the armoury chest, the inventory and the saddlebag
/// (once opened this session). Retainers are not read. Refreshed every couple of seconds while it is looked at.
/// Game thread only.
/// </summary>
public static unsafe class GearInventory
{
    private static readonly (InventoryType Type, GearPlace Place)[] Containers =
    [
        (InventoryType.EquippedItems, GearPlace.Equipped),
        (InventoryType.ArmoryMainHand, GearPlace.Armoury), (InventoryType.ArmoryOffHand, GearPlace.Armoury),
        (InventoryType.ArmoryHead, GearPlace.Armoury), (InventoryType.ArmoryBody, GearPlace.Armoury),
        (InventoryType.ArmoryHands, GearPlace.Armoury), (InventoryType.ArmoryLegs, GearPlace.Armoury),
        (InventoryType.ArmoryFeets, GearPlace.Armoury), (InventoryType.ArmoryEar, GearPlace.Armoury),
        (InventoryType.ArmoryNeck, GearPlace.Armoury), (InventoryType.ArmoryWrist, GearPlace.Armoury),
        (InventoryType.ArmoryRings, GearPlace.Armoury),
        (InventoryType.Inventory1, GearPlace.Inventory), (InventoryType.Inventory2, GearPlace.Inventory),
        (InventoryType.Inventory3, GearPlace.Inventory), (InventoryType.Inventory4, GearPlace.Inventory),
        (InventoryType.SaddleBag1, GearPlace.Saddlebag), (InventoryType.SaddleBag2, GearPlace.Saddlebag),
        (InventoryType.PremiumSaddleBag1, GearPlace.Saddlebag), (InventoryType.PremiumSaddleBag2, GearPlace.Saddlebag),
    ];

    private static Dictionary<uint, List<OwnedItem>> owned = new();
    private static DateTime nextRead = DateTime.MinValue;

    /// <summary>Everything you own, by item id (read again when older than two seconds).</summary>
    public static IReadOnlyDictionary<uint, List<OwnedItem>> Owned
    {
        get
        {
            if (DateTime.UtcNow >= nextRead)
            {
                nextRead = DateTime.UtcNow.AddSeconds(2);
                owned = Read();
            }

            return owned;
        }
    }

    private static Dictionary<uint, List<OwnedItem>> Read()
    {
        var result = new Dictionary<uint, List<OwnedItem>>();
        var manager = InventoryManager.Instance();
        if (manager == null)
            return result;

        foreach (var (type, place) in Containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;

                var materia = new List<uint>();
                for (var m = 0; m < 5; m++)
                    materia.Add(GearCatalog.MateriaItem(item->Materia[m], item->MateriaGrades[m]));
                while (materia.Count > 0 && materia[^1] == 0)
                    materia.RemoveAt(materia.Count - 1);

                var id = item->ItemId % 1_000_000; // high quality ids are offset
                if (!result.TryGetValue(id, out var list))
                    result[id] = list = [];
                list.Add(new OwnedItem(id, place, materia, item->IsHighQuality()));
            }
        }

        return result;
    }

    /// <summary>
    /// The copy of each piece of a set you own (the best one: equipped first, then the most materia as planned), null
    /// for a piece you lack. Two rings of the same item need two copies.
    /// </summary>
    public static Dictionary<GearSlot, OwnedItem?> Match(GearSet set)
    {
        var all = Owned;
        var used = new HashSet<OwnedItem>();
        var result = new Dictionary<GearSlot, OwnedItem?>();
        foreach (var (slot, piece) in set.Pieces.Where(p => p.Value.ItemId != 0).OrderBy(p => p.Key))
        {
            var best = all.TryGetValue(piece.ItemId, out var copies)
                           ? copies.Where(c => !used.Contains(c))
                                   .OrderByDescending(c => MateriaDone(piece, c))
                                   .ThenBy(c => c.Place)
                                   .FirstOrDefault()
                           : null;
            if (best is not null)
                used.Add(best);
            result[slot] = best;
        }

        return result;
    }

    /// <summary>How many of a piece's planned materia the copy holds, in any slot.</summary>
    public static int MateriaDone(GearPiece piece, OwnedItem? copy) => Melded(piece, copy).Count(m => m);

    /// <summary>
    /// For each planned materia of a piece (in its order), whether the copy holds it, in any slot: the order of the
    /// melds does not matter, each melded materia counts once.
    /// </summary>
    public static bool[] Melded(GearPiece piece, OwnedItem? copy)
    {
        var result = new bool[piece.Materia.Count];
        if (copy is null)
            return result;

        var left = copy.Materia.Where(m => m != 0).ToList();
        for (var i = 0; i < piece.Materia.Count; i++)
        {
            if (piece.Materia[i] != 0 && left.Remove(piece.Materia[i]))
                result[i] = true;
        }

        return result;
    }

    /// <summary>Whether you own the food (any amount).</summary>
    public static bool HasItem(uint id) => id != 0 && Owned.ContainsKey(id);
}
