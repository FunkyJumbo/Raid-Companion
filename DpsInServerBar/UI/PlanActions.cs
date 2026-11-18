using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using DpsInServerBar.Tracking;

namespace DpsInServerBar.UI;

/// <summary>
/// The actions of <see cref="MitigationCatalog"/> in the game data: ids by English name (PvE player and role actions),
/// and for each id, the ids of its whole family (all its levels). Built once, on the game thread.
/// </summary>
public static class PlanActions
{
    private static Dictionary<string, uint>? byName;
    private static Dictionary<uint, uint[]>? families;

    /// <summary>The id of an action of the catalog by its English name, 0 when the game has none.</summary>
    public static uint Id(string englishName)
    {
        Build();
        return byName!.GetValueOrDefault(englishName);
    }

    /// <summary>The ids that count as this action: all the levels of its family (itself when not in the catalog).</summary>
    public static IReadOnlyCollection<uint> Family(uint actionId)
    {
        Build();
        return families!.TryGetValue(actionId, out var f) ? f : [actionId];
    }

    private static readonly Dictionary<(PlanRole, string), HashSet<uint>> offered = new();

    /// <summary>The ids (all levels) of the skills a column offers for this role and job.</summary>
    public static IReadOnlySet<uint> Offered(PlanRole role, string job)
    {
        if (!offered.TryGetValue((role, job), out var ids))
        {
            ids = MitigationCatalog.For(role, job).SelectMany(g => g.Families).SelectMany(f => f)
                                   .Select(Id).Where(id => id != 0).SelectMany(Family).ToHashSet();
            offered[(role, job)] = ids;
        }

        return ids;
    }

    private static Dictionary<string, uint>? jobIds;

    /// <summary>The framed icon of a job (by its English abbreviation), 0 when unknown.</summary>
    public static uint JobIcon(string job)
    {
        jobIds ??= Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>(ClientLanguage.English)
                         .Where(j => j.RowId != 0)
                         .GroupBy(j => j.Abbreviation.ExtractText(), StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.First().RowId, StringComparer.OrdinalIgnoreCase);
        return jobIds.TryGetValue(job, out var id) ? 62100 + id : 0;
    }

    private static void Build()
    {
        if (byName is not null)
            return;

        var wanted = MitigationCatalog.Families.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English);
        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            if (row.IsPvP || !(row.IsPlayerAction || row.IsRoleAction) || !wanted.Contains(name) || byName.ContainsKey(name))
                continue;
            byName[name] = row.RowId;
        }

        // Upgraded actions (Holy Sheltron, Damnation...) are not always "player actions" in the data: any PvE job action.
        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            if (!row.IsPvP && row.ClassJobLevel > 0 && row.ClassJobCategory.RowId != 0 && wanted.Contains(name) && !byName.ContainsKey(name))
                byName[name] = row.RowId;
        }

        families = new Dictionary<uint, uint[]>();
        foreach (var family in MitigationCatalog.Families.Values.Distinct())
        {
            var ids = family.Select(n => byName.GetValueOrDefault(n)).Where(id => id != 0).ToArray();
            foreach (var id in ids)
                families[id] = ids;
        }
    }
}
