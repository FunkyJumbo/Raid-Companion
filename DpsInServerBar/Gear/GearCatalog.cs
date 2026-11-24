using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Gear;

/// <summary>An item as the gear tools show it.</summary>
public sealed record GearItem(uint Id, string Name, uint Icon, int ItemLevel, int MateriaSlots, bool Overmeld, int Rarity);

/// <summary>
/// The game's items for the gear tools, from the game data: what a job can wear in each slot, the meals, the
/// materia. Lists are built once and kept. Names in the plugin language (and in the game's, to find them on screen).
/// </summary>
public static class GearCatalog
{
    private const uint MealCategory = 46;
    private const uint MateriaCategory = 58;

    private static readonly Dictionary<uint, GearItem?> Items = new();
    private static readonly Dictionary<(string Job, GearSlot Slot), List<GearItem>> BySlot = new();
    private static readonly Dictionary<string, HashSet<uint>> JobCategories = new();
    private static List<GearItem>? meals;
    private static List<GearItem>? materia;
    private static List<(uint Id, string Abbreviation, string Name)>? jobs;
    private static Dictionary<uint, string>? gameNames;

    /// <summary>An item by id, null when it does not exist.</summary>
    public static GearItem? Item(uint id)
    {
        if (id == 0)
            return null;
        if (Items.TryGetValue(id, out var cached))
            return cached;

        GearItem? item = null;
        if (Plugin.DataManager.GetExcelSheet<Item>(Loc.GameLanguage).TryGetRow(id, out var row))
            item = ToGear(row);
        Items[id] = item;
        return item;
    }

    private static GearItem ToGear(Item row) =>
        new(row.RowId, Loc.Game(row.Name.ExtractText()), row.Icon, (int)row.LevelItem.RowId, row.MateriaSlotCount,
            row.IsAdvancedMeldingPermitted, row.Rarity);

    /// <summary>The name of an item as the game client shows it (to find it in the game's windows).</summary>
    public static string GameName(uint id)
    {
        if (gameNames is null)
        {
            gameNames = new Dictionary<uint, string>();
        }

        if (!gameNames.TryGetValue(id, out var name))
        {
            name = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : string.Empty;
            gameNames[id] = name;
        }

        return name;
    }

    /// <summary>The combat jobs (not the base classes), in the game's order: id, English abbreviation, name.</summary>
    public static IReadOnlyList<(uint Id, string Abbreviation, string Name)> Jobs
    {
        get
        {
            if (jobs is not null)
                return jobs;

            var english = Plugin.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English);
            var local = Plugin.DataManager.GetExcelSheet<ClassJob>(Loc.GameLanguage);
            jobs = english.Where(j => j.JobIndex > 0 && j.ClassJobCategory.RowId is 30 or 31)
                          .OrderBy(j => j.UIPriority)
                          .Select(j => (j.RowId, j.Abbreviation.ExtractText(),
                                        local.TryGetRow(j.RowId, out var l) ? Capitalize(l.Name.ExtractText()) : j.Name.ExtractText()))
                          .ToList();
            return jobs;
        }
    }

    /// <summary>English abbreviation of a job by its ClassJob id, empty when unknown.</summary>
    public static string JobAbbreviation(uint id) =>
        Plugin.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English).TryGetRow(id, out var row) ? row.Abbreviation.ExtractText() : string.Empty;

    /// <summary>The job you are on (English abbreviation), empty when not logged in.</summary>
    public static string CurrentJob() => JobAbbreviation(Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0);

    private static string Capitalize(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>What a job can wear in a slot, best item level first.</summary>
    public static List<GearItem> ForSlot(string job, GearSlot slot)
    {
        if (BySlot.TryGetValue((job, slot), out var list))
            return list;

        var categories = CategoriesOf(job);
        list = Plugin.DataManager.GetExcelSheet<Item>(Loc.GameLanguage)
                     .Where(i => i.LevelItem.RowId > 1 && categories.Contains(i.ClassJobCategory.RowId) && Fits(i, slot)
                                 && i.Name.ExtractText().Length > 0)
                     .Select(ToGear)
                     .OrderByDescending(i => i.ItemLevel).ThenBy(i => i.Name)
                     .ToList();
        BySlot[(job, slot)] = list;
        return list;
    }

    private static bool Fits(Item item, GearSlot slot)
    {
        if (!item.EquipSlotCategory.IsValid)
            return false;
        var c = item.EquipSlotCategory.Value;
        return slot switch
        {
            GearSlot.Weapon => c.MainHand > 0,
            GearSlot.OffHand => c.OffHand > 0 && c.MainHand == 0,
            GearSlot.Head => c.Head > 0,
            GearSlot.Body => c.Body > 0,
            GearSlot.Hands => c.Gloves > 0,
            GearSlot.Legs => c.Legs > 0,
            GearSlot.Feet => c.Feet > 0,
            GearSlot.Ears => c.Ears > 0,
            GearSlot.Neck => c.Neck > 0,
            GearSlot.Wrists => c.Wrists > 0,
            GearSlot.RingLeft or GearSlot.RingRight => c.FingerL > 0 || c.FingerR > 0,
            _ => false,
        };
    }

    // The ClassJobCategory rows holding a job (one bool column per class or job, named by its abbreviation).
    private static HashSet<uint> CategoriesOf(string job)
    {
        if (JobCategories.TryGetValue(job, out var set))
            return set;

        set = [];
        var column = typeof(ClassJobCategory).GetProperty(job);
        if (column?.PropertyType == typeof(bool))
        {
            foreach (var row in Plugin.DataManager.GetExcelSheet<ClassJobCategory>())
            {
                if ((bool)column.GetValue(row)!)
                    set.Add(row.RowId);
            }
        }

        JobCategories[job] = set;
        return set;
    }

    /// <summary>Whether a job wears a shield (paladin): the only one with an off-hand slot.</summary>
    public static bool HasOffHand(string job) => job is "PLD" or "GLA";

    /// <summary>The meals, best item level first.</summary>
    public static List<GearItem> Meals => meals ??= Category(MealCategory);

    /// <summary>The materia, best grade first.</summary>
    public static List<GearItem> Materia => materia ??= Category(MateriaCategory);

    private static List<GearItem> Category(uint category) =>
        Plugin.DataManager.GetExcelSheet<Item>(Loc.GameLanguage)
              .Where(i => i.ItemUICategory.RowId == category && i.Name.ExtractText().Length > 0)
              .Select(ToGear)
              .OrderByDescending(i => i.ItemLevel).ThenBy(i => i.Name)
              .ToList();

    /// <summary>
    /// The materia melded into an item, as the inventory stores it (materia row and grade), as a materia item id;
    /// 0 when empty or unknown.
    /// </summary>
    public static uint MateriaItem(ushort materiaRow, byte grade)
    {
        if (materiaRow == 0 || !Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Materia>().TryGetRow(materiaRow, out var row))
            return 0;
        return grade < row.Item.Count ? row.Item[grade].RowId : 0;
    }

    /// <summary>Forget the names (the language changed).</summary>
    public static void Reset()
    {
        Items.Clear();
        BySlot.Clear();
        meals = null;
        materia = null;
        jobs = null;
    }

    /// <summary>A slot's name.</summary>
    public static string SlotName(GearSlot slot) => slot switch
    {
        GearSlot.Weapon => Loc.T("Weapon"),
        GearSlot.OffHand => Loc.T("Shield"),
        GearSlot.Head => Loc.T("Head"),
        GearSlot.Body => Loc.T("Body"),
        GearSlot.Hands => Loc.T("Hands"),
        GearSlot.Legs => Loc.T("Legs"),
        GearSlot.Feet => Loc.T("Feet"),
        GearSlot.Ears => Loc.T("Earrings"),
        GearSlot.Neck => Loc.T("Necklace"),
        GearSlot.Wrists => Loc.T("Bracelets"),
        GearSlot.RingLeft => Loc.T("Ring"),
        GearSlot.RingRight => Loc.T("Ring"),
        _ => slot.ToString(),
    };

    /// <summary>The slots of a job's sets (the shield for a paladin only).</summary>
    public static IEnumerable<GearSlot> SlotsOf(string job) =>
        Enum.GetValues<GearSlot>().Where(s => s != GearSlot.OffHand || HasOffHand(job));
}
