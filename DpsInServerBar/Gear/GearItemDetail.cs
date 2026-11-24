using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DpsInServerBar.Gear;

/// <summary>
/// Names the gear sets an item belongs to in the game's item tooltip, under its name: "[Sets: SGE - FRU BiS (2), ...]",
/// with how many of it the set holds when more than one (materia...). A text line of the plugin's own, added to the
/// tooltip window the first time it is needed (as BisBuddy does) and taken out when the plugin stops. Game thread only.
/// </summary>
public sealed unsafe class GearItemDetail : IDisposable
{
    private const string Window = "ItemDetail";
    // Our node's id (unused by the game), and its place in the tooltip: under the item's name.
    private const uint NodeId = 0x52430001;
    private const float X = 66, Y = 45;
    private const ushort Width = 292, Height = 16;
    // Longest text shown (longer: cut with "..").
    private const int MaxLength = 52;

    private static readonly ByteColor Green = new() { R = 140, G = 255, B = 90, A = 255 };
    private static readonly ByteColor Edge = new() { R = 0, G = 0, B = 0, A = 255 };

    private readonly IAddonLifecycle lifecycle;
    private readonly IGameGui gameGui;
    private readonly Func<IEnumerable<GearSet>> sets;
    private readonly Func<bool> enabled;

    private ulong shownItem = ulong.MaxValue;

    public GearItemDetail(IAddonLifecycle lifecycle, IGameGui gameGui, Func<IEnumerable<GearSet>> sets, Func<bool> enabled)
    {
        this.lifecycle = lifecycle;
        this.gameGui = gameGui;
        this.sets = sets;
        this.enabled = enabled;
        lifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, Window, OnUpdate);
        lifecycle.RegisterListener(AddonEvent.PostDraw, Window, OnUpdate);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, Window, OnFinalize);
    }

    public void Dispose()
    {
        lifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, Window, OnUpdate);
        lifecycle.UnregisterListener(AddonEvent.PostDraw, Window, OnUpdate);
        lifecycle.UnregisterListener(AddonEvent.PreFinalize, Window, OnFinalize);
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(Window).Address;
        if (addon != null)
            Remove(addon);
    }

    /// <summary>The sets holding an item, with how many of it each one holds.</summary>
    public static List<(GearSet Set, int Count)> SetsWith(IEnumerable<GearSet> sets, uint itemId) =>
        sets.Select(s => (Set: s, Count: s.Pieces.Values.Count(p => p.ItemId == itemId)
                                          + s.Pieces.Values.Sum(p => p.ItemId != 0 ? p.Materia.Count(m => m == itemId) : 0)
                                          + (s.Food == itemId ? 1 : 0)))
            .Where(s => s.Count > 0)
            .ToList();

    // The text for an item: "[Sets: SGE - FRU BiS (2), ...]", empty when it is in no set.
    private string TextFor(ulong hovered)
    {
        var id = (uint)(hovered % 500_000); // high quality (+1 000 000) and collectable (+500 000) ids
        if (id == 0 || !enabled())
            return string.Empty;

        var holding = SetsWith(sets(), id);
        if (holding.Count == 0)
            return string.Empty;

        var names = string.Join(", ", holding.Select(h => h.Count > 1 ? $"{h.Set.Job} - {h.Set.Name} ({h.Count})" : $"{h.Set.Job} - {h.Set.Name}"));
        if (names.Length > MaxLength)
            names = names[..(MaxLength - 2)] + "..";
        return $"[{Loc.T("Sets")}: {names}]";
    }

    private void OnUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;

            // Worked out again when the item changes (or the tooltip is filled again).
            var hovered = gameGui.HoveredItem;
            if (type == AddonEvent.PostDraw && hovered == shownItem)
                return;
            shownItem = hovered;
            var text = TextFor(hovered);

            var node = Find(addon);
            if (text.Length == 0)
            {
                if (node != null)
                    node->AtkResNode.ToggleVisibility(false);
                return;
            }

            if (node == null)
                node = Create(addon);
            if (node == null)
                return;
            node->SetText(text);
            node->AtkResNode.ToggleVisibility(true);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Could not show the gear sets in the item tooltip");
        }
    }

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null)
            Remove(addon);
        shownItem = ulong.MaxValue;
    }

    private static AtkTextNode* Find(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node != null && node->NodeId == NodeId && node->Type == NodeType.Text)
                return (AtkTextNode*)node;
        }

        return null;
    }

    // Our text line, made like the game's own and linked last under the tooltip's root (drawn over the rest).
    private static AtkTextNode* Create(AtkUnitBase* addon)
    {
        var root = addon->RootNode;
        if (root == null || root->ChildNode == null)
            return null;

        var node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
        if (node == null)
            return null;

        node->AtkResNode.Type = NodeType.Text;
        node->AtkResNode.NodeId = NodeId;
        node->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop | NodeFlags.Visible | NodeFlags.Enabled;
        node->AtkResNode.DrawFlags = 0;
        node->AtkResNode.SetPositionFloat(X, Y);
        node->AtkResNode.SetWidth(Width);
        node->AtkResNode.SetHeight(Height);
        node->AtkResNode.Color = new ByteColor { R = 255, G = 255, B = 255, A = 255 };
        node->TextColor = Green;
        node->EdgeColor = Edge;
        node->LineSpacing = 12;
        node->FontSize = 12;
        node->SetFont(FontType.Axis);
        node->SetAlignment(AlignmentType.TopLeft);
        node->TextFlags = TextFlags.Edge;

        // Last of the root's children.
        var last = root->ChildNode;
        while (last->PrevSiblingNode != null)
            last = last->PrevSiblingNode;
        last->PrevSiblingNode = (AtkResNode*)node;
        node->AtkResNode.NextSiblingNode = last;
        node->AtkResNode.ParentNode = root;
        addon->UldManager.UpdateDrawNodeList();
        return node;
    }

    private static void Remove(AtkUnitBase* addon)
    {
        var node = Find(addon);
        if (node == null)
            return;

        var res = (AtkResNode*)node;
        if (res->NextSiblingNode != null)
            res->NextSiblingNode->PrevSiblingNode = res->PrevSiblingNode;
        if (res->PrevSiblingNode != null)
            res->PrevSiblingNode->NextSiblingNode = res->NextSiblingNode;
        if (res->ParentNode != null && res->ParentNode->ChildNode == res)
            res->ParentNode->ChildNode = res->PrevSiblingNode;
        addon->UldManager.UpdateDrawNodeList();
        node->AtkResNode.Destroy(true);
    }
}
