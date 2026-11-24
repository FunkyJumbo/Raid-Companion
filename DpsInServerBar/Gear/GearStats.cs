using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Gear;

/// <summary>A stat of an item or a set: its BaseParam row and value.</summary>
public readonly record struct Stat(uint Param, int Value);

/// <summary>A food bonus: a share of the stat (capped), or a flat value.</summary>
public readonly record struct FoodBonus(uint Param, int Percent, int Max, bool Relative);

/// <summary>A set's totals for a stat: level base, gear, materia (after the caps), food, and what the caps cut.</summary>
public sealed record StatTotal(uint Param, int Base, int Gear, int Materia, int Food, int Wasted)
{
    public int Total => Base + Gear + Materia + Food;
}

/// <summary>
/// The stats of items, materia and food from the game data, and a set's totals at level 100: the main and secondary
/// stats (critical hit, direct hit, determination, speed, tenacity, piety). Materia raise a secondary stat up to the
/// item's highest secondary stat (the meld cap). High quality values for items that can be high quality.
/// </summary>
public static class GearStats
{
    // BaseParam rows.
    public const uint Strength = 1, Dexterity = 2, Vitality = 3, Intelligence = 4, Mind = 5, Piety = 6, Tenacity = 19,
                      DirectHit = 22, CriticalHit = 27, Determination = 44, SkillSpeed = 45, SpellSpeed = 46;

    /// <summary>The secondary stats, in the order of the character window.</summary>
    public static readonly uint[] Secondary = [CriticalHit, Determination, DirectHit, SkillSpeed, SpellSpeed, Tenacity, Piety];

    /// <summary>The stats a set shows: the main ones, then the secondary ones.</summary>
    public static readonly uint[] Shown = [Strength, Dexterity, Vitality, Intelligence, Mind, .. Secondary];

    // Level 100: base main stat (before race and job), base secondary stats, and the divisor of the speed formula.
    private const int MainBase = 440, SubBase = 420, Divisor = 2780;

    /// <summary>A secondary stat's value with no gear at level 100.</summary>
    public static int BaseOf(uint param) => param is Determination or Piety ? 440 : SubBase;

    private static readonly Dictionary<(uint, bool), (List<Stat> Stats, int Damage)> ItemCache = new();
    private static Dictionary<uint, Stat>? materia;
    private static readonly Dictionary<uint, List<FoodBonus>> FoodCache = new();
    private static readonly Dictionary<uint, string> Names = new();

    /// <summary>A stat's name in the plugin language.</summary>
    public static string Name(uint param)
    {
        if (!Names.TryGetValue(param, out var name))
        {
            name = Plugin.DataManager.GetExcelSheet<BaseParam>(Loc.GameLanguage).TryGetRow(param, out var row) ? row.Name.ExtractText() : $"#{param}";
            Names[param] = name;
        }

        return name;
    }

    /// <summary>
    /// An item's stats and its weapon damage (0 for armour); high quality when it can be, unless told otherwise.
    /// </summary>
    public static (List<Stat> Stats, int Damage) OfItem(uint id, bool highQuality = true)
    {
        if (ItemCache.TryGetValue((id, highQuality), out var cached))
            return cached;

        var stats = new List<Stat>();
        var damage = 0;
        if (Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var row))
        {
            var values = new Dictionary<uint, int>();
            for (var i = 0; i < row.BaseParam.Count; i++)
            {
                if (row.BaseParam[i].RowId != 0)
                    values[row.BaseParam[i].RowId] = values.GetValueOrDefault(row.BaseParam[i].RowId) + row.BaseParamValue[i];
            }

            if (row.CanBeHq && highQuality)
            {
                for (var i = 0; i < row.BaseParamSpecial.Count; i++)
                {
                    var param = row.BaseParamSpecial[i].RowId;
                    if (param != 0 && values.ContainsKey(param))
                        values[param] += row.BaseParamValueSpecial[i];
                }
            }

            stats = values.Select(v => new Stat(v.Key, v.Value)).ToList();
            damage = Math.Max(row.DamagePhys, row.DamageMag);
        }

        ItemCache[(id, highQuality)] = (stats, damage);
        return (stats, damage);
    }

    /// <summary>The stat a materia gives, null when the item is not a materia.</summary>
    public static Stat? OfMateria(uint itemId)
    {
        if (materia is null)
        {
            materia = new Dictionary<uint, Stat>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Materia>())
            {
                for (var grade = 0; grade < row.Item.Count && grade < row.Value.Count; grade++)
                {
                    if (row.Item[grade].RowId != 0 && row.Value[grade] > 0)
                        materia[row.Item[grade].RowId] = new Stat(row.BaseParam.RowId, row.Value[grade]);
                }
            }
        }

        return materia.TryGetValue(itemId, out var stat) ? stat : null;
    }

    /// <summary>A meal's bonuses (high quality), empty when the item is not food.</summary>
    public static List<FoodBonus> OfFood(uint itemId)
    {
        if (FoodCache.TryGetValue(itemId, out var cached))
            return cached;

        var bonuses = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item) && item.ItemAction.IsValid
                      && item.ItemAction.Value.Data.Count > 1
                          ? OfFoodRow(item.ItemAction.Value.Data[1], true)
                          : [];
        FoodCache[itemId] = bonuses;
        return bonuses;
    }

    /// <summary>The bonuses of an ItemFood row (the "Well Fed" status names it), normal or high quality.</summary>
    public static List<FoodBonus> OfFoodRow(uint row, bool highQuality)
    {
        var bonuses = new List<FoodBonus>();
        if (Plugin.DataManager.GetExcelSheet<ItemFood>().TryGetRow(row, out var food))
        {
            foreach (var p in food.Params)
            {
                if (p.BaseParam.RowId != 0)
                    bonuses.Add(highQuality
                                    ? new FoodBonus(p.BaseParam.RowId, p.ValueHQ, p.MaxHQ, p.IsRelative)
                                    : new FoodBonus(p.BaseParam.RowId, p.Value, p.Max, p.IsRelative));
            }
        }

        return bonuses;
    }

    /// <summary>
    /// Whether an item is a relic whose secondary stats the player chooses: equipment of a high item level with no
    /// secondary stat of its own.
    /// </summary>
    public static bool IsCustomRelic(uint itemId) =>
        GearCatalog.Item(itemId) is { ItemLevel: > 1 } item && item.MateriaSlots == 0 && !item.Overmeld
        && OfItem(itemId).Damage + OfItem(itemId).Stats.Count > 0
        && !OfItem(itemId).Stats.Any(s => Secondary.Contains(s.Param));

    /// <summary>The meld cap of an item: its highest secondary stat (what a materia can raise another one to).</summary>
    public static int MeldCap(uint itemId) =>
        OfItem(itemId).Stats.Where(s => Secondary.Contains(s.Param)).Select(s => s.Value).DefaultIfEmpty(0).Max();

    /// <summary>
    /// A piece's materia, each with what it really adds once the item's cap is reached (in slot order): the stat, the
    /// value added and the value lost to the cap.
    /// </summary>
    public static List<(uint MateriaId, Stat Gives, int Wasted)> Melds(GearPiece piece)
    {
        var result = new List<(uint, Stat, int)>();
        var cap = MeldCap(piece.ItemId);
        var current = OfItem(piece.ItemId).Stats.ToDictionary(s => s.Param, s => s.Value);
        foreach (var id in piece.Materia)
        {
            if (OfMateria(id) is not { } stat)
                continue;
            var have = current.GetValueOrDefault(stat.Param);
            var room = Secondary.Contains(stat.Param) ? Math.Max(0, cap - have) : stat.Value;
            var added = Math.Min(stat.Value, room);
            current[stat.Param] = have + added;
            result.Add((id, new Stat(stat.Param, added), stat.Value - added));
        }

        return result;
    }

    /// <summary>A piece's secondary stats: from the item, from its materia (after the cap), and what the cap cut.</summary>
    public static Dictionary<uint, (int Gear, int Meld, int Wasted)> OfPiece(GearPiece piece)
    {
        var result = new Dictionary<uint, (int Gear, int Meld, int Wasted)>();
        foreach (var s in OfItem(piece.ItemId).Stats.Where(s => Secondary.Contains(s.Param)))
            result[s.Param] = (s.Value, 0, 0);
        foreach (var (param, value) in piece.Relic.Where(r => r.Value > 0))
            result[param] = (result.GetValueOrDefault(param).Gear + value, 0, 0);
        foreach (var (_, gives, wasted) in Melds(piece))
        {
            if (!Secondary.Contains(gives.Param))
                continue;
            var (gear, meld, lost) = result.GetValueOrDefault(gives.Param);
            result[gives.Param] = (gear, meld + gives.Value, lost + wasted);
        }

        return result;
    }

    /// <summary>A set's totals at level 100, for every stat shown (main stats without race and job bonuses).</summary>
    public static List<StatTotal> Totals(GearSet set)
    {
        var gear = new Dictionary<uint, int>();
        var melded = new Dictionary<uint, int>();
        var wasted = new Dictionary<uint, int>();
        foreach (var piece in set.Pieces.Values.Where(p => p.ItemId != 0))
        {
            foreach (var s in OfItem(piece.ItemId).Stats)
                gear[s.Param] = gear.GetValueOrDefault(s.Param) + s.Value;
            foreach (var (param, value) in piece.Relic)
                gear[param] = gear.GetValueOrDefault(param) + value;
            foreach (var (_, gives, lost) in Melds(piece))
            {
                melded[gives.Param] = melded.GetValueOrDefault(gives.Param) + gives.Value;
                wasted[gives.Param] = wasted.GetValueOrDefault(gives.Param) + lost;
            }
        }

        var food = OfFood(set.Food);
        var totals = new List<StatTotal>();
        foreach (var param in Shown)
        {
            var main = param is Strength or Dexterity or Vitality or Intelligence or Mind;
            var level = main ? MainBase : param is Determination or Piety ? 440 : SubBase;
            var before = level + gear.GetValueOrDefault(param) + melded.GetValueOrDefault(param);
            var bonus = food.FirstOrDefault(f => f.Param == param) is { Param: > 0 } f
                            ? f.Relative ? Math.Min(before * f.Percent / 100, f.Max) : f.Percent
                            : 0;
            totals.Add(new StatTotal(param, level, gear.GetValueOrDefault(param), melded.GetValueOrDefault(param), bonus,
                                     wasted.GetValueOrDefault(param)));
        }

        return totals;
    }

    /// <summary>Critical hit rate (%) at level 100.</summary>
    public static double CritRate(int crit) => Math.Floor(200.0 * (crit - SubBase) / Divisor + 50) / 10;

    /// <summary>Critical hit damage (%: 140 = +40%) at level 100.</summary>
    public static double CritDamage(int crit) => Math.Floor(200.0 * (crit - SubBase) / Divisor + 1400) / 10;

    /// <summary>Direct hit rate (%) at level 100.</summary>
    public static double DirectHitRate(int directHit) => Math.Floor(550.0 * (directHit - SubBase) / Divisor) / 10;

    /// <summary>Determination's damage bonus (%) at level 100.</summary>
    public static double DeterminationBonus(int determination) => Math.Floor(140.0 * (determination - 440) / Divisor) / 10;

    /// <summary>Speed's bonus to damage over time and auto-attacks (%) at level 100.</summary>
    public static double SpeedBonus(int speed) => Math.Floor(130.0 * (speed - SubBase) / Divisor) / 10;

    /// <summary>Tenacity's bonus to damage and damage taken reduction (%) at level 100.</summary>
    public static double TenacityBonus(int tenacity) => Math.Floor(112.0 * (tenacity - SubBase) / Divisor) / 10;

    /// <summary>MP regained every 3 s at a piety (level 100).</summary>
    public static int PietyMp(int piety) => (int)Math.Floor(150.0 * (piety - 440) / Divisor) + 200;

    /// <summary>What a secondary stat's value gives, as text (rates, bonuses, GCD); empty for the main stats.</summary>
    public static string Effect(uint param, int value) => param switch
    {
        CriticalHit => Loc.F("{0:0.0}% rate, +{1:0.0}% damage", CritRate(value), CritDamage(value) - 100),
        DirectHit => Loc.F("{0:0.0}% rate", DirectHitRate(value)),
        Determination => Loc.F("+{0:0.0}% damage", DeterminationBonus(value)),
        SkillSpeed or SpellSpeed => Loc.F("GCD {0:0.00}s, +{1:0.0}% damage over time", Gcd(value), SpeedBonus(value)),
        Tenacity => Loc.F("+{0:0.0}% damage, -{0:0.0}% taken", TenacityBonus(value)),
        Piety => Loc.F("{0} MP every 3 s", PietyMp(value)),
        _ => string.Empty,
    };

    /// <summary>The 2.5 s global cooldown at a skill or spell speed (level 100).</summary>
    public static double Gcd(int speed)
    {
        var reduction = 130 * (speed - SubBase) / Divisor;
        return Math.Floor(Math.Floor((1000.0 - reduction) * 2500 / 1000) / 10) / 100;
    }

    /// <summary>Whether a job's global cooldown follows spell speed (casters and healers) rather than skill speed.</summary>
    public static bool UsesSpellSpeed(string job) =>
        job is "WHM" or "SCH" or "AST" or "SGE" or "BLM" or "SMN" or "RDM" or "PCT" or "BLU";

    /// <summary>The main stat of a job.</summary>
    public static uint MainStat(string job) => job switch
    {
        "PLD" or "WAR" or "DRK" or "GNB" or "MNK" or "DRG" or "SAM" or "RPR" => Strength,
        "NIN" or "VPR" or "BRD" or "MCH" or "DNC" => Dexterity,
        "WHM" or "SCH" or "AST" or "SGE" => Mind,
        _ => Intelligence,
    };

    /// <summary>Forget the names (the language changed).</summary>
    public static void Reset() => Names.Clear();
}
