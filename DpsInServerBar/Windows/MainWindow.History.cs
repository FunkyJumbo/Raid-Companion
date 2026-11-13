using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.Native;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;
using DpsInServerBar.Ultimates;

namespace DpsInServerBar.Windows;

/// <summary>The History tab: filters, the pull list.</summary>
public sealed partial class MainWindow
{
    private (int Revision, ContentCategory Categories, string Filter) historyListKey;
    private List<PullRecord>? historyInCategories;
    private List<PullRecord>? historyVisible;

    private void DrawHistory()
    {
        var pulls = plugin.History.Pulls;
        if (pulls.Count == 0)
        {
            Theme.Hint(Loc.T("No pull recorded yet."));
            Theme.Hint(plugin.Configuration.RecordOnlyInDuties
                           ? Loc.T("Pulls are recorded inside duties only (see Settings > Pulls history).")
                           : Loc.T("Pulls shorter than the minimum duration are ignored."));
            fitHeight = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y;
            return;
        }

        var categories = ShowProgression ? plugin.Configuration.ProgressionCategories & ContentCategory.Progression : plugin.Configuration.HistoryCategories;
        // The filtered lists only change with the history or the filters: not built again on every frame.
        var listKey = (plugin.History.Revision, categories, historyFilter);
        if (listKey != historyListKey || historyInCategories is null || historyVisible is null)
        {
            historyListKey = listKey;
            historyInCategories = pulls.Where(p => (plugin.ContentCategories.Of(p.TerritoryId) & categories) != 0).ToList();
            historyVisible = historyInCategories
                .Where(p => historyFilter.Length == 0 || Progression.Encounter(p).Equals(historyFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var inCategories = historyInCategories;

        // The filters, with a little room around and between them.
        ImGui.Dummy(new Vector2(0, Theme.Px(5)));
        DrawCategoryFilter(pulls);
        ImGui.Dummy(new Vector2(0, Theme.Px(2)));
        DrawHistoryFilter(inCategories);
        ImGui.Dummy(new Vector2(0, Theme.Px(3)));

        if (ShowProgression)
        {
            DrawProgression(inCategories);
            return;
        }

        var visible = historyVisible;

        var selected = pulls.FirstOrDefault(p => p.Id == selectedId);

        // Pull list: room for the headers and at least 3 pulls, even in a small window. Without a selected pull it
        // takes all the room left (the details would be empty), with one it leaves most of the room to the details.
        var style = ImGui.GetStyle();
        float ListHeight(int rows) => ImGui.GetTextLineHeight() + style.CellPadding.Y * 2
                                      + rows * (ImGui.GetFrameHeight() + style.CellPadding.Y * 2)
                                      + style.ScrollbarSize + style.WindowPadding.Y * 2 + Theme.Px(2);
        var minHeight = ListHeight(3);
        var fullHeight = Math.Max(minHeight, ListHeight(visible.Count)); // every pull shown: no taller than that
        var avail = ImGui.GetContentRegionAvail().Y;
        var listHeight = selected is null
            ? Math.Max(avail - ImGui.GetTextLineHeightWithSpacing() - style.ItemSpacing.Y, minHeight)
            : Math.Max(minHeight, avail - detailsHeight - style.ItemSpacing.Y);
        // With a pull selected, the list stays at its smallest until all the details below fit; past that, all the
        // extra room goes to the list (the details keep their size, the graph its smallest height).
        pullListExtra = Math.Min(pullListExtra, Math.Max(0, fullHeight - listHeight));
        var shown = Math.Min(listHeight + pullListExtra, fullHeight);
        // During a screenshot the list keeps the height it had when it was asked for, while the window grows.
        var frozen = shot.Holding && shotPullListHeight is not null;
        if (frozen)
            shown = shotPullListHeight!.Value;
        else
            (pullListHeight, pullListFullHeight) = (shown, fullHeight);

        using (var child = ImRaii.Child("##pulllist", new Vector2(-1, shown), true))
        {
            if (child)
                DrawPullTable(visible);
        }

        DrawPullListGrip();

        // Where what follows the list would start with every pull shown: the window needs no more than that.
        var afterFullList = ImGui.GetCursorPosY() + (frozen ? 0 : fullHeight - shown);
        if (selected is null)
        {
            Theme.Hint(Loc.T("Select a pull to see its details. Tick \"vs\" on another one to compare."));
            fitHeight = afterFullList + ImGui.GetTextLineHeight() + style.WindowPadding.Y;
            return;
        }

        var compare = pulls.FirstOrDefault(p => p.Id == compareId && p.Id != selectedId);
        DrawPullDetails(selected, compare);
        fitHeight = afterFullList + detailsHeight + style.WindowPadding.Y;
    }

    /// <summary>
    /// One chip per kind of content (several can be on), with the number of pulls in each. The Progression tab has its
    /// own choice, among the content with a real progression only.
    /// </summary>
    private void DrawCategoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        var config = plugin.Configuration;
        var offered = ShowProgression ? ContentCategory.Progression : ContentCategory.All;
        var selected = ShowProgression ? config.ProgressionCategories : config.HistoryCategories;
        void Set(ContentCategory value)
        {
            if (ShowProgression)
                config.ProgressionCategories = value;
            else
                config.HistoryCategories = value;
            config.Save();
        }

        var counts = pulls.GroupBy(p => plugin.ContentCategories.Of(p.TerritoryId)).ToDictionary(g => g.Key, g => g.Count());

        var first = true;
        foreach (var (category, label) in ContentCategories.Labels.Where(l => (offered & l.Category) != 0))
        {
            // Wrap to the next line when the chip would not fit.
            var text = $"{label} {counts.GetValueOrDefault(category)}";
            var width = ImGui.CalcTextSize(text).X + Theme.Px(20);
            if (!first)
            {
                ImGui.SameLine(0, Theme.Px(4));
                if (ImGui.GetContentRegionAvail().X < width)
                    ImGui.NewLine();
            }

            first = false;
            var on = (selected & category) != 0;
            if (Theme.Chip(text, on))
                Set(on ? selected & ~category : selected | category);
        }

        ImGui.SameLine(0, Theme.Px(4));
        var all = (selected & offered) == offered;
        if (Theme.Chip(all ? Loc.T("None") : Loc.T("All"), false))
            Set(all ? ContentCategory.None : offered);
    }

    private void DrawHistoryFilter(IReadOnlyList<PullRecord> pulls)
    {
        // The fights go by their instance (with the fight's name when the instance has two, e.g. a door boss); an
        // ultimate by its own name (its instance has a meaningless name, e.g. "Liminal Space").
        var zoneOf = pulls.Where(p => Progression.Encounter(p).Length > 0)
                          .GroupBy(Progression.Encounter, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key,
                                        g => Progression.IsUltimate(g.First().TerritoryId)
                                                 ? g.Key
                                                 : g.Select(p => p.Zone).FirstOrDefault(z => z.Length > 0) ?? g.Key,
                                        StringComparer.OrdinalIgnoreCase);
        string Label(string title)
        {
            if (!zoneOf.TryGetValue(title, out var zone))
                return title;
            zone = char.ToUpperInvariant(zone[0]) + zone[1..];
            return zoneOf.Values.Count(z => z.Equals(zoneOf[title], StringComparison.OrdinalIgnoreCase)) > 1 ? $"{zone} ({title})" : zone;
        }

        var titles = zoneOf.Keys.OrderBy(Label).ToList();

        ImGui.SetNextItemWidth(Theme.Px(260));
        using (var combo = ImRaii.Combo(Loc.T("Instance"), historyFilter.Length == 0 ? Loc.T("All") : Label(historyFilter),
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                // Type to filter the fights.
                if (ImGui.IsWindowAppearing())
                {
                    encounterSearch = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##encountersearch", Loc.T("Search a fight"), ref encounterSearch, 64);

                if (encounterSearch.Length == 0 && ImGui.Selectable(Loc.T("All"), historyFilter.Length == 0))
                    historyFilter = string.Empty;
                foreach (var title in titles)
                {
                    if (encounterSearch.Length > 0 && !Label(title).Contains(encounterSearch, StringComparison.OrdinalIgnoreCase)
                        && !title.Contains(encounterSearch, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable(Label(title), title.Equals(historyFilter, StringComparison.OrdinalIgnoreCase)))
                        historyFilter = title;
                }
            }
        }

        var label = Loc.T("Clear all");
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(label).X - Theme.Px(20));
        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (Theme.GhostButton(label))
            {
                plugin.History.Clear();
                selectedId = compareId = null;
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("Hold Ctrl to delete the whole history."));
    }

    /// <summary>A grip under the pull list: drag it down to make the list taller, double-click to bring it back.</summary>
    private void DrawPullListGrip()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - spacing); // right under the list
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Theme.Px(8);
        ImGui.InvisibleButton("##pulllistgrip", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if (active)
            pullListExtra = Math.Max(0, pullListExtra + ImGui.GetIO().MouseDelta.Y);
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            pullListExtra = 0;
        if (hovered)
            ImGui.SetTooltip(Loc.T("Drag to make the list taller, double-click to reset it."));

        var mid = new Vector2(pos.X + width / 2, pos.Y + height / 2);
        var color = Theme.U32(active ? Theme.Gold : hovered ? Theme.TextMuted : Theme.Line);
        for (var i = -1; i <= 1; i += 2)
        {
            var y = mid.Y + i * Theme.Px(1.5f);
            ImGui.GetWindowDrawList().AddLine(new Vector2(mid.X - Theme.Px(18), y), new Vector2(mid.X + Theme.Px(18), y), color, Theme.Px(1));
        }
    }

    private void DrawPullTable(List<PullRecord> pulls)
    {
        // Scrolls sideways in a narrow window rather than squashing the columns.
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollX
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        var innerWidth = Math.Max(ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize, Theme.Px(840));
        using var table = ImRaii.Table("##pulls", 12, flags, new Vector2(-1, -1), innerWidth);
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(1, 1); // the date and the headers stay visible while scrolling
        ImGui.TableSetupColumn(Loc.T("When"), ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn(Loc.T("Encounter"), ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(Loc.T("Result"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(Loc.T("Boss HP"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(Loc.T("Job"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(Loc.T("DPS"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Loc.T("rDPS"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Loc.T("pDPS"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn(Loc.T("Rank"), ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn(Loc.T("vs"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableHeadersRow();

        // Rows as tall as the checkbox and the delete button, everything else centered on that height.
        var rowHeight = ImGui.GetFrameHeight();
        var iconSize = ImGui.GetTextLineHeight();
        var framePadding = ImGui.GetStyle().FramePadding.Y;
        using var selectableAlign = ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, 0.5f));

        void Text(string text, Vector4? color = null)
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color ?? Theme.Text, text);
        }

        Guid? toDelete = null;
        foreach (var p in pulls)
        {
            using var id = ImRaii.PushId(p.Id.ToString());
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

            ImGui.TableNextColumn();
            if (ImGui.Selectable(p.StartUtc.ToLocalTime().ToString("dd/MM HH:mm"), p.Id == selectedId,
                                 ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, rowHeight)))
            {
                selectedId = p.Id;
            }

            Text(Progression.IsUltimate(p.TerritoryId)
                     ? $"{Progression.Encounter(p)} · {UltimateDefinitions.Label(p.TerritoryId, Progression.Phase(p))}"
                     : p.Title.Length > 0 ? p.Title : p.Zone);
            // In an ultimate, the time in the phase reached (the whole pull on hover).
            Text(NumberFormat.Duration(TimeSpan.FromSeconds(p.PhaseSeconds ?? p.DurationSeconds)), Theme.TextMuted);
            if (p.PhaseSeconds is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.F("Time in {0}. Whole pull: {1}", UltimateDefinitions.Label(p.TerritoryId, Progression.Phase(p)),
                                       NumberFormat.Duration(TimeSpan.FromSeconds(p.DurationSeconds))));
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + framePadding - Theme.Px(1));
            switch (p.Outcome)
            {
                case PullOutcome.Kill:
                    Theme.Badge(Loc.T("Kill"), Theme.Good);
                    break;
                case PullOutcome.Wipe:
                    Theme.Badge(Loc.T("Wipe"), Theme.Bad);
                    break;
                default:
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Px(1));
                    ImGui.TextColored(Theme.TextFaint, "-");
                    break;
            }

            // Boss HP left when the pull ended (of the phase reached, in an ultimate), or Kill.
            if (p.Outcome == PullOutcome.Kill)
                Text(Loc.T("Kill"), Theme.Good);
            else if (p.BossHpLeft is { } hpLeft)
                Text($"{hpLeft:0.0%}", Theme.TextMuted);
            else
                Text("-", Theme.TextFaint);

            // The icon is drawn by hand next to text aligned like the other cells (the row's text baseline also
            // applies to anything placed after an image, which pushed the text down).
            ImGui.TableNextColumn();
            var cell = ImGui.GetCursorScreenPos();
            JobInfo.DrawIconAt(p.Job, cell with { Y = cell.Y + framePadding }, iconSize);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + iconSize + Theme.Px(4));
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(p.Job);
            Text(NumberFormat.Format(p.YourDps, NumberStyle.Grouped));
            Text(p.YourRdps is { } rd ? NumberFormat.Format(rd, NumberStyle.Grouped) : "-");
            Text(NumberFormat.Format(p.PartyDps, NumberStyle.Grouped));
            Text(p.PartySize > 1 && p.Rank > 0 ? $"{p.Rank}/{p.PartySize}" : "-", Theme.TextMuted);

            ImGui.TableNextColumn();
            var isCompare = compareId == p.Id;
            if (ImGui.Checkbox("##cmp", ref isCompare))
                compareId = isCompare ? p.Id : null;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Compare the selected pull with this one."));

            ImGui.TableNextColumn();
            if (Theme.IconButton("del", FontAwesomeIcon.TrashAlt, Loc.T("Delete this pull"), Theme.TextFaint))
                toDelete = p.Id;
        }

        if (toDelete is { } del)
        {
            plugin.History.Remove(del);
            if (selectedId == del)
                selectedId = null;
            if (compareId == del)
                compareId = null;
        }
    }
}
