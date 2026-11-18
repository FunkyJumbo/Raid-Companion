using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DpsInServerBar.Display;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// The table of a mitigation plan, shared by its window and the editor: the cells (skill icons, centered, three per
/// line, and a "?" holding the note), the skills picker of a cell, the headers with the job of each column, and in the
/// plan's window, the pull in progress followed. Holds the state of one table (one per window).
/// </summary>
public sealed class MitigationTable
{
    private const int IconsPerLine = 3;

    private readonly Plugin plugin;

    // Bottom of each row on the last frame (screen y): a cell is clickable down to it.
    private readonly Dictionary<int, float> rowBottoms = new();

    // Where the pull in progress is in the plan shown, refreshed a few times per second.
    private DateTime nextRefresh;
    private (int Row, double At) progress = (-1, 0);
    private double elapsed;
    private int scrolledTo = -2;
    private string followed = string.Empty;

    public MitigationTable(Plugin plugin) => this.plugin = plugin;

    private Configuration Config => plugin.Configuration;

    public static float IconSize => Theme.Px(26);

    /// <summary>Width of a column of skills: three icons side by side.</summary>
    public static float CellWidth => IconSize * IconsPerLine + Theme.Px(3) * (IconsPerLine - 1) + Theme.Px(4);

    /// <summary>Whether the plan follows the pull in progress (its fight, rows linked to casts).</summary>
    public bool Following { get; private set; }

    // ---- The plan, as a table

    /// <summary>The plan, read only (the cells can still be clicked to pick their skills).</summary>
    public void DrawPlan(MitigationPlan plan)
    {
        RefreshProgress(plan);
        var next = Following ? progress.Row + 1 : -1;
        var now = Following && progress.Row >= 0 && elapsed - progress.At < 10 ? progress.Row : -1;
        var mine = plugin.PlanCheck.MyColumn(plan);

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX
                    | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("##mitplan", plan.Columns.Count + 2, flags, new Vector2(-1, -1));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableSetupColumn(Loc.T("Time"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("-00:00").X + Theme.Px(6));
        ImGui.TableSetupColumn(Loc.T("Cast"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(170));
        for (var c = 0; c < plan.Columns.Count; c++)
            ImGui.TableSetupColumn($"{plan.Columns[c]}##col{c}", ImGuiTableColumnFlags.WidthFixed, CellWidth);
        DrawHeaders(plan, 2, 0, mine);

        var phase = string.Empty;
        for (var i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            if (row.Phase.Length > 0 && row.Phase != phase)
            {
                phase = row.Phase;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(Theme.GoldBright, phase);
            }

            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();
            if (i == now)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, Theme.U32(Theme.Good, 0.35f));
            else if (i == next)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, Theme.U32(Theme.Gold, 0.35f));

            var done = Following && i <= progress.Row && i != now;
            using var color = ImRaii.PushColor(ImGuiCol.Text, Theme.TextFaint, done);

            ImGui.TableNextColumn();
            if (i == next && scrolledTo != next)
            {
                ImGui.SetScrollHereY(0.3f);
                scrolledTo = next;
            }

            if (row.Prepull)
                ImGui.TextColored(Theme.Crystal, row.PrepullSeconds is { } before ? $"-{before:0.#}s" : Loc.T("pre"));
            else if (row.AtSeconds is { } at)
                ImGui.TextColored(done ? Theme.TextFaint : Theme.TextMuted, NumberFormat.Duration(TimeSpan.FromSeconds(at)));

            ImGui.TableNextColumn();
            var rowTop = ImGui.GetCursorScreenPos().Y;
            ImGui.TextWrapped(row.Prepull && row.Name.Length == 0 ? Loc.T("Pre-pull") : row.Name);
            if (ImGui.IsItemHovered() && row.ActionId != 0)
                ImGui.SetTooltip(Loc.F("Linked to {0} (use {1} in the pull)", GameNames.Action(row.ActionId, row.Action), row.Occurrence));
            if (row.Note.Length > 0)
                Theme.HelpMarker(row.Note);

            DrawCells(plan, row, i, rowTop, mine);
        }
    }

    private void RefreshProgress(MitigationPlan plan)
    {
        var pull = plugin.Pulls.Current;
        Following = pull is not null && plan.TerritoryId == plugin.CurrentTerritory && plan.Rows.Any(r => r.ActionId != 0);
        if (!Following)
        {
            progress = (-1, 0);
            return;
        }

        if (followed != plan.Id)
        {
            followed = plan.Id;
            scrolledTo = -2;
            nextRefresh = DateTime.MinValue;
        }

        elapsed = (DateTime.UtcNow - pull!.StartUtc).TotalSeconds;
        if (DateTime.UtcNow < nextRefresh)
            return;
        nextRefresh = DateTime.UtcNow.AddSeconds(0.4);
        progress = MitigationPlans.Progress(plan, plugin.FightLog.CastsSince(pull.StartUtc));
    }

    // ---- Cells

    /// <summary>The cells of a row (the next columns). A click anywhere in a cell, the whole height of the row, opens its skills.</summary>
    public void DrawCells(MitigationPlan plan, MitigationRow row, int index, float rowTop, int mine)
    {
        // The row's height is known once all its cells are drawn: the one of the last frame is used.
        var padding = ImGui.GetStyle().CellPadding;
        var lastBottom = rowBottoms.GetValueOrDefault(index, rowTop + IconSize);
        var mouse = ImGui.GetMousePos();
        var inRow = ImGui.IsWindowHovered() && mouse.Y >= rowTop - padding.Y && mouse.Y <= lastBottom + padding.Y;

        var bottom = ImGui.GetItemRectMax().Y;
        for (var c = 0; c < plan.Columns.Count; c++)
        {
            ImGui.TableNextColumn();
            if (c == mine)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, Theme.U32(Theme.Crystal, 0.12f));

            // Opened here, in the cell: ImGui gives each column of a table its own id, the popup must be asked for in it.
            var left = ImGui.GetCursorScreenPos().X;
            var right = left + ImGui.GetContentRegionAvail().X;
            if (inRow && mouse.X >= left - padding.X && mouse.X <= right + padding.X)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, Theme.U32(Theme.Gold, 0.10f));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    ImGui.OpenPopup($"##pick{c}");
            }

            DrawCell(plan, row, c);
            bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
        }

        rowBottoms[index] = bottom;
    }

    // The skills of a cell as icons, three per line and centered, and a "?" holding its note.
    private void DrawCell(MitigationPlan plan, MitigationRow row, int column)
    {
        var picks = MitigationPlan.Picks(row, column);
        var text = MitigationPlan.Cell(row, column);

        // With a job chosen for the column, only its skills (the others stay in the plan, for other groups).
        var job = ColumnJob(plan, column);
        if (job.Length > 0)
        {
            var offered = PlanActions.Offered(plan.RoleOf(column).Role, job);
            picks = picks.Where(p => offered.Contains(p.ActionId)).ToList();
        }

        var size = IconSize;
        var gap = Theme.Px(3);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorPosX();
        var marker = text.Length > 0 ? ImGui.GetFrameHeight() * 0.7f : 0;
        var perLine = Math.Max(1, Math.Min(IconsPerLine, (int)((width + gap) / (size + gap))));
        var lines = picks.Count == 0 ? 1 : (picks.Count + perLine - 1) / perLine;

        ImGui.BeginGroup();
        for (var line = 0; line < lines; line++)
        {
            var count = Math.Min(perLine, picks.Count - line * perLine);
            var last = line == lines - 1;
            var lineWidth = Math.Max(0, count * size + Math.Max(0, count - 1) * gap) + (last && marker > 0 ? gap + marker : 0);
            ImGui.SetCursorPosX(start + Math.Max(0, (width - lineWidth) / 2));

            for (var k = 0; k < count; k++)
            {
                var p = picks[line * perLine + k];
                if (k > 0)
                    ImGui.SameLine(0, gap);
                if (ActionIcons.Draw(ActionIcons.OfAction(p.ActionId), size))
                    ImGui.SetTooltip(GameNames.Action(p.ActionId, $"#{p.ActionId}"));
            }

            if (last && marker > 0)
            {
                if (count > 0)
                    ImGui.SameLine(0, gap);
                Theme.Icon(FontAwesomeIcon.QuestionCircle, Theme.TextFaint);
                if (ImGui.IsItemHovered())
                {
                    using var tooltip = ImRaii.Tooltip();
                    ImGui.PushTextWrapPos(Theme.Px(380));
                    ImGui.TextUnformatted(text);
                    ImGui.PopTextWrapPos();
                }
            }
            else if (count == 0)
            {
                ImGui.Dummy(new Vector2(size, size));
            }
        }

        ImGui.EndGroup();
        DrawPicker(plan, row, column);
    }

    // The skills of the column (its job's, or its role's), to tick, and the note of the cell.
    private void DrawPicker(MitigationPlan plan, MitigationRow row, int column)
    {
        using var popup = ImRaii.Popup($"##pick{column}");
        if (!popup)
            return;

        var role = plan.RoleOf(column).Role;
        var job = ColumnJob(plan, column);
        ImGui.TextColored(Theme.Gold, $"{plan.Columns[column]}  ·  {(row.Prepull ? Loc.T("Pre-pull") : row.Name)}");
        if (role == PlanRole.Any && job.Length == 0)
            Theme.Hint(Loc.T("This column names no role: every skill is offered. Name it MT, OT, H1, H2, M1, M2, R1, R2 (or a job) to see only its own."));

        var picks = MitigationPlan.PicksToEdit(row, column);
        var size = IconSize;
        var changed = false;
        var shown = new HashSet<uint>();
        foreach (var group in MitigationCatalog.For(role, job))
        {
            var ids = group.Families.Select(f => (Family: f, Id: f.Select(PlanActions.Id).FirstOrDefault(i => i != 0)))
                           .Where(x => x.Id != 0 && shown.Add(x.Id)).ToList();
            if (ids.Count == 0)
                continue;

            if (group.Job.Length > 0)
            {
                ActionIcons.Draw(PlanActions.JobIcon(group.Job), ImGui.GetTextLineHeight());
                ImGui.SameLine(0, Theme.Px(4));
                ImGui.TextColored(Theme.TextMuted, group.Job);
            }
            else
            {
                ImGui.TextColored(Theme.TextMuted, Loc.T("Role actions"));
            }

            ImGui.SameLine(Theme.Px(110));
            var first = true;
            foreach (var (family, id) in ids)
            {
                if (!first)
                    ImGui.SameLine(0, Theme.Px(3));
                first = false;

                var picked = picks.Any(p => p.ActionId == id);
                var pos = ImGui.GetCursorScreenPos();
                var hovered = ActionIcons.Draw(ActionIcons.OfAction(id), size);
                if (!picked)
                    ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(size, size), Theme.U32(Theme.Ink, 0.55f));
                else
                    ImGui.GetWindowDrawList().AddRect(pos, pos + new Vector2(size, size), Theme.U32(Theme.Good, 1f), 3, ImDrawFlags.None, Theme.Px(2));
                if (hovered)
                    ImGui.SetTooltip(GameNames.Action(id, family[0]));
                if (ImGui.IsItemClicked())
                {
                    if (picked)
                        picks.RemoveAll(p => p.ActionId == id);
                    else
                        picks.Add(new PlannedAction { ActionId = id });
                    changed = true;
                }
            }
        }

        Theme.Rule(2);
        var text = MitigationPlan.Cell(row, column);
        ImGui.SetNextItemWidth(Theme.Px(320));
        if (ImGui.InputTextWithHint("##celltext", Loc.T("Note (shown on hover of the ?)"), ref text, 200))
            MitigationPlan.SetCell(row, column, text);
        if (ImGui.IsItemDeactivatedAfterEdit())
            changed = true;

        if (changed)
            Config.Save();
    }

    // ---- Jobs of the columns (this group's, not shared)

    public string ColumnJob(MitigationPlan plan, int column) =>
        Config.MitigationColumnJobs.TryGetValue($"{plan.Id}:{column}", out var job) ? job : plan.RoleOf(column).Job;

    private void SetColumnJob(MitigationPlan plan, int column, string job)
    {
        if (job.Length == 0)
            Config.MitigationColumnJobs.Remove($"{plan.Id}:{column}");
        else
            Config.MitigationColumnJobs[$"{plan.Id}:{column}"] = job;
        Config.Save();
    }

    /// <summary>The header row: the plan's columns with the icon of the job played; click one to choose its job.</summary>
    public void DrawHeaders(MitigationPlan plan, int before, int after, int mine)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < before + plan.Columns.Count + after; i++)
        {
            ImGui.TableSetColumnIndex(i);
            var c = i - before;
            if (c < 0 || c >= plan.Columns.Count)
            {
                ImGui.TableHeader(ImGui.TableGetColumnName(i));
                continue;
            }

            using var id = ImRaii.PushId($"head{c}");
            var job = ColumnJob(plan, c);
            if (job.Length > 0)
            {
                ActionIcons.Draw(PlanActions.JobIcon(job), ImGui.GetTextLineHeight());
                ImGui.SameLine(0, Theme.Px(4));
            }

            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Crystal, c == mine))
                ImGui.TableHeader(plan.Columns[c]);
            if (ImGui.IsItemClicked())
                ImGui.OpenPopup("##colJob");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Click to choose the job played in this position: its cells then offer its skills only. Your choice stays with you (a shared plan carries no job)."));

            using var popup = ImRaii.Popup("##colJob");
            if (!popup)
                continue;
            if (ImGui.Selectable(Loc.T("All jobs"), job.Length == 0))
                SetColumnJob(plan, c, string.Empty);
            foreach (var j in MitigationCatalog.JobsFor(plan.RoleOf(c).Role))
            {
                ActionIcons.Draw(PlanActions.JobIcon(j), ImGui.GetTextLineHeight());
                ImGui.SameLine(0, Theme.Px(4));
                if (ImGui.Selectable(j, j == job))
                    SetColumnJob(plan, c, j);
            }
        }
    }

    /// <summary>Your column, and whether this plan checks your mitigations in its fight.</summary>
    public void DrawMine(MitigationPlan plan)
    {
        var mine = plugin.PlanCheck.MyColumn(plan);
        ImGui.SetNextItemWidth(Theme.Px(150));
        using (var combo = ImRaii.Combo(Loc.T("My column"), mine >= 0 ? plan.Columns[mine] : Loc.T("none")))
        {
            if (combo)
            {
                for (var c = 0; c < plan.Columns.Count; c++)
                {
                    if (ImGui.Selectable($"{plan.Columns[c]}##mine{c}", c == mine))
                    {
                        Config.MitigationMyColumn[plan.Id] = c;
                        Config.MitigationMySlot = plan.Columns[c];
                        Config.Save();
                    }
                }
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Your role in this plan: its column is lit, and your mitigations are checked against it."));

        ImGui.SameLine(0, Theme.Px(14));
        var active = Config.MitigationActive.TryGetValue(plan.TerritoryId, out var id) && id == plan.Id;
        if (ImGui.Checkbox(Loc.T("Check my mitigations"), ref active))
        {
            if (active)
                Config.MitigationActive[plan.TerritoryId] = plan.Id;
            else
                Config.MitigationActive.Remove(plan.TerritoryId);
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Uses this plan in its fight: each skill of your column not used in time for its cast shows in Personal (Live and History). One plan per fight."));
    }
}
