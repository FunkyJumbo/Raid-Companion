using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DpsInServerBar.Tracking;

/// <summary>Kind of content a pull happened in, used to filter the history. Several can be selected at once.</summary>
[Flags]
public enum ContentCategory
{
    None = 0,
    Dungeon = 1,
    Trial = 2,
    Extreme = 4,
    Raid = 8,
    Savage = 16,
    Ultimate = 32,
    Other = 64,
    Criterion = 128,
    All = Dungeon | Trial | Extreme | Raid | Savage | Ultimate | Other | Criterion,

    /// <summary>The content with a real progression: shown in the Progression tab.</summary>
    Progression = Extreme | Savage | Ultimate | Criterion,
}

/// <summary>
/// Classifies a territory from the game data: its duty's ContentType (dungeons, trials, raids, ultimates...),
/// and its Duty Finder section for the difficulty, which is not a content type of its own (extreme, savage).
/// </summary>
public sealed class ContentCategories
{
    // ContentType sheet rows.
    private const uint Dungeons = 2;
    private const uint Trials = 4;
    private const uint Raids = 5;
    private const uint UltimateRaids = 28;
    private const uint VariantAndCriterionDungeons = 30;
    private const uint ChaoticAllianceRaid = 37;

    /// <summary>The kinds of content, hardest first: the order of the filters and of the fights in the lists.</summary>
    public static (ContentCategory Category, string Label)[] Labels =>
    [
        (ContentCategory.Ultimate, Loc.T("Ultimate")),
        (ContentCategory.Savage, Loc.T("Savage")),
        (ContentCategory.Extreme, Loc.T("Extreme")),
        (ContentCategory.Criterion, Loc.T("Criterion")),
        (ContentCategory.Raid, Loc.T("Raids")),
        (ContentCategory.Trial, Loc.T("Trials")),
        (ContentCategory.Dungeon, Loc.T("Dungeons")),
        (ContentCategory.Other, Loc.T("Other")),
    ];

    /// <summary>Position of a kind of content in <see cref="Labels"/>.</summary>
    public static int Order(ContentCategory category) => Array.FindIndex(Labels, l => l.Category == category) is var i and >= 0 ? i : Labels.Length;

    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, ContentCategory> cache = new();

    private readonly Dictionary<uint, (uint Expansion, uint Release)> releases = new();

    public ContentCategories(IDataManager dataManager) => this.dataManager = dataManager;

    /// <summary>
    /// The expansion of a territory (0: A Realm Reborn ... 5: Dawntrail) and its place among the duties of the game
    /// (higher: released later): its expansion, then its duty's order in the Duty Finder.
    /// </summary>
    public (uint Expansion, uint Release) ReleaseOf(uint territory)
    {
        if (!releases.TryGetValue(territory, out var release))
        {
            release = dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)
                          ? (row.ExVersion.RowId,
                             row.ContentFinderCondition.ValueNullable is { } duty ? (uint)duty.SortKey : territory)
                          : (0u, territory);
            releases[territory] = release;
        }

        return release;
    }

    private readonly Dictionary<(uint, string, string), string?> displayNames = new();
    private readonly Dictionary<uint, List<uint>> savageFloors = new();

    // The letter of each expansion's savage floors (Heavensward: A1S, Stormblood: O1S, ... Dawntrail: M1S).
    private static readonly Dictionary<uint, char> SavageLetters = new() { [1] = 'A', [2] = 'O', [3] = 'E', [4] = 'P', [5] = 'M' };

    private static readonly System.Text.RegularExpressions.Regex TrailingParenthesis =
        new(@"\s*[\(（][^\(（]*[\)）]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The name of a fight as the windows show it, by kind of content: "The Jade Stoa Unreal (Byakko)", "The Unmaking
    /// EX (Enuo)", "AAC Heavyweight M4 (M12S)", "The Epic of Alexander (TEA)". Null for the other kinds (shown as before).
    /// </summary>
    /// <param name="encounter">The fight's key (<see cref="Progression.Encounter"/>): the boss, or the ultimate's name.</param>
    public string? DisplayName(uint territory, string encounter)
    {
        var key = (territory, encounter, Loc.Code);
        if (!displayNames.TryGetValue(key, out var name))
        {
            name = BuildName(territory, encounter);
            displayNames[key] = name;
        }

        return name;
    }

    private string? BuildName(uint territory, string encounter)
    {
        if (Ultimates.UltimateTracker.FightFor(territory) is { } ultimate)
            return $"{ultimate.Name} ({ultimate.Key})";

        var category = Of(territory);
        if (category is not (ContentCategory.Extreme or ContentCategory.Savage)
            || !dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)
            || row.ContentFinderCondition.ValueNullable is not { } duty)
            return null;

        // The duty's name without its "(Extreme)", "(Unreal)", "(Savage)".
        var zone = TrailingParenthesis.Replace(duty.Name.ExtractText(), string.Empty).Trim();
        if (zone.Length == 0)
            return null;
        zone = char.ToUpperInvariant(zone[0]) + zone[1..];

        if (category == ContentCategory.Extreme)
        {
            var english = dataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English)
                                     .TryGetRow(duty.RowId, out var en) ? en.Name.ExtractText() : string.Empty;
            var kind = english.Contains("(Unreal)", StringComparison.OrdinalIgnoreCase) ? "Unreal" : "EX";
            return encounter.Length > 0 ? $"{zone} {kind} ({encounter})" : $"{zone} {kind}";
        }

        return SavageFloor(row.ExVersion.RowId, duty.RowId) is { } floor ? $"{zone} ({floor})" : zone;
    }

    // "M12S": the floor's letter for its expansion and its place among the expansion's savage floors, in the Duty
    // Finder's order. Null for an expansion without a letter or a duty not found (alliance raids).
    private string? SavageFloor(uint expansion, uint duty)
    {
        if (!SavageLetters.TryGetValue(expansion, out var letter))
            return null;

        if (!savageFloors.TryGetValue(expansion, out var floors))
        {
            var sections = dataManager.GetExcelSheet<ContentUICategory>(ClientLanguage.English);
            floors = dataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English)
                                .Where(d => d.ContentType.RowId == Raids
                                            && d.TerritoryType.ValueNullable is { } t && t.ExVersion.RowId == expansion
                                            && sections.TryGetRow(d.ContentUICategory.RowId, out var ui)
                                            && ui.Name.ExtractText().StartsWith("Savage", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(d => d.SortKey).ThenBy(d => d.RowId)
                                .Select(d => d.RowId)
                                .ToList();
            savageFloors[expansion] = floors;
        }

        var index = floors.IndexOf(duty);
        return index < 0 ? null : $"{letter}{index + 1}S";
    }

    /// <summary>The expansions, by id, with their name in the client's language.</summary>
    public IReadOnlyList<(uint Id, string Name)> Expansions() =>
        dataManager.GetExcelSheet<ExVersion>().Select(e => (e.RowId, e.Name.ExtractText())).Where(e => e.Item2.Length > 0).ToList();

    public ContentCategory Of(uint territory)
    {
        if (!cache.TryGetValue(territory, out var category))
        {
            category = Classify(territory);
            cache[territory] = category;
        }

        return category;
    }

    private ContentCategory Classify(uint territory)
    {
        if (!dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English).TryGetRow(territory, out var row)
            || row.ContentFinderCondition.ValueNullable is not { } duty)
        {
            return ContentCategory.Other;
        }

        // The Duty Finder section of the duty tells the difficulty: "High-end Trials (Dawntrail)" holds the extremes
        // and unreals (with "The Minstrel's Ballad: ..." ones), "Savage Raids (...)" the savage raids. Read in English.
        var section = dataManager.GetExcelSheet<ContentUICategory>(ClientLanguage.English)
                                 .TryGetRow(duty.ContentUICategory.RowId, out var ui) ? ui.Name.ExtractText() : string.Empty;
        var name = duty.Name.ExtractText();
        return duty.ContentType.RowId switch
        {
            // Criterion dungeons ("Another ..."), normal and savage, apart from the variant ones (no section for them).
            VariantAndCriterionDungeons when name.StartsWith("Another ", StringComparison.OrdinalIgnoreCase) => ContentCategory.Criterion,
            Dungeons or VariantAndCriterionDungeons => ContentCategory.Dungeon,
            Trials when section.StartsWith("High-end", StringComparison.OrdinalIgnoreCase) => ContentCategory.Extreme,
            Trials => ContentCategory.Trial,
            Raids when section.StartsWith("Savage", StringComparison.OrdinalIgnoreCase) => ContentCategory.Savage,
            ChaoticAllianceRaid => ContentCategory.Savage,
            Raids => ContentCategory.Raid,
            UltimateRaids => ContentCategory.Ultimate,
            _ => ContentCategory.Other,
        };
    }
}
