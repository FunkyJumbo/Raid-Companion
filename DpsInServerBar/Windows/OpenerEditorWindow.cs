using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Opener;
using DpsInServerBar.UI;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace DpsInServerBar.Windows;

/// <summary>
/// Opener editor: make your own openers, edit them, copy a built-in one to change it, record one by playing it, and
/// share them with a code. Your openers show up in the opener practice next to the built-in ones.
/// </summary>
public sealed class OpenerEditorWindow : Window
{
    private readonly Plugin plugin;
    private readonly Dictionary<uint, (uint Icon, string Name)> actions = new();
    private IDisposable? themeScope;

    private string job = string.Empty;
    private string selectedId = string.Empty;
    private string search = string.Empty;
    private string importCode = string.Empty;
    private string message = string.Empty;
    private bool messageIsError;
    private string notesBuffer = string.Empty;
    private string notesFor = string.Empty;

    // Recording: each action you use is added to the opener; the ones used before the pull get their time before it.
    private bool recording;
    private readonly List<(CustomOpenerStep Step, DateTime At)> recordedBeforePull = [];
    private bool wasInCombat;

    public OpenerEditorWindow(Plugin plugin)
        : base(Loc.T("Opener editor###DpsInServerBarOpenerEditor"))
    {
        Loc.Changed += () =>
        {
            WindowName = Loc.T("Opener editor###DpsInServerBarOpenerEditor");
            actions.Clear();
        };
        this.plugin = plugin;
        Size = new Vector2(900, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 400), MaximumSize = new Vector2(4000, 4000) };
        AllowBackgroundBlur = false;
        plugin.OpenerTracker.ActionUsed += OnActionUsed;
    }

    /// <summary>True while recording: the opener practice hook must stay on.</summary>
    public bool Recording => recording && IsOpen;

    /// <summary>Opens the editor on a job and, if given, one of its openers.</summary>
    public void Open(string forJob, string? openerId = null)
    {
        if (forJob.Length > 0)
            job = forJob;
        if (openerId is not null)
            selectedId = openerId;
        IsOpen = true;
        BringToFront();
    }

    public override void OnOpen()
    {
        if (job.Length == 0)
            job = CurrentJob() is { Length: > 0 } current ? current : CustomOpeners.CombatJobs().FirstOrDefault() ?? "PLD";
    }

    public override void OnClose() => SetRecording(false);

    public override void PreDraw() => themeScope = Theme.Push();

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        FollowPull();
        DrawTopBar();
        Theme.Rule();

        var openers = CustomOpeners.ForJob(plugin.Configuration, job);
        var listWidth = Theme.Px(250);
        using (var list = ImRaii.Child("##openerlist", new Vector2(listWidth, -1), true))
        {
            if (list)
                DrawList(openers);
        }

        ImGui.SameLine();
        using var page = ImRaii.Child("##openerpage", new Vector2(-1, -1));
        if (!page)
            return;

        var custom = plugin.Configuration.CustomOpeners.FirstOrDefault(o => o.Id == selectedId && o.Job == job);
        if (custom is not null)
            DrawEditor(custom);
        else if (openers.FirstOrDefault(o => o.Id == selectedId) is { } builtIn)
            DrawBuiltIn(builtIn);
        else
            Theme.Hint(Loc.T("Pick an opener on the left: a built-in one to see it or copy it, one of yours to edit it. Or make a new one, or import a code."));
    }

    // ------------------------------------------------------------------ Top bar and list

    private void DrawTopBar()
    {
        JobInfo.DrawIcon(job, ImGui.GetFrameHeight());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Px(110));
        using (var combo = ImRaii.Combo("##editorjob", job))
        {
            if (combo)
            {
                foreach (var j in CustomOpeners.CombatJobs())
                {
                    if (ImGui.Selectable(j, j == job))
                    {
                        job = j;
                        selectedId = string.Empty;
                        SetRecording(false);
                    }
                }
            }
        }

        ImGui.SameLine();
        if (Theme.PrimaryButton(Loc.T("New opener")))
        {
            var opener = new CustomOpener { Job = job, Name = Loc.T("My opener") };
            Add(opener);
        }

        // Import: paste a code in the field (or straight from the clipboard).
        ImGui.SameLine(0, Theme.Px(16));
        ImGui.SetNextItemWidth(Theme.Px(220));
        ImGui.InputTextWithHint("##importcode", Loc.T("Paste an opener code"), ref importCode, 8192);
        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Import")))
        {
            var code = importCode.Trim().Length > 0 ? importCode : ImGui.GetClipboardText();
            if (CustomOpeners.Import(code ?? string.Empty, out var error) is { } imported)
            {
                Add(imported);
                job = imported.Job;
                importCode = string.Empty;
                Say(Loc.F("Imported: {0} ({1}).", imported.Name, imported.Job));
            }
            else
            {
                Say(error, isError: true);
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Imports the code pasted in the field, or the one in your clipboard when the field is empty."));

        if (message.Length > 0)
        {
            ImGui.SameLine(0, Theme.Px(12));
            ImGui.TextColored(messageIsError ? Theme.Bad : Theme.Good, message);
        }
    }

    private void DrawList(List<OpenerDefinition> openers)
    {
        if (openers.Count == 0)
            Theme.Hint(Loc.F("No opener for {0} yet: make one with New opener.", job));

        var builtInShown = false;
        var customShown = false;
        foreach (var o in openers)
        {
            var custom = CustomOpeners.IsCustom(o.Id);
            if (!custom && !builtInShown)
            {
                ImGui.TextColored(Theme.TextMuted, Loc.T("Built-in"));
                builtInShown = true;
            }
            else if (custom && !customShown)
            {
                if (builtInShown)
                    ImGui.Spacing();
                ImGui.TextColored(Theme.TextMuted, Loc.T("Yours"));
                customShown = true;
            }

            if (ImGui.Selectable($"{Loc.T(o.Name)}##{o.Id}", o.Id == selectedId))
            {
                selectedId = o.Id;
                SetRecording(false);
            }
        }
    }

    // ------------------------------------------------------------------ Built-in opener (read only)

    private void DrawBuiltIn(OpenerDefinition opener)
    {
        ImGui.TextColored(Theme.GoldBright, Loc.T(opener.Name));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextFaint, opener.Source);
        Theme.Hint(Loc.T("Built-in openers cannot be changed: copy it to make your own version."));

        if (Theme.PrimaryButton(Loc.T("Copy to edit")))
            Add(CustomOpeners.Copy(opener));
        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Practice")))
            Practice(opener.Id);

        ImGui.Spacing();
        foreach (var (step, index) in opener.Steps.Select((s, i) => (s, i)))
        {
            var id = step.Kind == StepKind.Potion ? 0 : ActionNames.Find(step.Names[0]);
            DrawStepLine(index, id, step.Kind, step.Prepull);
        }

        DrawNotesReadOnly(opener.Notes);
    }

    private void DrawNotesReadOnly(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
            return;
        ImGui.Spacing();
        ImGui.TextColored(Theme.TextMuted, Loc.T("Notes"));
        foreach (var note in notes)
            ImGui.TextWrapped("• " + Loc.T(note));
    }

    // ------------------------------------------------------------------ Your opener

    private void DrawEditor(CustomOpener opener)
    {
        var config = plugin.Configuration;
        var name = opener.Name;
        ImGui.SetNextItemWidth(Theme.Px(260));
        if (ImGui.InputText(Loc.T("Name"), ref name, 64))
        {
            opener.Name = name;
            Changed(opener, save: false);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        // Share, practice, delete.
        if (Theme.PrimaryButton(Loc.T("Copy share code")))
        {
            ImGui.SetClipboardText(CustomOpeners.Export(opener));
            Say(Loc.T("Code copied: paste it anywhere, the other player imports it in their editor."));
        }

        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Practice")))
            Practice(opener.Id);
        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Duplicate")))
        {
            var copy = CustomOpeners.Copy(CustomOpeners.ToDefinition(opener));
            Add(copy);
        }

        ImGui.SameLine();
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!ctrl))
        {
            if (Theme.GhostButton(Loc.T("Delete")))
            {
                config.CustomOpeners.Remove(opener);
                selectedId = string.Empty;
                SetRecording(false);
                config.Save();
                Say(Loc.F("{0} deleted.", opener.Name));
                return;
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("Hold Ctrl to delete this opener."));

        Theme.SectionHeader(Loc.F("Steps ({0})", opener.Steps.Count));
        DrawRecording(opener);
        DrawSteps(opener);
        DrawAddActions(opener);
        DrawNotesEditor(opener);
    }

    private void DrawRecording(CustomOpener opener)
    {
        if (recording)
        {
            if (Theme.PrimaryButton(Loc.T("Stop recording")))
                SetRecording(false);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, Loc.T("Recording: every action you use is added at the end."));
        }
        else
        {
            if (Theme.GhostButton(Loc.T("Record")))
                SetRecording(true);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.T("Play your opener on a striking dummy: each action you use is added at the end, GCD or oGCD read from the game. Actions used before the pull get their time before it. Fix the details by hand afterwards."));
            }

            if (opener.Steps.Count > 0)
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
                {
                    if (Theme.GhostButton(Loc.T("Clear the steps")))
                    {
                        opener.Steps.Clear();
                        Changed(opener);
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Loc.T("Hold Ctrl to remove every step (e.g. before recording again)."));
            }
        }
    }

    private void DrawSteps(CustomOpener opener)
    {
        if (opener.Steps.Count == 0)
        {
            Theme.Hint(Loc.T("No step yet: add actions from the list below, or record them."));
            return;
        }

        using var table = ImRaii.Table("##steps", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Theme.Px(24));
        ImGui.TableSetupColumn(Loc.T("Action"), ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn(Loc.T("Kind"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(100));
        ImGui.TableSetupColumn(Loc.T("Pre-pull"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(110));
        ImGui.TableSetupColumn("##move", ImGuiTableColumnFlags.WidthFixed, Theme.Px(56));
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, Theme.Px(26));
        ImGui.TableHeadersRow();

        string[] kinds = [Loc.T("GCD"), Loc.T("oGCD"), Loc.T("Tincture")];
        int? moveFrom = null, moveTo = null, remove = null;
        for (var i = 0; i < opener.Steps.Count; i++)
        {
            var step = opener.Steps[i];
            using var id = ImRaii.PushId(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, (i + 1).ToString());

            ImGui.TableNextColumn();
            var (icon, name) = step.Kind == StepKind.Potion ? (0u, Loc.T("Tincture")) : ActionInfo(step.ActionIds.FirstOrDefault());
            ActionIcons.Draw(icon, ImGui.GetFrameHeight());
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(name);
            if (step.ActionIds.Count > 1 && ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Any of these counts: ") + string.Join(", ", step.ActionIds.Select(a => ActionInfo(a).Name)));

            ImGui.TableNextColumn();
            var kind = (int)step.Kind;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##kind", ref kind, kinds, kinds.Length))
            {
                step.Kind = (StepKind)kind;
                Changed(opener);
            }

            ImGui.TableNextColumn();
            var before = step.Prepull is not null;
            if (ImGui.Checkbox("##prepull", ref before))
            {
                step.Prepull = before ? 1 : null;
                Changed(opener);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Used before the pull, this many seconds before it."));

            if (step.Prepull is { } seconds)
            {
                ImGui.SameLine();
                var value = (float)seconds;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.DragFloat("##seconds", ref value, 0.05f, 0, 30, "-%.2f s"))
                {
                    step.Prepull = Math.Round(Math.Clamp(value, 0, 30), 2);
                    Changed(opener, save: false);
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                    plugin.Configuration.Save();
            }

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(i == 0))
            {
                if (Theme.IconButton("up", FontAwesomeIcon.ArrowUp, Loc.T("Move up")))
                    (moveFrom, moveTo) = (i, i - 1);
            }

            ImGui.SameLine(0, Theme.Px(2));
            using (ImRaii.Disabled(i == opener.Steps.Count - 1))
            {
                if (Theme.IconButton("down", FontAwesomeIcon.ArrowDown, Loc.T("Move down")))
                    (moveFrom, moveTo) = (i, i + 1);
            }

            ImGui.TableNextColumn();
            if (Theme.IconButton("remove", FontAwesomeIcon.Times, Loc.T("Remove"), Theme.Bad))
                remove = i;
        }

        if (moveFrom is { } from && moveTo is { } to)
        {
            (opener.Steps[from], opener.Steps[to]) = (opener.Steps[to], opener.Steps[from]);
            Changed(opener);
        }

        if (remove is { } r)
        {
            opener.Steps.RemoveAt(r);
            Changed(opener);
        }
    }

    /// <summary>The job's actions, searchable by name; a click adds one at the end. The tincture has its own button.</summary>
    private void DrawAddActions(CustomOpener opener)
    {
        Theme.SectionHeader(Loc.T("Add a step"));
        ImGui.SetNextItemWidth(Theme.Px(240));
        ImGui.InputTextWithHint("##search", Loc.T("Search an action"), ref search, 64);
        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Add the tincture")))
        {
            opener.Steps.Add(new CustomOpenerStep { Kind = StepKind.Potion });
            Changed(opener);
        }

        var filter = search.Trim();
        var shown = CustomOpeners.ActionsOf(opener.Job)
                                 .Select(id => (Id: id, Info: ActionInfo(id)))
                                 .Where(a => filter.Length == 0 || a.Info.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                                 || ActionNames.Of(a.Id).Contains(filter, StringComparison.OrdinalIgnoreCase))
                                 .ToList();

        var size = Theme.Px(36);
        var spacing = Theme.Px(4);
        var perLine = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (size + spacing)));
        for (var i = 0; i < shown.Count; i++)
        {
            if (i % perLine != 0)
                ImGui.SameLine(0, spacing);
            var (id, info) = shown[i];
            using var pushId = ImRaii.PushId((int)id);
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##add", new Vector2(size, size)))
            {
                opener.Steps.Add(new CustomOpenerStep { ActionIds = [id], Kind = CustomOpeners.IsGcd(id) ? StepKind.Gcd : StepKind.Ogcd });
                Changed(opener);
            }

            var hovered = ImGui.IsItemHovered();
            ActionIcons.DrawAt(ImGui.GetWindowDrawList(), info.Icon, pos, size);
            if (hovered)
            {
                ImGui.GetWindowDrawList().AddRect(pos, pos + new Vector2(size, size), Theme.U32(Theme.GoldBright), Theme.Px(4), ImDrawFlags.None, Theme.Px(2));
                ImGui.SetTooltip(Loc.F("{0} ({1}): click to add it at the end", info.Name, CustomOpeners.IsGcd(id) ? Loc.T("GCD") : Loc.T("oGCD")));
            }
        }

        if (shown.Count == 0)
            Theme.Hint(Loc.T("No action matches."));
    }

    private void DrawNotesEditor(CustomOpener opener)
    {
        Theme.SectionHeader(Loc.T("Notes"));
        if (notesFor != opener.Id)
        {
            notesFor = opener.Id;
            notesBuffer = string.Join("\n", opener.Notes);
        }

        if (ImGui.InputTextMultiline("##notes", ref notesBuffer, 4096, new Vector2(-1, Theme.Px(90))))
        {
            opener.Notes = notesBuffer.Split('\n').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
            Changed(opener, save: false);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.Configuration.Save();
        Theme.Hint(Loc.T("One note per line, shown under the opener in the practice window."));
    }

    /// <summary>A read-only step: number, icon, name, kind and time before the pull.</summary>
    private void DrawStepLine(int index, uint actionId, StepKind kind, double? prepull)
    {
        ImGui.TextColored(Theme.TextFaint, $"{index + 1,2}");
        ImGui.SameLine();
        var (icon, name) = kind == StepKind.Potion ? (0u, Loc.T("Tincture")) : ActionInfo(actionId);
        ActionIcons.Draw(icon, ImGui.GetTextLineHeight() * 1.3f);
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextFaint, kind switch { StepKind.Gcd => Loc.T("GCD"), StepKind.Ogcd => Loc.T("oGCD"), _ => string.Empty }
                                           + (prepull is { } p ? $"  -{p:0.##} s" : string.Empty));
    }

    // ------------------------------------------------------------------ Recording

    private void SetRecording(bool on)
    {
        if (recording == on)
            return;
        recording = on;
        recordedBeforePull.Clear();
        wasInCombat = Plugin.Condition[ConditionFlag.InCombat];
        plugin.UpdateOpenerTracking();
    }

    private void OnActionUsed(byte type, uint id)
    {
        if (!Recording || plugin.Configuration.CustomOpeners.FirstOrDefault(o => o.Id == selectedId) is not { } opener)
            return;

        CustomOpenerStep step;
        if (type == 2)
            step = new CustomOpenerStep { Kind = StepKind.Potion };
        else if (CustomOpeners.ActionsOf(opener.Job).Contains(id) || ActionNames.Of(id).Length > 0)
            step = new CustomOpenerStep { ActionIds = [id], Kind = CustomOpeners.IsGcd(id) ? StepKind.Gcd : StepKind.Ogcd };
        else
            return;

        // Before the pull: its time before it is known once the fight starts.
        if (!Plugin.Condition[ConditionFlag.InCombat])
        {
            step.Prepull = 0;
            recordedBeforePull.Add((step, DateTime.UtcNow));
        }

        opener.Steps.Add(step);
        Changed(opener);
    }

    /// <summary>When the fight starts while recording, the actions used before it get their time before the pull.</summary>
    private void FollowPull()
    {
        var inCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (recording && inCombat && !wasInCombat && recordedBeforePull.Count > 0)
        {
            var pull = DateTime.UtcNow;
            foreach (var (step, at) in recordedBeforePull)
                step.Prepull = Math.Round(Math.Max(0, (pull - at).TotalSeconds), 1);
            recordedBeforePull.Clear();
            if (plugin.Configuration.CustomOpeners.FirstOrDefault(o => o.Id == selectedId) is { } opener)
                Changed(opener);
        }

        wasInCombat = inCombat;
    }

    // ------------------------------------------------------------------ Helpers

    private void Add(CustomOpener opener)
    {
        plugin.Configuration.CustomOpeners.Add(opener);
        plugin.Configuration.Save();
        job = opener.Job;
        selectedId = opener.Id;
        SetRecording(false);
    }

    private void Changed(CustomOpener opener, bool save = true)
    {
        opener.Revision++;
        if (save)
            plugin.Configuration.Save();
    }

    /// <summary>Selects this opener in the practice window and opens it.</summary>
    private void Practice(string openerId)
    {
        plugin.Configuration.OpenerSelected[job] = openerId;
        plugin.Configuration.Save();
        plugin.OpenOpener();
    }

    private void Say(string text, bool isError = false) => (message, messageIsError) = (text, isError);

    private (uint Icon, string Name) ActionInfo(uint id)
    {
        if (actions.TryGetValue(id, out var info))
            return info;

        info = id != 0 && Plugin.DataManager.GetExcelSheet<Action>(Loc.GameLanguage).TryGetRow(id, out var row)
            ? (row.Icon, Loc.Game(row.Name.ExtractText()))
            : (0u, Loc.T("Unknown action"));
        actions[id] = info;
        return info;
    }

    private static string CurrentJob()
    {
        var id = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        return id != 0 && Plugin.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English).TryGetRow(id, out var row)
            ? row.Abbreviation.ExtractText()
            : string.Empty;
    }
}
