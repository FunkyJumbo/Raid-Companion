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
    private string columnsBuffer = string.Empty;
    private string columnsFor = string.Empty;

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
        var buttons = ImGui.CalcTextSize(Loc.T("Show")).X + ImGui.CalcTextSize(Loc.T("Edit")).X
                      + ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X + ImGui.GetFrameHeight() * 2;
        using var list = ImRaii.Child("##planlist", new Vector2(-1, -1));
        foreach (var fight in fights)
        {
            var encounter = fight.First().Encounter;
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
            if (ImGui.Selectable($"{encounter}##{territory}"))
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

        ImGui.SameLine(0, Theme.Px(20));
        if (columnsFor != plan.Id)
        {
            columnsFor = plan.Id;
            columnsBuffer = string.Join(", ", plan.Columns);
        }

        ImGui.SetNextItemWidth(Theme.Px(360));
        ImGui.InputText(Loc.T("Columns"), ref columnsBuffer, 300);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var columns = columnsBuffer.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (columns.Count > 0)
                plan.Columns = columns;
            columnsBuffer = string.Join(", ", plan.Columns);
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Who does what, separated by commas: roles (MT, OT, H1, H2, M1, M2, R1, R2) or jobs. The role of a column decides the skills offered in its cells."));

        var source = SourcePull(plan);
        var casts = source is null ? [] : MitigationPlans.Numbered(source.BossCasts);

        var sideWidth = Theme.Px(300);
        using (var rows = ImRaii.Child("##planrows", new Vector2(-sideWidth - ImGui.GetStyle().ItemSpacing.X, -1)))
        {
            if (rows)
                DrawRowsEditor(plan, casts);
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
                ImGui.TableSetupColumn(Loc.T("Part"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(90));
                ImGui.TableSetupColumn(Loc.T("Cast"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(140));
                ImGui.TableSetupColumn(Loc.T("Timeline cast"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(150));
                for (var c = 0; c < plan.Columns.Count; c++)
                    ImGui.TableSetupColumn($"{plan.Columns[c]}##ecol{c}", ImGuiTableColumnFlags.WidthFixed, MitigationTable.CellWidth);
                ImGui.TableSetupColumn("##rowtools", ImGuiTableColumnFlags.WidthFixed, ToolsWidth());
                table.DrawHeaders(plan, 3, 1, plugin.PlanCheck.MyColumn(plan));

                var move = (From: -1, To: -1);
                var remove = -1;
                for (var i = 0; i < plan.Rows.Count; i++)
                {
                    var row = plan.Rows[i];
                    using var id = ImRaii.PushId(i);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var rowTop = ImGui.GetCursorScreenPos().Y;
                    var phase = row.Phase;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("##phase", ref phase, 80))
                        row.Phase = phase;
                    SaveWhenDone();

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
                        DrawLinkButton(row, casts);

                    table.DrawCells(plan, row, i, rowTop, -1);

                    ImGui.TableNextColumn();
                    if (Theme.IconButton("up", FontAwesomeIcon.ArrowUp, Loc.T("Move up")) && i > 0)
                        move = (i, i - 1);
                    ImGui.SameLine(0, Theme.Px(2));
                    if (Theme.IconButton("down", FontAwesomeIcon.ArrowDown, Loc.T("Move down")) && i < plan.Rows.Count - 1)
                        move = (i, i + 1);
                    ImGui.SameLine(0, Theme.Px(2));
                    if (Theme.IconButton("remove", FontAwesomeIcon.Times, Loc.T("Remove this row"), Theme.Bad))
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
            plan.Rows.Add(new MitigationRow { Phase = plan.Rows.LastOrDefault()?.Phase ?? string.Empty });
            Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Add a pre-pull row")))
        {
            // After the pre-pull rows already there, at the top.
            var index = plan.Rows.FindLastIndex(r => r.Prepull) + 1;
            plan.Rows.Insert(index, new MitigationRow
            {
                Prepull = true, Name = Loc.T("Pre-pull"), Phase = plan.Rows.FirstOrDefault()?.Phase ?? string.Empty,
            });
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("A row for the skills used before the pull, with when they are used (8 s before...). One row per time."));
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

    // The three buttons of a row (up, down, remove), measured in the icon font.
    private static float ToolsWidth()
    {
        using var font = ImRaii.PushFont(Dalamud.Interface.UiBuilder.IconFont);
        var padding = ImGui.GetStyle().FramePadding.X * 2;
        return new[] { FontAwesomeIcon.ArrowUp, FontAwesomeIcon.ArrowDown, FontAwesomeIcon.Times }
                   .Sum(i => ImGui.CalcTextSize(i.ToIconString()).X + padding) + Theme.Px(2) * 2 + Theme.Px(4);
    }

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
            if (ImGui.Selectable(CastLabel(cast, occurrence), row.ActionId == cast.ActionId && row.Occurrence == occurrence))
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
            var inPlan = plan.Rows.Any(r => r.ActionId == cast.ActionId && r.Occurrence == occurrence);
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.TextFaint, inPlan))
            {
                if (ImGui.Selectable($"{CastLabel(cast, occurrence)}##{cast.ActionId}-{occurrence}"))
                    AddRow(plan, cast, occurrence);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(inPlan ? Loc.T("Already in the plan. Click to add it again.") : Loc.F("{0}, cast by {1}", GameNames.Action(cast.ActionId, cast.Action), cast.Caster));
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
