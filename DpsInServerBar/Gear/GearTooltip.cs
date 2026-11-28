using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using DpsInServerBar.UI;

namespace DpsInServerBar.Gear;

/// <summary>
/// The tooltip of an item, like the game's: icon, name, item level, weapon damage, stats; for a piece of a set, its
/// materia and what each one adds (what the meld cap cuts in red); for a materia its stat, for a meal its bonuses.
/// </summary>
public static class GearTooltip
{
    /// <summary>Shows an item's tooltip (call when the item is hovered).</summary>
    public static void Show(uint itemId, GearPiece? piece = null, OwnedItem? owned = null)
    {
        if (GearCatalog.Item(itemId) is not { } item)
            return;

        using var tooltip = ImRaii.Tooltip();
        var icon = ImGui.GetTextLineHeight() * 2.2f;
        ActionIcons.Draw(item.Icon, icon);
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextColored(RarityColor(item.Rarity), item.Name);
            if (item.ItemLevel > 1)
                ImGui.TextColored(Theme.TextMuted, Loc.F("Item level {0}", item.ItemLevel));
        }

        // A materia: its stat.
        if (GearStats.OfMateria(itemId) is { } materia)
        {
            ImGui.Separator();
            StatLine(materia.Param, materia.Value);
            return;
        }

        // A meal: its bonuses.
        var food = GearStats.OfFood(itemId);
        if (food.Count > 0)
        {
            ImGui.Separator();
            foreach (var f in food)
            {
                if (f.Relative)
                    ImGui.TextUnformatted(Loc.F("{0} +{1}% (max {2})", GearStats.Name(f.Param), f.Percent, f.Max));
                else
                    StatLine(f.Param, f.Percent);
            }

            return;
        }

        // Gear.
        var (stats, damage) = GearStats.OfItem(itemId);
        if (damage > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(Theme.Gold, Loc.F("Weapon damage {0}", damage));
        }

        // The secondary stats only (the main stats and vitality left out).
        var secondary = stats.Where(s => GearStats.Secondary.Contains(s.Param))
                             .OrderBy(s => System.Array.IndexOf(GearStats.Secondary, s.Param))
                             .ToList();
        if (secondary.Count > 0)
        {
            ImGui.Separator();
            foreach (var s in secondary)
                StatLine(s.Param, s.Value);
        }

        // A relic whose stats the player sets: the ones the set wants.
        if (piece is not null && GearStats.IsCustomRelic(itemId))
        {
            ImGui.Separator();
            var wanted = piece.Relic.Where(r => r.Value > 0)
                              .OrderBy(r => System.Array.IndexOf(GearStats.Secondary, r.Key))
                              .ToList();
            if (wanted.Count == 0)
            {
                ImGui.TextColored(Theme.TextFaint, Loc.T("Relic stats: none chosen for this set (Edit)."));
            }
            else
            {
                ImGui.TextColored(Theme.Gold, Loc.T("Relic stats of the set"));
                foreach (var (param, value) in wanted)
                    StatLine(param, value);
            }
        }

        if (item.MateriaSlots > 0 || item.Overmeld)
        {
            ImGui.Separator();
            ImGui.TextColored(Theme.TextMuted, item.Overmeld
                                                   ? Loc.F("{0} materia slots, overmeld up to 5", item.MateriaSlots)
                                                   : Loc.F("{0} materia slots", item.MateriaSlots));
            var cap = GearStats.MeldCap(itemId);
            if (cap > 0)
                ImGui.TextColored(Theme.TextFaint, Loc.F("Meld cap: {0} per secondary stat", cap));
        }

        // A piece of a set: its materia, what they add, and which ones your copy holds.
        if (piece is not null && piece.Materia.Any(m => m != 0))
        {
            ImGui.Separator();
            var melds = GearStats.Melds(piece);
            // The melded flag of each planned materia, in the order of the melds (the materia slots holding one).
            var flags = GearInventory.Melded(piece, owned);
            var planned = Enumerable.Range(0, piece.Materia.Count).Where(i => GearStats.OfMateria(piece.Materia[i]) is not null).ToList();
            var size = ImGui.GetTextLineHeight();
            for (var i = 0; i < melds.Count; i++)
            {
                var (id, gives, wasted) = melds[i];
                ActionIcons.Draw(GearCatalog.Item(id)?.Icon ?? 0, size);
                ImGui.SameLine();
                ImGui.TextUnformatted($"{GearStats.Name(gives.Param)} +{gives.Value}");
                if (wasted > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Bad, Loc.F("({0} over the cap)", wasted));
                }

                if (owned is not null)
                {
                    var done = i < planned.Count && flags[planned[i]];
                    ImGui.SameLine();
                    ImGui.TextColored(done ? Theme.Good : Theme.TextFaint, done ? Loc.T("Melded") : Loc.T("Not melded"));
                }
            }
        }

        if (piece is { Note.Length: > 0 })
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(Theme.Px(360));
            ImGui.TextColored(Theme.TextMuted, piece.Note);
            ImGui.PopTextWrapPos();
        }
    }

    private static void StatLine(uint param, int value)
    {
        ImGui.TextUnformatted(GearStats.Name(param));
        ImGui.SameLine(Theme.Px(170));
        ImGui.TextColored(Theme.Good, $"+{value}");
    }

    /// <summary>An item's name color by rarity, as in the game.</summary>
    public static Vector4 RarityColor(int rarity) => rarity switch
    {
        2 => new Vector4(0.75f, 1f, 0.75f, 1f),
        3 => new Vector4(0.36f, 0.6f, 1f, 1f),
        4 => new Vector4(0.71f, 0.53f, 0.94f, 1f),
        7 => new Vector4(0.98f, 0.55f, 0.75f, 1f),
        _ => Theme.Text,
    };
}
