using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Display;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// Mitigation plans: the list of the plans of every fight (searched by fight or name), each shown in its own window
/// (<see cref="MitigationPlanWindow"/>) or edited here. Plans are made from the casts of a pull in History, imported
/// from cells copied out of a spreadsheet, or from a code shared by another player.
/// </summary>
public sealed class MitigationWindow : Window
{
    private readonly Plugin plugin;
    private readonly MitigationPlanWindow planWindow;
    private readonly MitigationTable table;
    private readonly WindowShot shot;
    private IDisposable? themeScope;

    private string editingId = string.Empty;
    private string search = string.Empty;
    private string message = string.Empty;
    private bool messageIsError;
    private Guid? sourcePullId;
    private string castFilter = string.Empty;

    public MitigationWindow(Plugin plugin, MitigationPlanWindow planWindow) : base(Loc.T("Mitigation plans###DpsInServerBarMitigation"))
    {
        Loc.Changed += () => WindowName = Loc.T("Mitigation plans###DpsInServerBarMitigation");
        this.plugin = plugin;
        this.planWindow = planWindow;
        planWindow.EditRequested += Edit;
        table = new MitigationTable(plugin);
        Size = new Vector2(760, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 300), MaximumSize = new Vector2(5000, 5000) };
        AllowBackgroundBlur = false;
        shot = new WindowShot(this);
        TitleBarButtons.Add(shot.TitleBarButton(() => Editing() is { } plan ? plan.Name : Loc.T("Mitigation plans")));
    }

    private Configuration Config => plugin.Configuration;

    /// <summary>A new plan for the fight of this pull, open in the editor with its casts ready to be added as rows.</summary>
    public void NewFromPull(PullRecord pull)
    {
        var plan = NewPlan(pull.TerritoryId, Progression.Encounter(pull));
        sourcePullId = pull.Id;
        Edit(plan);
        Say(Loc.T("New plan: add the casts of the pull on the right, then click a cell to pick the skills of its role."), false);
    }

    /// <summary>The list of the plans; during a pull, the plan of its fight is shown too.</summary>
    public void Show()
    {
        IsOpen = true;
        BringToFront();
        if (plugin.Pulls.Current is not null && ForTerritory(plugin.CurrentTerritory) is { } plan)
            planWindow.Show(plan);
    }

    private void Edit(MitigationPlan plan)
    {
        editingId = plan.Id;
        message = string.Empty;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        themeScope = Theme.Push();

        // Editing: no wider than the editor (its last column would stretch). The list: as wide as wished.
        var max = Editing() is { } plan ? MitigationTable.WindowWidthFor(EditorWidth(plan)) / Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale : 5000;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(Math.Min(480, max), 300),
            MaximumSize = new Vector2(max, 5000),
        };
        shot.PreDraw();
    }

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        shot.Step();
        Theme.BlurBehind();
        if (Editing() is { } plan)
        {
            DrawEditorBar(plan);
            if (message.Length > 0)
                ImGui.TextColored(messageIsError ? Theme.Bad : Theme.Good, message);
            Theme.Rule(2);
            DrawEditor(plan);
            return;
        }

        editingId = string.Empty;
        DrawListBar();
        if (message.Length > 0)
            ImGui.TextColored(messageIsError ? Theme.Bad : Theme.Good, message);
        Theme.Rule(2);
        DrawList();
    }

    // ---- Plans

    private MitigationPlan? Editing() => Find(editingId);

    private MitigationPlan? Find(string id) => id.Length == 0 ? null : Config.MitigationPlans.FirstOrDefault(p => p.Id == id);

    // The plan of a fight: the active one, else the one shown last, else the first.
    private MitigationPlan? ForTerritory(uint territory) =>
        (Config.MitigationActive.TryGetValue(territory, out var active) ? Find(active) : null)
        ?? (Config.MitigationPlanSelected.TryGetValue(territory, out var id) ? Find(id) : null)
        ?? Config.MitigationPlans.FirstOrDefault(p => p.TerritoryId == territory);

    private void ShowPlan(MitigationPlan plan)
    {
        Config.MitigationPlanSelected[plan.TerritoryId] = plan.Id;
        Config.Save();
        planWindow.Show(plan);
    }

    private MitigationPlan NewPlan(uint territory, string encounter)
    {
        var plan = new MitigationPlan { TerritoryId = territory, Encounter = encounter, Name = Loc.F("{0} plan", encounter) };
        Config.MitigationPlans.Add(plan);
        Config.Save();
        return plan;
    }

    private void Say(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    // The fights of the History, most recent first: territory, name, and their pulls.
    private List<(uint Territory, string Encounter, List<PullRecord> Pulls)> Fights() =>
        plugin.History.Pulls
              .GroupBy(p => p.TerritoryId)
              .Select(g => (g.Key, Progression.Encounter(g.OrderByDescending(p => p.StartUtc).First()), g.ToList()))
              .OrderByDescending(f => f.Item3.Max(p => p.StartUtc))
              .ToList();

    // The pull whose casts are offered in the editor: the one chosen, else the longest of the fight (it went furthest).
    private PullRecord? SourcePull(MitigationPlan plan)
    {
        var pulls = plugin.History.Pulls.Where(p => p.TerritoryId == plan.TerritoryId && p.BossCasts.Count > 0).ToList();
        return pulls.FirstOrDefault(p => p.Id == sourcePullId) ?? pulls.OrderByDescending(p => p.DurationSeconds).FirstOrDefault();
    }

    private static IEnumerable<string> CastNames(BossCast cast) => [cast.Action, GameNames.Action(cast.ActionId, cast.Action)];

    // ---- The list

    private void DrawListBar()
    {
        ImGui.SetNextItemWidth(Theme.Px(280));
        ImGui.InputTextWithHint("##plansearch", Loc.T("Search a fight or a plan"), ref search, 64);

        ImGui.SameLine();
        if (Theme.IconButton("newplan", FontAwesomeIcon.Plus, Loc.T("New plan, for a fight of the History")))
            ImGui.OpenPopup("##newplan");
        DrawFightPopup("##newplan", Loc.T("New plan for:"), (territory, encounter, pulls) =>
        {
            var created = NewPlan(territory, encounter);
            sourcePullId = pulls.OrderByDescending(p => p.DurationSeconds).First().Id;
            Edit(created);
            Say(Loc.T("New plan: add the casts of the pull on the right, then click a cell to pick the skills of its role."), false);
        });

        ImGui.SameLine();
        if (Theme.IconButton("importplan", FontAwesomeIcon.Paste,
                             Loc.T("Import a plan: a code shared by another player, or cells copied from a spreadsheet (the header row naming the roles, the casts and what each role uses). Copy it, then click here.")))
        {
            if (MitigationPlans.Import(ImGui.GetClipboardText() ?? string.Empty) is { } shared)
            {
                Config.MitigationPlans.Add(shared);
                Config.Save();
                Say(Loc.F("Imported the shared plan: {0} ({1}). Choose your column, then tick \"Check my mitigations\".", shared.Name, shared.Encounter), false);
                ShowPlan(shared);
            }
            else
            {
                ImGui.OpenPopup("##importplan");
            }
        }

        DrawFightPopup("##importplan", Loc.T("Import the copied plan for:"), Import);
    }

    // A copy of a plan, right after it, named "Name (1)", "(2)"... (the first number free).
    private void Duplicate(MitigationPlan plan)
    {
        if (MitigationPlans.Import(MitigationPlans.Export(plan)) is not { } copy)
            return;

        var n = 1;
        while (Config.MitigationPlans.Any(p => p.Name == $"{plan.Name} ({n})"))
            n++;
        copy.Name = $"{plan.Name} ({n})";
        Config.MitigationPlans.Insert(Config.MitigationPlans.IndexOf(plan) + 1, copy);
        Config.Save();
        Say(Loc.F("Duplicated: {0}.", copy.Name), false);
    }

    // The plans by fight (the fight in progress first), their name only; a fight searched shows all its plans.
    private void DrawList()
    {
        var plans = Config.MitigationPlans;
        if (plans.Count == 0)
        {
            Theme.Hint(Loc.T("No plan yet. Make one from a pull in History (Timeline, \"New mitigation plan\"), import one copied from a spreadsheet or shared by another player (the paste button above)."));
            return;
        }

        var here = plugin.CurrentTerritory;
        var fights = plans.GroupBy(p => p.TerritoryId)
                          .OrderBy(g => g.Key == here ? 0 : 1).ThenBy(g => g.First().Encounter)
                          .ToList();
        var any = false;
        var style = ImGui.GetStyle();
        var buttons = ImGui.CalcTextSize(Loc.T("Show")).X + ImGui.CalcTextSize(Loc.T("Edit")).X
                      + (style.FramePadding.X + style.FrameBorderSize) * 4 + style.ItemSpacing.X * 2
                      + Theme.IconButtonWidth(FontAwesomeIcon.Copy) + style.ItemSpacing.X
                      + Theme.IconButtonWidth(FontAwesomeIcon.Trash);
        using var list = ImRaii.Child("##planlist", new Vector2(-1, -1));
        foreach (var fight in fights)
        {
            var encounter = plugin.ContentCategories.DisplayName(fight.Key, fight.First().Encounter) ?? fight.First().Encounter;
            var fightMatches = search.Length == 0 || encounter.Contains(search, StringComparison.OrdinalIgnoreCase);
            var shown = fight.Where(p => fightMatches || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            if (shown.Count == 0)
                continue;

            any = true;
            ImGui.TextColored(Theme.Gold, encounter);
            if (fight.Key == here)
            {
                ImGui.SameLine();
                Theme.Badge(Loc.T("here"), Theme.Crystal);
            }

            foreach (var plan in shown)
            {
                using var id = ImRaii.PushId(plan.Id);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Px(14));
                var active = Config.MitigationActive.TryGetValue(plan.TerritoryId, out var a) && a == plan.Id;
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(plan.Name);
                if (active)
                {
                    ImGui.SameLine();
                    Theme.Badge(Loc.T("checks my mitigations"), Theme.Good);
                }

                ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + Theme.Px(10), ImGui.GetContentRegionMax().X - buttons));
                if (ImGui.Button(Loc.T("Show")))
                    ShowPlan(plan);
                ImGui.SameLine();
                if (ImGui.Button(Loc.T("Edit")))
                    Edit(plan);
                ImGui.SameLine();
                if (Theme.IconButton("duplicateplan", FontAwesomeIcon.Copy, Loc.T("Duplicate this plan")))
                {
                    Duplicate(plan);
                    break;
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
                {
                    if (Theme.IconButton("deleteplan", FontAwesomeIcon.Trash, Loc.T("Delete this plan (hold Ctrl)")))
                    {
                        Config.MitigationPlans.Remove(plan);
                        if (active)
                            Config.MitigationActive.Remove(plan.TerritoryId);
                        Config.Save();
                        Say(Loc.F("Deleted: {0}.", plan.Name), false);
                        break;
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Loc.T("Delete this plan (hold Ctrl)"));
            }

            ImGui.Spacing();
        }

        if (!any)
            Theme.Hint(Loc.T("No plan for this search."));
    }

    private void DrawFightPopup(string id, string title, Action<uint, string, List<PullRecord>> picked)
    {
        using var popup = ImRaii.Popup(id);
        if (!popup)
            return;

        ImGui.TextColored(Theme.TextMuted, title);
        var fights = Fights();
        if (fights.Count == 0)
            ImGui.TextDisabled(Loc.T("No pull in the History yet."));
        foreach (var (territory, encounter, pulls) in fights.Take(40))
        {
            if (ImGui.Selectable($"{plugin.ContentCategories.DisplayName(territory, encounter) ?? encounter}##{territory}"))
            {
                picked(territory, encounter, pulls);
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void Import(uint territory, string encounter, List<PullRecord> pulls)
    {
        var plan = MitigationPlans.FromSheet(ImGui.GetClipboardText() ?? string.Empty, out var error, PlanActions.Id);
        if (plan is null)
        {
            Say(Loc.T("Nothing to import: ") + Loc.T(error), true);
            return;
        }

        plan.TerritoryId = territory;
        plan.Encounter = encounter;
        plan.Name = Loc.F("{0} plan", encounter);
        var source = pulls.Where(p => p.BossCasts.Count > 0).OrderByDescending(p => p.DurationSeconds).FirstOrDefault();
        var linked = source is null ? 0 : MitigationPlans.LinkByName(plan, source.BossCasts, CastNames);
        sourcePullId = source?.Id;
        Config.MitigationPlans.Add(plan);
        Config.Save();
        Edit(plan);
        Say(Loc.F("Imported: {0} casts, {1} linked to a cast of the timeline by their name. Link the others in the editor (pen) to follow them during a pull.",
                  plan.Rows.Count, linked), false);
    }

    // ---- Editor

    private void DrawEditorBar(MitigationPlan plan)
    {
        if (Theme.IconButton("back", FontAwesomeIcon.ArrowLeft, Loc.T("Back to the plans")))
        {
            Config.Save();
            editingId = string.Empty;
            message = string.Empty;
            return;
        }

        ImGui.SameLine();
        if (Theme.IconButton("showplan", FontAwesomeIcon.Eye, Loc.T("Show this plan in its window")))
            ShowPlan(plan);

        ImGui.SameLine(0, Theme.Px(14));
        table.DrawMine(plan);
    }

    private void DrawEditor(MitigationPlan plan)
    {
        var name = plan.Name;
        ImGui.SetNextItemWidth(Theme.Px(260));
        if (ImGui.InputText(Loc.T("Name"), ref name, 80))
            plan.Name = name;
        SaveWhenDone();

        DrawComposition(plan);

        var source = SourcePull(plan);
        var casts = source is null ? [] : MitigationPlans.Numbered(source.BossCasts);

        var sideWidth = SideWidth;
        using (var rows = ImRaii.Child("##planrows", new Vector2(-sideWidth - ImGui.GetStyle().ItemSpacing.X, -1)))
        {
            if (rows)
            {
                editTab = table.DrawTabs(plan, editable: true);
                DrawRowsEditor(plan, casts);
            }
        }

        ImGui.SameLine();
        using (var side = ImRaii.Child("##plancasts", new Vector2(-1, -1), true))
        {
            if (side)
                DrawCastList(plan, source, casts);
        }
    }

    private void SaveWhenDone()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
            Config.Save();
    }

    private void DrawRowsEditor(MitigationPlan plan, List<(BossCast Cast, int Occurrence)> casts)
    {
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.ScrollX;
        using (var editTable = ImRaii.Table("##mitedit", plan.Columns.Count + 4, flags, new Vector2(-1, -ImGui.GetFrameHeightWithSpacing())))
        {
            if (editTable)
            {
                ImGui.TableSetupScrollFreeze(2, 1);
                ImGui.TableSetupColumn(Loc.T("Note"), ImGuiTableColumnFlags.WidthFixed, NoteWidth);
                ImGui.TableSetupColumn(Loc.T("Cast"), ImGuiTableColumnFlags.WidthFixed, EditCastWidth);
                ImGui.TableSetupColumn(Loc.T("Timeline cast"), ImGuiTableColumnFlags.WidthFixed, LinkWidth);
                for (var c = 0; c < plan.Columns.Count; c++)
                    ImGui.TableSetupColumn($"{plan.Columns[c]}##ecol{c}", ImGuiTableColumnFlags.WidthFixed, MitigationTable.CellWidth);
                ImGui.TableSetupColumn("##rowtools", ImGuiTableColumnFlags.WidthFixed, ToolsWidth());
                table.DrawHeaders(plan, 3, 1, plugin.PlanCheck.MyColumn(plan));

                var move = (From: -1, To: -1);
                var remove = -1;
                var inTab = Enumerable.Range(0, plan.Rows.Count).Where(r => plan.TabOf(plan.Rows[r]) == editTab).ToList();
                for (var k = 0; k < inTab.Count; k++)
                {
                    var i = inTab[k];
                    var row = plan.Rows[i];
                    using var id = ImRaii.PushId(i);

                    if (row.Separator)
                    {
                        DrawSeparatorRow(plan, row);
                        if (DrawRowTools(k, inTab, ref move))
                            remove = i;
                        continue;
                    }

                    // Where a part of the fight starts ("P1: Living Liquid", the gold line of the plan): its title, to
                    // rename (every row of the part) or clear.
                    if (row.Phase.Length > 0 && (i == 0 || plan.Rows[i - 1].Phase != row.Phase))
                        DrawPartTitle(plan, i);

                    ImGui.TableNextRow();
                    Theme.RowHover();

                    ImGui.TableNextColumn();
                    var rowTop = ImGui.GetCursorScreenPos().Y;
                    // The row's note: shown in the "?" next to its cast in the plan.
                    var note = row.Note;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("##note", ref note, 300))
                        row.Note = note;
                    SaveWhenDone();
                    if (ImGui.IsItemHovered() && row.Note.Length > 0)
                        ImGui.SetTooltip(row.Note);

                    ImGui.TableNextColumn();
                    var cast = row.Name;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputTextWithHint("##cast", row.Prepull ? Loc.T("Pre-pull") : string.Empty, ref cast, 80))
                        row.Name = cast;
                    SaveWhenDone();

                    ImGui.TableNextColumn();
                    if (row.Prepull)
                        DrawPrepullTime(row);
                    else
                    {
                        linkedPlan = plan;
                        DrawLinkButton(row, casts);
                    }

                    table.DrawCells(plan, row, i, rowTop, -1);
                    if (DrawRowTools(k, inTab, ref move))
                        remove = i;
                }

                if (move.From >= 0)
                {
                    (plan.Rows[move.From], plan.Rows[move.To]) = (plan.Rows[move.To], plan.Rows[move.From]);
                    Config.Save();
                }

                if (remove >= 0)
                {
                    plan.Rows.RemoveAt(remove);
                    Config.Save();
                }
            }
        }

        if (ImGui.Button(Loc.T("Add a row")))
        {
            plan.Rows.Add(new MitigationRow { Phase = plan.Rows.LastOrDefault()?.Phase ?? string.Empty, Tab = editTab });
            Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Add a pre-pull row")))
        {
            // After the pre-pull rows already there, at the top.
            var index = plan.Rows.FindLastIndex(r => r.Prepull) + 1;
            plan.Rows.Insert(index, new MitigationRow
            {
                Prepull = true, Name = Loc.T("Pre-pull"), Phase = plan.Rows.FirstOrDefault()?.Phase ?? string.Empty, Tab = editTab,
            });
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("A row for the skills used before the pull, with when they are used (8 s before...). One row per time."));

        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Add a separator")))
        {
            plan.Rows.Add(new MitigationRow { Separator = true, Phase = plan.Rows.LastOrDefault()?.Phase ?? string.Empty, Tab = editTab });
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("A line across the plan (a limit cut, an add phase, or the phases on one page), with a word on it if you like. Move it with the arrows."));
    }

    // The tab the editor shows (rows added go in it).
    private string editTab = string.Empty;

    // A separator in the editor: its label (may be empty), the line shown under it.
    private void DrawSeparatorRow(MitigationPlan plan, MitigationRow row)
    {
        ImGui.TableNextRow();
        Theme.RowHover();
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.GoldBright, Loc.T("Separator"));
        ImGui.TableNextColumn();
        var label = row.Name;
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.GoldBright))
        {
            if (ImGui.InputTextWithHint("##separator", Loc.T("Label (optional)"), ref label, 60))
                row.Name = label;
        }

        SaveWhenDone();
        ImGui.TableNextColumn();
        var pos = ImGui.GetCursorScreenPos();
        var y = pos.Y + ImGui.GetFrameHeight() / 2;
        ImGui.GetWindowDrawList().AddLine(new Vector2(pos.X, y), new Vector2(pos.X + ImGui.GetContentRegionAvail().X, y), Theme.U32(Theme.Gold, 0.6f), Theme.Px(1.5f));
        for (var c = 0; c < plan.Columns.Count; c++)
            ImGui.TableNextColumn();
    }

    // The arrows and the cross of a row: moved within its tab, or removed (true).
    private static bool DrawRowTools(int k, List<int> inTab, ref (int From, int To) move)
    {
        ImGui.TableNextColumn();
        if (Theme.IconButton("up", FontAwesomeIcon.ArrowUp, Loc.T("Move up")) && k > 0)
            move = (inTab[k], inTab[k - 1]);
        ImGui.SameLine(0, Theme.Px(2));
        if (Theme.IconButton("down", FontAwesomeIcon.ArrowDown, Loc.T("Move down")) && k < inTab.Count - 1)
            move = (inTab[k], inTab[k + 1]);
        ImGui.SameLine(0, Theme.Px(2));
        return Theme.IconButton("remove", FontAwesomeIcon.Times, Loc.T("Remove this row"), Theme.Bad);
    }

    // The composition of the group: one chip per position (its role and name, to change or remove), and "+" to add one.
    private void DrawComposition(MitigationPlan plan)
    {
        ImGui.SameLine(0, Theme.Px(20));
        ImGui.TextColored(Theme.TextMuted, Loc.T("Composition"));
        Theme.HelpMarker(Loc.T("The positions of the plan, by role: tanks get the tank skills, healers the healer ones, DPS every DPS skill. Any composition: two tanks and six DPS, three healers... Click a position to change its role or name, or remove it."));
        for (var c = 0; c < plan.Columns.Count; c++)
        {
            using var id = ImRaii.PushId($"pos{c}");
            ImGui.SameLine(0, Theme.Px(4));
            var role = plan.RoleOf(c).Role;
            var color = role switch
            {
                PlanRole.Tank => Theme.Tank,
                PlanRole.Healer => Theme.Healer,
                PlanRole.Melee or PlanRole.Ranged or PlanRole.Caster => Theme.Dps,
                _ => Theme.TextMuted,
            };
            if (Theme.Chip(plan.Columns[c], false, accent: color))
            {
                positionName = plan.Columns[c];
                ImGui.OpenPopup("##position");
            }

            DrawPositionMenu(plan, c);
        }

        ImGui.SameLine(0, Theme.Px(4));
        if (Theme.Chip("+", false))
            ImGui.OpenPopup("##addposition");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Add a position"));

        using var popup = ImRaii.Popup("##addposition");
        if (!popup)
            return;
        foreach (var (role, label) in RoleChoices)
        {
            if (ImGui.Selectable(label))
            {
                plan.AddColumn(role);
                Config.Save();
            }
        }
    }

    private static (PlanRole Role, string Label)[] RoleChoices =>
        [(PlanRole.Tank, Loc.T("Tank")), (PlanRole.Healer, Loc.T("Healer")), (PlanRole.Melee, Loc.T("DPS")), (PlanRole.Any, Loc.T("Other"))];

    private string positionName = string.Empty;

    // A position: its role, its name, or removed (Ctrl held).
    private void DrawPositionMenu(MitigationPlan plan, int column)
    {
        using var popup = ImRaii.Popup("##position");
        if (!popup)
            return;

        var current = plan.RoleOf(column).Role;
        foreach (var (role, label) in RoleChoices)
        {
            var on = MitigationCatalog.IsDps(role) ? MitigationCatalog.IsDps(current) : current == role;
            if (ImGui.RadioButton(label, on) && !on)
            {
                plan.SetRole(column, role);
                positionName = plan.Columns[column];
                Config.Save();
            }

            ImGui.SameLine();
        }

        ImGui.NewLine();
        ImGui.SetNextItemWidth(Theme.Px(160));
        if (ImGui.InputText(Loc.T("Name"), ref positionName, 30) && positionName.Trim().Length > 0)
            plan.Columns[column] = positionName.Trim();
        SaveWhenDone();

        var ctrl = ImGui.GetIO().KeyCtrl;
        if (ImGui.MenuItem(ctrl ? Loc.T("Remove this position") : Loc.T("Remove this position (hold Ctrl)"), false, ctrl))
        {
            RemoveColumn(plan, column);
            ImGui.CloseCurrentPopup();
        }
    }

    // A position removed: what the rows had in it goes, and your column and the jobs chosen follow the others' places.
    private void RemoveColumn(MitigationPlan plan, int column)
    {
        plan.RemoveColumn(column);
        if (Config.MitigationMyColumn.TryGetValue(plan.Id, out var mine))
        {
            if (mine == column)
                Config.MitigationMyColumn.Remove(plan.Id);
            else if (mine > column)
                Config.MitigationMyColumn[plan.Id] = mine - 1;
        }

        var jobs = Config.MitigationColumnJobs.Where(j => j.Key.StartsWith(plan.Id + ":", StringComparison.Ordinal)).ToList();
        foreach (var (key, job) in jobs)
        {
            Config.MitigationColumnJobs.Remove(key);
            var c = int.Parse(key[(plan.Id.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            if (c != column)
                Config.MitigationColumnJobs[$"{plan.Id}:{(c > column ? c - 1 : c)}"] = job;
        }

        Config.Save();
    }

    // The title of the part starting at this row, on a line of its own: renamed or cleared for all the rows of the part.
    private void DrawPartTitle(MitigationPlan plan, int first)
    {
        var old = plan.Rows[first].Phase;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.GoldBright, Loc.T("Part"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("A part of the fight (a gold line above its first cast in the plan). Clear it to remove the line."));

        ImGui.TableNextColumn();
        var title = old;
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.GoldBright))
            ImGui.InputText("##parttitle", ref title, 80);
        if (ImGui.IsItemDeactivatedAfterEdit() && title != old)
        {
            for (var i = first; i < plan.Rows.Count && plan.Rows[i].Phase == old; i++)
                plan.Rows[i].Phase = title.Trim();
            Config.Save();
        }

        ImGui.TableNextColumn();
        if (Theme.IconButton("clearpart", FontAwesomeIcon.Times, Loc.T("Remove this part title"), Theme.Bad))
        {
            for (var i = first; i < plan.Rows.Count && plan.Rows[i].Phase == old; i++)
                plan.Rows[i].Phase = string.Empty;
            Config.Save();
        }
    }

    // When the skills of a pre-pull row are used, in seconds before the pull.
    private void DrawPrepullTime(MitigationRow row)
    {
        var before = (float)(row.PrepullSeconds ?? 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputFloat("##prepullat", ref before, 1, 5, Loc.T("%.0f s before")))
            row.PrepullSeconds = before > 0 ? Math.Round(before, 1) : null;
        SaveWhenDone();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("When the skills of this row are used: seconds before the pull."));
    }

    private static float NoteWidth => Theme.Px(110);
    private static float EditCastWidth => Theme.Px(140);
    private static float LinkWidth => Theme.Px(150);
    private static float SideWidth => Theme.Px(300);

    // Width of the editor: its table of rows, then the casts of the pull on the right.
    private static float EditorWidth(MitigationPlan plan) =>
        MitigationTable.TableWidth([NoteWidth, EditCastWidth, LinkWidth, .. Enumerable.Repeat(MitigationTable.CellWidth, plan.Columns.Count), ToolsWidth()])
        + ImGui.GetStyle().ItemSpacing.X + SideWidth;

    // The three buttons of a row (up, down, remove), measured in the icon font.
    private static float ToolsWidth()
    {
        using var font = ImRaii.PushFont(Dalamud.Interface.UiBuilder.IconFont);
        var padding = ImGui.GetStyle().FramePadding.X * 2;
        return new[] { FontAwesomeIcon.ArrowUp, FontAwesomeIcon.ArrowDown, FontAwesomeIcon.Times }
                   .Sum(i => ImGui.CalcTextSize(i.ToIconString()).X + padding) + Theme.Px(2) * 2 + Theme.Px(4);
    }

    // The plan whose row is being linked (to leave out the casts its other rows have).
    private MitigationPlan? linkedPlan;

    private void DrawLinkButton(MitigationRow row, List<(BossCast Cast, int Occurrence)> casts)
    {
        var label = row.ActionId == 0
                        ? Loc.T("not linked")
                        : $"{GameNames.Action(row.ActionId, row.Action)} #{row.Occurrence}";
        using (ImRaii.PushColor(ImGuiCol.Text, row.ActionId == 0 ? Theme.TextFaint : Theme.Text))
        {
            if (ImGui.Button($"{label}##link", new Vector2(-1, 0)))
                ImGui.OpenPopup("##linkcast");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("The cast of the timeline this row stands for: the plan follows the pull with it."));

        using var popup = ImRaii.Popup("##linkcast");
        if (!popup)
            return;

        ImGui.SetNextItemWidth(Theme.Px(220));
        ImGui.InputTextWithHint("##linkfilter", Loc.T("Search a cast"), ref castFilter, 64);
        if (ImGui.Selectable(Loc.T("Not linked")))
            MitigationPlans.Unlink(row);
        using var list = ImRaii.Child("##linklist", new Vector2(Theme.Px(320), Theme.Px(300)));
        foreach (var (cast, occurrence) in Filtered(casts))
        {
            // A cast already in another row of the plan cannot be linked again.
            var mine = row.ActionId == cast.ActionId && row.Occurrence == occurrence;
            var taken = !mine && linkedPlan?.Rows.Any(r => r.ActionId == cast.ActionId && r.Occurrence == occurrence) == true;
            if (ImGui.Selectable(CastLabel(cast, occurrence), mine, taken ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
            {
                MitigationPlans.Link(row, cast, occurrence);
                if (row.Name.Length == 0)
                    row.Name = GameNames.Action(cast.ActionId, cast.Action);
                Config.Save();
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void DrawCastList(MitigationPlan plan, PullRecord? source, List<(BossCast Cast, int Occurrence)> casts)
    {
        ImGui.TextColored(Theme.Gold, Loc.T("Casts of the pull"));
        Theme.HelpMarker(Loc.T("Click a cast to add it as a row, at its time. The pull is the longest of the fight unless you pick another."));

        var pulls = plugin.History.Pulls.Where(p => p.TerritoryId == plan.TerritoryId && p.BossCasts.Count > 0)
                          .OrderByDescending(p => p.StartUtc).ToList();
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##sourcepull", source is null ? Loc.T("No pull with casts") : PullLabel(source)))
        {
            if (combo)
            {
                foreach (var p in pulls)
                {
                    if (ImGui.Selectable($"{PullLabel(p)}##{p.Id}", p.Id == source?.Id))
                        sourcePullId = p.Id;
                }
            }
        }

        if (source is null)
        {
            Theme.Hint(Loc.T("No pull of this fight with its casts in the History."));
            return;
        }

        if (ImGui.Button(Loc.T("Link the rows by name")))
        {
            var linked = MitigationPlans.LinkByName(plan, source.BossCasts, CastNames);
            Config.Save();
            Say(Loc.F("{0} rows linked by their name.", linked), false);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Links the rows not linked yet to the casts of this pull named like them (the second row named like a cast goes to its second use)."));

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##castfilter", Loc.T("Search a cast"), ref castFilter, 64);

        foreach (var (cast, occurrence) in Filtered(casts))
        {
            // Each attack once in the plan.
            var inPlan = plan.Rows.Any(r => r.ActionId == cast.ActionId && r.Occurrence == occurrence);
            if (ImGui.Selectable($"{CastLabel(cast, occurrence)}##{cast.ActionId}-{occurrence}", false,
                                 inPlan ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                AddRow(plan, cast, occurrence);

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(inPlan ? Loc.T("Already in the plan.") : Loc.F("{0}, cast by {1}", GameNames.Action(cast.ActionId, cast.Action), cast.Caster));
        }
    }

    // A row for this cast, placed by its time among the rows that have one (after the pre-pull).
    private void AddRow(MitigationPlan plan, BossCast cast, int occurrence)
    {
        var index = plan.Rows.FindLastIndex(r => r.Prepull || (r.AtSeconds is { } t && t <= cast.AtSeconds)) + 1;
        if (index <= 1 && plan.Rows.Count > 0 && plan.Rows.All(r => r.Prepull || r.AtSeconds is null))
            index = plan.Rows.Count;
        var row = new MitigationRow
        {
            Name = GameNames.Action(cast.ActionId, cast.Action),
            Phase = index > 0 ? plan.Rows[index - 1].Phase : string.Empty,
            Tab = editTab,
        };
        MitigationPlans.Link(row, cast, occurrence);
        plan.Rows.Insert(index, row);
        Config.Save();
    }

    private IEnumerable<(BossCast Cast, int Occurrence)> Filtered(List<(BossCast Cast, int Occurrence)> casts) =>
        castFilter.Length == 0
            ? casts
            : casts.Where(c => GameNames.Action(c.Cast.ActionId, c.Cast.Action).Contains(castFilter, StringComparison.OrdinalIgnoreCase));

    private static string CastLabel(BossCast cast, int occurrence) =>
        $"{NumberFormat.Duration(TimeSpan.FromSeconds(cast.AtSeconds))}  {GameNames.Action(cast.ActionId, cast.Action)}" +
        (occurrence > 1 ? $" #{occurrence}" : string.Empty) + (cast.CastSeconds > 0 ? string.Empty : "  " + Loc.T("(instant)"));

    private static string PullLabel(PullRecord pull) =>
        $"{pull.StartUtc.ToLocalTime():dd/MM HH:mm}  ({NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds))})";
}
