using System.Collections.Generic;

namespace DpsInServerBar.UI;

/// <summary>
/// Names of actions and statuses in the plugin language (<see cref="Loc.GameLanguage"/>), read from the game data.
/// Pulls store names in the client language at record time; showing them by id puts them in the plugin language.
/// </summary>
public static class GameNames
{
    private static readonly Dictionary<uint, string> Actions = new();
    private static readonly Dictionary<uint, string> Statuses = new();

    static GameNames() => Loc.Changed += () =>
    {
        lock (Actions)
            Actions.Clear();
        lock (Statuses)
            Statuses.Clear();
    };

    /// <summary>Name of an action, else <paramref name="fallback"/> (the name saved with the pull).</summary>
    public static string Action(uint id, string fallback)
    {
        if (id == 0)
            return fallback;

        lock (Actions)
        {
            if (!Actions.TryGetValue(id, out var name))
            {
                name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(Loc.GameLanguage).TryGetRow(id, out var row)
                           ? row.Name.ExtractText()
                           : string.Empty;
                Actions[id] = name;
            }

            return Loc.Game(name.Length > 0 ? name : fallback);
        }
    }

    /// <summary>
    /// A name saved as a key (see <see cref="ActionKey"/>...) in the plugin language, else <paramref name="fallback"/>
    /// (the name saved with the pull, for pulls recorded before keys existed).
    /// </summary>
    public static string Resolve(string key, string fallback)
    {
        if (key.Length == 0)
            return fallback;

        var colon = key.IndexOf(':');
        var kind = colon < 0 ? key : key[..colon];
        var id = colon >= 0 && uint.TryParse(key[(colon + 1)..], out var parsed) ? parsed : 0u;
        return kind switch
        {
            "a" => Action(id, fallback),
            "s" => Status(id, fallback),
            "dot" => Loc.F("{0} (DoT)", Status(id, Loc.T("damage over time"))),
            "debuff" => Loc.F("{0} (debuff)", Status(id, fallback)),
            "auto" => Loc.T("Auto-attack"),
            "fall" => Loc.T("falling off the arena"),
            "instant" => Loc.T("instant death (arena edge?)"),
            _ => fallback,
        };
    }

    public static string ActionKey(uint id) => $"a:{id}";

    /// <summary>Name of a status, else <paramref name="fallback"/> (the name saved with the pull).</summary>
    public static string Status(uint id, string fallback)
    {
        if (id == 0)
            return fallback;

        lock (Statuses)
        {
            if (!Statuses.TryGetValue(id, out var name))
            {
                name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>(Loc.GameLanguage).TryGetRow(id, out var row)
                           ? row.Name.ExtractText()
                           : string.Empty;
                Statuses[id] = name;
            }

            return Loc.Game(name.Length > 0 ? name : fallback);
        }
    }
}
