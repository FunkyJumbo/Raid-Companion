using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace DpsInServerBar.Gear;

/// <summary>
/// Lights the items of a gear set in the game's own windows, like the game lights a slot: in your inventory, armoury
/// chest, saddlebag and retainer, the pieces of the set, its food and the materia still to meld (green); in the shops and
/// the loot window (gold for what you lack, green for what you have). In the bag, the saddlebag and a retainer, each slot
/// is read for the item it really holds: the game shows them in the order you arranged them, kept by its item order
/// module (the way BisBuddy reads them, thanks to it and haselnussbomber); an exchange shop is read from its list of
/// items. Elsewhere (armoury chest) by the slot's icon, when no other item you own shares it; in other shops and the loot
/// by the names shown. The windows keep their order: another window over them hides it. Each node gets its colors back
/// as soon as it shows something else, and all of them when turned off. Game thread only (framework update).
/// </summary>
public sealed unsafe class GearHighlighter : IDisposable
{
    // Windows showing items in slots, read by their icon (none left: every one is read through the item order).
    private static readonly string[] GridWindows = [];

    // Windows listing items by name: shops, the loot.
    private static readonly string[] ListWindows =
    [
        "NeedGreed", "Shop", "ShopExchangeCoin", "InclusionShop", "FreeShop", "GrandCompanyExchange",
    ];

    // Exchange shops (tomestones, raid tokens): their item list (node), the count and item ids in their values, and the
    // list telling which items the filter shows (visible up to 1).
    private static readonly (string Window, int CountIndex)[] ExchangeShops = [("ShopExchangeItem", 3), ("ShopExchangeCurrency", 4)];
    private const uint ExchangeListNode = 20;
    private const int ExchangeItemIds = 1066, ExchangeFilter = 1554;

    // Slots: a tint added to the node's colors. Names: the text's color.
    private static readonly (short R, short G, short B) SlotTint = (-30, 90, -10);
    private static readonly (short R, short G, short B) LackTint = (90, 55, -40);
    private static readonly ByteColor HaveText = new() { R = 120, G = 230, B = 130, A = 255 };
    private static readonly ByteColor LackText = new() { R = 255, G = 200, B = 70, A = 255 };

    private readonly IGameGui gameGui;
    private readonly Func<IEnumerable<GearSet>> sets;
    private readonly Func<bool> enabled;

    // What to light: item id to whether you have it. Refreshed every second.
    private readonly Dictionary<uint, bool> targets = new();
    private readonly List<(string Name, bool Have)> names = [];
    // Icons of what you own, with the items behind each (to light a slot by its icon only when no other item shares it).
    private readonly Dictionary<uint, HashSet<uint>> ownedIcons = new();
    private DateTime nextTargets = DateTime.MinValue;

    // Nodes changed, with their own colors and their window (to know it still exists before touching them again).
    private readonly Dictionary<nint, (string Window, short R, short G, short B)> tintedSlots = new();
    private readonly Dictionary<nint, (string Window, ByteColor Color)> tintedTexts = new();
    private readonly HashSet<nint> seen = [];

    public GearHighlighter(IGameGui gameGui, Func<IEnumerable<GearSet>> sets, Func<bool> enabled)
    {
        this.gameGui = gameGui;
        this.sets = sets;
        this.enabled = enabled;
    }

    public void Update()
    {
        seen.Clear();
        if (enabled() && !gameGui.GameUiHidden)
        {
            RefreshTargets();
            if (targets.Count > 0)
            {
                LightBags();

                foreach (var window in GridWindows)
                {
                    var addon = Visible(window);
                    if (addon != null)
                        Walk(window, &addon->UldManager, grid: true);
                }

                foreach (var (window, countIndex) in ExchangeShops)
                {
                    var addon = Visible(window);
                    if (addon != null)
                        LightExchange(window, addon, countIndex);
                }

                foreach (var window in ListWindows)
                {
                    var addon = Visible(window);
                    if (addon != null)
                        Walk(window, &addon->UldManager, grid: false);
                }
            }
        }

        RestoreUnseen();
    }

    public void Dispose()
    {
        seen.Clear();
        RestoreUnseen();
    }

    private AtkUnitBase* Addon(string name) => (AtkUnitBase*)gameGui.GetAddonByName(name).Address;

    private AtkUnitBase* Visible(string name)
    {
        var addon = Addon(name);
        return addon != null && addon->IsVisible ? addon : null;
    }

    /// <summary>
    /// What a set asks for: its pieces, its food, and the materia not melded yet into the pieces you own (or into the
    /// ones still to get), each with whether you have it.
    /// </summary>
    public static Dictionary<uint, bool> Wanted(IEnumerable<GearSet> sets)
    {
        var owned = GearInventory.Owned;
        var result = new Dictionary<uint, bool>();
        foreach (var set in sets)
        {
            var copies = GearInventory.Match(set);
            foreach (var (slot, piece) in set.Pieces.Where(p => p.Value.ItemId != 0))
            {
                result[piece.ItemId] = copies.GetValueOrDefault(slot) is not null;
                var melded = GearInventory.Melded(piece, copies.GetValueOrDefault(slot));
                for (var i = 0; i < piece.Materia.Count; i++)
                {
                    if (piece.Materia[i] != 0 && !melded[i])
                        result[piece.Materia[i]] = owned.ContainsKey(piece.Materia[i]);
                }
            }

            if (set.Food != 0)
                result[set.Food] = owned.ContainsKey(set.Food);
        }

        return result;
    }

    private void RefreshTargets()
    {
        if (DateTime.UtcNow < nextTargets)
            return;
        nextTargets = DateTime.UtcNow.AddSeconds(1);

        targets.Clear();
        foreach (var (id, have) in Wanted(sets()))
            targets[id] = have;

        names.Clear();
        foreach (var (id, have) in targets)
        {
            var name = GearCatalog.GameName(id);
            if (name.Length > 0)
                names.Add((name, have));
        }

        ownedIcons.Clear();
        foreach (var id in GearInventory.Owned.Keys)
        {
            if (GearCatalog.Item(id) is { Icon: > 0 } item)
            {
                if (!ownedIcons.TryGetValue(item.Icon, out var items))
                    ownedIcons[item.Icon] = items = [];
                items.Add(id);
            }
        }
    }

    // ---- The bag, the saddlebag, a retainer: read through the game's item order.

    private void LightBags()
    {
        var order = ItemOrderModule.Instance();
        if (order == null)
            return;

        LightArmoury(order);

        // Bag: expanded (four pages), large (two pages a tab), normal (one page a tab).
        var expanded = Visible("InventoryExpansion");
        if (expanded != null)
            LightView(order->InventorySorter, ((AddonInventoryExpansion*)expanded)->TabIndex, 4, ["InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E"]);
        var large = Visible("InventoryLarge");
        if (large != null)
            LightView(order->InventorySorter, ((AddonInventoryLarge*)large)->TabIndex, 2, ["InventoryGrid0", "InventoryGrid1"]);
        var normal = Visible("Inventory");
        if (normal != null)
            LightView(order->InventorySorter, ((AddonInventory*)normal)->TabIndex, 1, ["InventoryGrid"]);

        // Saddlebag: its own slots, the normal or premium bag by its tab.
        var buddy = Visible("InventoryBuddy");
        if (buddy != null)
        {
            var bag = (AddonInventoryBuddy*)buddy;
            var sorter = bag->TabIndex == 0 ? order->SaddleBagSorter : order->PremiumSaddleBagSorter;
            LightSlots("InventoryBuddy", sorter, 0, Slots(bag->Slots));
        }

        // Retainer: large (two pages a tab) or normal (one page a tab).
        if (order->RetainerSorter.TryGetValuePointer(order->ActiveRetainerId, out var retainerSorter) && retainerSorter != null)
        {
            var sorter = retainerSorter->Value;
            var retainerLarge = Visible("InventoryRetainerLarge");
            if (retainerLarge != null)
            {
                var tab = ((AddonInventoryRetainerLarge*)retainerLarge)->TabIndex;
                string[] grids = ["RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4"];
                LightView(sorter, tab, 2, grids.Skip(tab * 2).Take(2).ToArray());
            }

            var retainer = Visible("InventoryRetainer");
            if (retainer != null)
                LightView(sorter, ((AddonInventoryRetainer*)retainer)->TabIndex, 1, ["RetainerGrid"]);
        }
    }

    // The armoury chest: the slots of the category shown, in reading order, read through that category's item order. The
    // category is the one whose items the slots show (their icons), so the order of its tabs need not be known.
    private void LightArmoury(ItemOrderModule* order)
    {
        var board = Visible("ArmouryBoard");
        if (board == null)
            return;

        var found = new List<(nint Node, nint Slot, float X, float Y)>();
        Collect(&board->UldManager, found);
        var slots = found.OrderBy(f => MathF.Round(f.Y / 4f)).ThenBy(f => f.X).Select(f => f.Slot).ToList();
        if (slots.Count == 0)
            return;

        ItemOrderModuleSorter*[] sorters =
        [
            order->ArmouryMainHandSorter, order->ArmouryOffHandSorter, order->ArmouryHeadSorter, order->ArmouryBodySorter,
            order->ArmouryHandsSorter, order->ArmouryLegsSorter, order->ArmouryFeetSorter, order->ArmouryEarsSorter,
            order->ArmouryNeckSorter, order->ArmouryWristsSorter, order->ArmouryRingsSorter,
        ];

        ItemOrderModuleSorter* best = null;
        var bestScore = 0;
        foreach (var sorter in sorters)
        {
            var score = Matches(sorter, slots);
            if (score > bestScore)
            {
                best = sorter;
                bestScore = score;
            }
        }

        if (best != null)
            LightSlots("ArmouryBoard", best, 0, slots);
    }

    // How many slots show the item at their place in an item order (by its icon).
    private static int Matches(ItemOrderModuleSorter* sorter, List<nint> slots)
    {
        var manager = InventoryManager.Instance();
        if (sorter == null || manager == null)
            return 0;

        var score = 0;
        var count = (int)sorter->Items.LongCount;
        for (var i = 0; i < slots.Count && i < count; i++)
        {
            var slot = (AtkComponentDragDrop*)slots[i];
            var icon = slot->AtkComponentIcon != null ? (uint)slot->AtkComponentIcon->IconId % 1_000_000 : 0;
            if (icon == 0)
                continue;
            var entry = sorter->Items[i].Value;
            var container = entry != null ? manager->GetInventoryContainer(sorter->InventoryType + entry->Page) : null;
            var item = container != null && container->IsLoaded ? container->GetInventorySlot(entry->Slot) : null;
            if (item != null && item->ItemId != 0 && GearCatalog.Item(item->ItemId % 1_000_000)?.Icon == icon)
                score++;
        }

        return score;
    }

    // Every visible slot (drag and drop component) of a window, with its place on screen.
    private static void Collect(AtkUldManager* uld, List<(nint Node, nint Slot, float X, float Y)> slots)
    {
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !node->IsVisible() || (int)node->Type < 1000)
                continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null)
                continue;
            if (component->GetComponentType() == ComponentType.DragDrop)
                slots.Add(((nint)node, (nint)component, node->ScreenX, node->ScreenY));
            else
                Collect(&component->UldManager, slots);
        }
    }

    private static List<nint> Slots(Span<Pointer<AtkComponentDragDrop>> slots)
    {
        var list = new List<nint>();
        foreach (var slot in slots)
        {
            if (slot.Value != null)
                list.Add((nint)slot.Value);
        }

        return list;
    }

    // A view of pages: its grids' slots one after the other are the pages shown by the tab, in the order kept.
    private void LightView(ItemOrderModuleSorter* sorter, int tab, int pages, string[] grids)
    {
        if (sorter == null || tab < 0)
            return;

        var slots = new List<nint>();
        foreach (var name in grids)
        {
            var grid = Addon(name);
            if (grid != null && grid->IsVisible)
                slots.AddRange(Slots(((AddonInventoryGrid*)grid)->Slots));
        }

        LightSlots(grids.Length > 0 ? grids[0] : string.Empty, sorter, tab * pages * PageSize, slots);
    }

    private const int PageSize = 35;

    // Each slot shown (from the first place shown on): the item at that place in the order kept, lit when wanted.
    private void LightSlots(string window, ItemOrderModuleSorter* sorter, int first, List<nint> slots)
    {
        if (sorter == null)
            return;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return;

        var count = (int)sorter->Items.LongCount;
        for (var i = 0; i < slots.Count && first + i < count; i++)
        {
            var node = (AtkResNode*)((AtkComponentDragDrop*)slots[i])->OwnerNode;
            if (node == null || !node->IsVisible())
                continue;

            var entry = sorter->Items[first + i].Value;
            if (entry == null)
                continue;
            var container = manager->GetInventoryContainer(sorter->InventoryType + entry->Page);
            if (container == null || !container->IsLoaded)
                continue;
            var item = container->GetInventorySlot(entry->Slot);
            if (item != null && item->ItemId != 0 && targets.ContainsKey(item->ItemId % 1_000_000))
                TintSlot(window, node);
        }
    }

    // ---- Exchange shops: their list of items.

    private void LightExchange(string window, AtkUnitBase* addon, int countIndex)
    {
        var values = addon->AtkValuesSpan;
        if (values.Length <= countIndex)
            return;
        var count = values[countIndex].Int;
        if (count <= 0 || values.Length <= ExchangeFilter + count)
            return;

        // The rows shown (the filter hides some): row index to item.
        var rows = new Dictionary<int, uint>();
        var row = 0;
        for (var i = 0; i < count; i++)
        {
            if (values[ExchangeFilter + i].Type != AtkValueType.UInt || values[ExchangeFilter + i].UInt > 1)
                continue;
            if (values[ExchangeItemIds + i].Type == AtkValueType.UInt)
                rows[row] = values[ExchangeItemIds + i].UInt;
            row++;
        }

        var listNode = addon->GetNodeById(ExchangeListNode);
        if (listNode == null || (int)listNode->Type < 1000)
            return;
        var list = (AtkComponentTreeList*)((AtkComponentNode*)listNode)->Component;
        if (list == null)
            return;

        for (var i = 0; i < list->Items.LongCount; i++)
        {
            var renderer = list->Items[i].Value != null ? list->Items[i].Value->Renderer : null;
            if (renderer == null || renderer->OwnerNode == null)
                continue;
            var node = (AtkResNode*)renderer->OwnerNode;
            if (!node->IsVisible() || !rows.TryGetValue(renderer->ListItemIndex, out var id))
                continue;
            // A shop lights only what you still lack.
            if (targets.TryGetValue(id % 1_000_000, out var have) && !have)
                TintSlot(window, node, LackTint);
        }
    }

    private void Walk(string window, AtkUldManager* uld, bool grid)
    {
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !node->IsVisible())
                continue;

            if ((int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component == null)
                    continue;

                if (grid && component->GetComponentType() == ComponentType.DragDrop)
                {
                    if (SlotItem((AtkComponentDragDrop*)component) is { } id && targets.ContainsKey(id))
                        TintSlot(window, node);
                }
                else
                {
                    Walk(window, &component->UldManager, grid);
                }
            }
            else if (!grid && node->Type == NodeType.Text)
            {
                var text = new ReadOnlySeStringSpan(((AtkTextNode*)node)->NodeText.AsSpan()).ExtractText().Trim();
                // A shop or the loot lights only what you still lack.
                if (text.Length > 0 && names.Any(n => !n.Have && SameName(text, n.Name)))
                    TintText(window, (AtkTextNode*)node, LackText);
            }
        }
    }

    // The item a slot holds: read from its container and place; else from its icon, when only one item you own has it.
    private uint? SlotItem(AtkComponentDragDrop* slot)
    {
        // The icon shown (the icon component's: it is the one the game draws).
        var icon = slot->AtkComponentIcon != null ? (uint)slot->AtkComponentIcon->IconId : 0;
        if (icon == 0)
            return null;
        var baseIcon = icon % 1_000_000; // high quality icons are offset

        var payload = slot->AtkDragDropInterface.GetPayloadContainer();
        if (payload != null && payload->Int1 >= 0 && payload->Int2 >= 0)
        {
            var manager = InventoryManager.Instance();
            var container = manager != null ? manager->GetInventoryContainer((InventoryType)payload->Int1) : null;
            if (container != null && container->IsLoaded && payload->Int2 < container->Size)
            {
                var item = container->GetInventorySlot(payload->Int2);
                if (item != null && item->ItemId != 0)
                {
                    var id = item->ItemId % 1_000_000;
                    // The place read must hold what the slot shows (same icon); else it means something else.
                    if (GearCatalog.Item(id)?.Icon == baseIcon)
                        return id;
                }
            }
        }

        return ownedIcons.TryGetValue(baseIcon, out var items) && items.Count == 1 ? items.First() : null;
    }

    // A name in a list: the same, or cut with "..." in a narrow column.
    private static bool SameName(string text, string name)
    {
        if (text.Equals(name, StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Length >= 6 && text.Contains(name, StringComparison.OrdinalIgnoreCase))
            return true;
        var cut = text.TrimEnd('.', '…', ' ');
        return cut.Length >= 10 && cut.Length < text.Length && name.StartsWith(cut, StringComparison.OrdinalIgnoreCase);
    }

    private void TintSlot(string window, AtkResNode* node) => TintSlot(window, node, SlotTint);

    private void TintSlot(string window, AtkResNode* node, (short R, short G, short B) tint)
    {
        var key = (nint)node;
        seen.Add(key);
        if (!tintedSlots.ContainsKey(key))
            tintedSlots[key] = (window, node->AddRed, node->AddGreen, node->AddBlue);
        var own = tintedSlots[key];
        SetAdd(node, (short)(own.R + tint.R), (short)(own.G + tint.G), (short)(own.B + tint.B));
    }

    private void TintText(string window, AtkTextNode* node, ByteColor color)
    {
        var key = (nint)node;
        seen.Add(key);
        if (!tintedTexts.ContainsKey(key))
            tintedTexts[key] = (window, node->TextColor);
        node->TextColor = color;
    }

    // The nodes changed that no longer show a set item get their colors back; a closed window's nodes are gone with it.
    private void RestoreUnseen()
    {
        foreach (var (key, own) in tintedSlots.Where(t => !seen.Contains(t.Key)).ToList())
        {
            tintedSlots.Remove(key);
            if (Addon(own.Window) != null)
                SetAdd((AtkResNode*)key, own.R, own.G, own.B);
        }

        foreach (var (key, own) in tintedTexts.Where(t => !seen.Contains(t.Key)).ToList())
        {
            tintedTexts.Remove(key);
            if (Addon(own.Window) != null)
                ((AtkTextNode*)key)->TextColor = own.Color;
        }
    }

    private static void SetAdd(AtkResNode* node, short r, short g, short b)
    {
        node->AddRed = node->AddRed_2 = r;
        node->AddGreen = node->AddGreen_2 = g;
        node->AddBlue = node->AddBlue_2 = b;
        node->DrawFlags |= 0x1; // redraw
    }
}
