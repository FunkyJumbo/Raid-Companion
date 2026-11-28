using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Gear;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// Best in slot gear sets, per job: the set tracked (what you own of it, and its materia), an editor (items up to an
/// item level, materia, food, notes), imports from xivgear.app and etro.gg, and the items framed in the game's windows.
/// </summary>
public sealed class GearWindow : Window
{
    private readonly Plugin plugin;
    private readonly WindowShot shot;
    private IDisposable? themeScope;

    private string job = string.Empty;
    private bool editing;
    private string link = string.Empty;
    private Task<List<GearSet>>? import;
    private bool importRefused; // the link field shakes
    private string message = string.Empty;
    private bool messageIsError;

    // The name typed to rename the set shown.
    private string rename = string.Empty;

    // The item picker open: its search.
    private string pickSearch = string.Empty;

    public GearWindow(Plugin plugin) : base(Loc.T("BiS gearsets###DpsInServerBarGear"))
    {
        Loc.Changed += () =>
        {
            WindowName = Loc.T("BiS gearsets###DpsInServerBarGear");
            GearCatalog.Reset();
            GearStats.Reset();
        };
        this.plugin = plugin;
        Size = new Vector2(760, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 320), MaximumSize = new Vector2(5000, 5000) };
        // Always the size of its content: neither resized nor scrolled.
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        AllowBackgroundBlur = false;
        shot = new WindowShot(this);
        TitleBarButtons.Add(shot.TitleBarButton(() => Selected()?.Name ?? Loc.T("BiS gearsets")));
    }

    private Configuration Config => plugin.Configuration;

    /// <summary>Opens on the job you play.</summary>
    public void Show()
    {
        var current = GearCatalog.CurrentJob();
        if (current.Length > 0)
            job = current;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        themeScope = Theme.Push(WindowName);

        // Exactly the size of the content (measured on the last frame), within the screen.
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        if (fitWidth > 0 && fitHeight > 0)
        {
            var screen = ImGui.GetMainViewport().Size / scale;
            Size = new Vector2(Math.Clamp(fitWidth / scale, 520, Math.Max(520, screen.X)), Math.Clamp(fitHeight / scale, 320, Math.Max(320, screen.Y)));
            SizeCondition = ImGuiCond.Always;
        }

        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 320), MaximumSize = new Vector2(5000, 5000) };
        shot.PreDraw();
    }

    // The content's size, as wide as its tables and the bar's items, as tall as all of it (0 before the first frame).
    private float fitWidth, fitHeight, contentRight;

    // The last table's right edge, in the window.
    private void FitWidth() =>
        contentRight = Math.Max(contentRight, ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetScrollX());

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    private List<GearSet> SetsOf(string j) => Config.GearSets.Where(s => s.Job == j).ToList();

    // The set shown for the job: the one tracked, else the last one shown, else the first.
    // The set shown for each job (looking at one does not change the set tracked).
    private readonly Dictionary<string, string> viewed = new();

    // The set shown for the job: the one looked at last, else the one tracked, else the first.
    private GearSet? Selected()
    {
        var sets = SetsOf(job);
        return (viewed.TryGetValue(job, out var shown) ? sets.FirstOrDefault(s => s.Id == shown) : null)
               ?? Tracked(job)
               ?? sets.FirstOrDefault();
    }

    private GearSet? Tracked(string j) =>
        Config.GearTracked.TryGetValue(j, out var id) ? Config.GearSets.FirstOrDefault(s => s.Id == id && s.Job == j) : null;

    // Shows a set (and tracks it when its job tracks none yet).
    private void Select(GearSet set)
    {
        viewed[set.Job] = set.Id;
        if (Tracked(set.Job) is null)
            Track(set);
        Config.Save();
    }

    private void Track(GearSet set)
    {
        Config.GearTracked[set.Job] = set.Id;
        Config.Save();
    }

    private void Say(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    public override void Draw()
    {
        contentRight = 0;
        DrawContent();
        var style = ImGui.GetStyle();
        fitWidth = contentRight + style.WindowPadding.X;
        fitHeight = ImGui.GetCursorPosY() - style.ItemSpacing.Y + style.WindowPadding.Y;
    }

    private void DrawContent()
    {
        shot.Step();
        Theme.BlurBehind();
        Ambience.Draw();
        Backdrop.Draw(Backdrop.Kind.Hive);
        if (job.Length == 0)
            job = GearCatalog.CurrentJob() is { Length: > 0 } current ? current : GearCatalog.Jobs.FirstOrDefault().Abbreviation ?? string.Empty;

        PollImport();
        DrawBar();
        if (message.Length > 0)
            ImGui.TextColored(messageIsError ? Theme.Bad : Theme.Good, message);
        Theme.Rule(2);

        if (Selected() is not { } set)
        {
            Theme.Hint(Loc.T("No set for this job yet: make one (+), or import a link of xivgear.app or etro.gg (paste it above, then Import)."));
            return;
        }

        if (editing)
            DrawEditor(set);
        else
            DrawTracker(set);
    }

    // ---- The bar: job, set, new / duplicate / delete, import, edit, highlight.

    private void DrawBar()
    {
        // Job.
        ImGui.SetNextItemWidth(Theme.Px(150));
        var jobName = GearCatalog.Jobs.FirstOrDefault(j => j.Abbreviation == job).Name ?? job;
        using (var combo = ImRaii.Combo("##gearjob", jobName))
        {
            if (combo)
            {
                foreach (var (_, abbreviation, name) in GearCatalog.Jobs)
                {
                    JobInfo.DrawIcon(abbreviation, ImGui.GetTextLineHeight());
                    ImGui.SameLine();
                    var count = SetsOf(abbreviation).Count;
                    if (ImGui.Selectable(count > 0 ? $"{name} ({count})##{abbreviation}" : $"{name}##{abbreviation}", abbreviation == job))
                        job = abbreviation;
                }
            }
        }

        // Set.
        ImGui.SameLine();
        var sets = SetsOf(job);
        var selected = Selected();
        var tracked = Tracked(job);
        ImGui.SetNextItemWidth(Theme.Px(260));
        using (ImRaii.Disabled(sets.Count == 0))
        using (var combo = ImRaii.Combo("##gearset", selected?.Name ?? Loc.T("No set")))
        {
            if (combo)
            {
                foreach (var s in sets)
                {
                    // The tracked set marked.
                    var entry = s == tracked ? $"{s.Name}  ({Loc.T("tracked")})##{s.Id}" : $"{s.Name}##{s.Id}";
                    if (ImGui.Selectable(entry, s == selected))
                        Select(s);
                }
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Theme.Tooltip(Loc.T("The set shown. Looking at a set does not track it: track it with the button at its right. Right click: rename it."));

        // Right click the list: rename the set shown.
        if (selected is not null && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            rename = selected.Name;
            ImGui.OpenPopup("##gearrename");
        }

        using (var popup = ImRaii.Popup("##gearrename"))
        {
            if ((bool)popup && selected is not null)
            {
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(Theme.Px(260));
                var done = Theme.InputText("##rename", ref rename, 80, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                done |= Theme.GhostButton(Loc.T("Rename"));
                if (done && rename.Trim().Length > 0)
                {
                    selected.Name = rename.Trim();
                    Config.Save();
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        // Track the set shown: its items lit in the game's windows.
        if (selected is not null)
        {
            ImGui.SameLine(0, Theme.Px(2));
            var isTracked = selected == tracked;
            if (Theme.IconButton("geartrack", FontAwesomeIcon.Crosshairs,
                                 isTracked ? Loc.T("This set is the one tracked for this job.") : Loc.T("Track this set: its items lit in the game's windows (the set tracked for this job until now is no longer)."),
                                 isTracked ? Theme.Gold : null)
                && !isTracked)
            {
                Track(selected);
                Say(Loc.F("Tracked: {0}.", selected.Name), false);
            }
        }

        ImGui.SameLine();
        if (Theme.IconButton("gearnew", FontAwesomeIcon.Plus, Loc.T("New set")))
        {
            var set = new GearSet { Job = job, Name = UniqueName(Loc.T("My set")) };
            Config.GearSets.Add(set);
            Select(set);
            editing = true;
        }

        if (selected is not null)
        {
            ImGui.SameLine(0, Theme.Px(2));
            if (Theme.IconButton("gearcopy", FontAwesomeIcon.Copy, Loc.T("Duplicate this set")))
            {
                var copy = selected.Copy(UniqueName(selected.Name));
                Config.GearSets.Insert(Config.GearSets.IndexOf(selected) + 1, copy);
                Select(copy);
                Say(Loc.F("Duplicated: {0}.", copy.Name), false);
            }

            ImGui.SameLine(0, Theme.Px(2));
            if (Theme.IconButton("gearshare", FontAwesomeIcon.ShareAlt, Loc.T("Share this set: copies a code to your clipboard, that Import reads")))
            {
                ImGui.SetClipboardText(GearImport.ToCode(selected));
                Toasts.Show(Loc.T("Copied to the clipboard"));
                Say(Loc.F("Code of {0} copied: paste it to someone, who imports it.", selected.Name), false);
            }

            ImGui.SameLine(0, Theme.Px(2));
            {
                var deleteClicked = Theme.IconButton("geardelete", FontAwesomeIcon.Trash, Loc.T("Delete this set (hold Ctrl)"));
                if (deleteClicked && ImGui.GetIO().KeyCtrl)
                {
                    Config.GearSets.Remove(selected);
                    viewed.Remove(job);
                    if (selected == tracked)
                        Config.GearTracked.Remove(job);
                    Config.Save();
                    Say(Loc.F("Deleted: {0}.", selected.Name), false);
                }
            }


            ImGui.SameLine();
            if (Theme.Chip(editing ? Loc.T("Done") : Loc.T("Edit"), editing))
            {
                editing = !editing;
                Config.Save();
            }
        }

        // Highlight, at the right.
        var label = Loc.T("Light up in game windows");
        var checkWidth = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        contentRight = Math.Max(contentRight, ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Px(24) + checkWidth);
        var highlight = Config.GearHighlight;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(label).X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemInnerSpacing.X);
        if (Theme.Checkbox(label, ref highlight))
        {
            Config.GearHighlight = highlight;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            Theme.Tooltip(Loc.T("Lights up in the game's windows the set tracked for the job you play: its pieces, its food and the materia still to meld, in green in your inventory, armoury chest, saddlebag and retainer; in shops and the loot window, in gold the ones you still lack."));

        // Import, and at its right the sets named in the game's item tooltip.
        var tipLabel = Loc.T("Sets in item tooltips");
        var tipWidth = ImGui.CalcTextSize(tipLabel).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        contentRight = Math.Max(contentRight, ImGui.GetCursorPosX() + Theme.Px(200) + Theme.Px(110) + tipWidth);
        ImGui.SetNextItemWidth(Math.Max(Theme.Px(200), ImGui.GetContentRegionAvail().X - Theme.Px(110) - tipWidth));
        if (importRefused)
        {
            importRefused = false;
            Theme.Refuse("##gearlink");
        }

        Theme.InputTextWithHint("##gearlink", Loc.T("Link of xivgear.app or etro.gg, or a share code"), ref link, 8000);
        ImGui.SameLine();
        using (ImRaii.Disabled(import is not null))
        {
            if (Theme.GhostButton(import is null ? Loc.T("Import") : Loc.T("Importing...")))
            {
                var text = link.Trim().Length > 0 ? link.Trim() : ImGui.GetClipboardText() ?? string.Empty;
                if (GearImport.IsCode(text))
                {
                    // A set shared from the plugin: read here, no internet.
                    try
                    {
                        import = Task.FromResult(new List<GearSet> { GearImport.FromCode(text) });
                    }
                    catch (Exception e)
                    {
                        import = Task.FromException<List<GearSet>>(e);
                    }
                }
                else if (GearImport.IsLink(text))
                {
                    import = GearImport.FromLink(text);
                    Say(Loc.T("Importing..."), false);
                }
                else
                {
                    Say(Loc.T("Paste a link of xivgear.app or etro.gg, or a set's share code, in the field (or copy it, then click Import)."), true);
                }
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Theme.Tooltip(Loc.T("Imports the sets of a link: a set or a whole sheet of xivgear.app (its best in slot lists too), a gear set of etro.gg, or a set's share code (Share button). The field empty, the one in your clipboard."));

        var tooltip = Config.GearTooltip;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - tipWidth);
        if (Theme.Checkbox(tipLabel, ref tooltip))
        {
            Config.GearTooltip = tooltip;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            Theme.Tooltip(Loc.T("In the game's item tooltip, under the item's name: the sets holding it (every job), and how many of it when more than one."));
    }

    private string UniqueName(string name)
    {
        if (Config.GearSets.All(s => s.Name != name))
            return name;
        var n = 1;
        while (Config.GearSets.Any(s => s.Name == $"{name} ({n})"))
            n++;
        return $"{name} ({n})";
    }

    private void PollImport()
    {
        if (import is not { IsCompleted: true } done)
            return;
        import = null;

        if (done.IsFaulted)
        {
            var error = done.Exception?.GetBaseException();
            Say(Loc.F("Import failed: {0}", error?.Message ?? "?"), true);
            importRefused = true;
            Plugin.Log.Warning(error!, "Gear set import failed");
            return;
        }

        var sets = done.Result;
        foreach (var set in sets)
        {
            set.Name = UniqueName(set.Name.Length > 0 ? set.Name : Loc.T("Imported set"));
            Config.GearSets.Add(set);
        }

        if (sets.Count > 0)
        {
            job = sets[0].Job;
            Select(sets[0]);
        }

        link = string.Empty;
        Config.Save();
        Say(sets.Count == 1 ? Loc.F("Imported: {0}.", sets[0].Name) : Loc.F("Imported {0} sets: pick one in the list.", sets.Count), false);
    }

    // ---- Tracker: what you own of the set.

    // ---- The tracker's animations: the set's gauge, a piece dropping in when obtained, a row lifting under the mouse.

    private float gaugeShown = -1f;
    private string? gaugeSet;
    private string? ownedSet;
    private readonly Dictionary<GearSlot, bool> ownedBefore = new();
    private readonly Dictionary<GearSlot, float> obtainedAt = new();
    private readonly Dictionary<GearSlot, float> lifted = new();
    private readonly Sparks dropSparks = new();

    // The set's progress (pieces and materia): a gauge gliding to it, a mark at each quarter lit once reached.
    private void DrawProgressGauge(GearSet set, int total, int done)
    {
        if (total <= 0)
            return;
        var target = done / (float)total;
        if (gaugeSet != set.Id || !Fx.Enabled || gaugeShown < 0)
        {
            gaugeSet = set.Id;
            gaugeShown = Fx.Enabled ? 0 : target;
        }

        gaugeShown += (target - gaugeShown) * Math.Min(1f, Fx.Delta * 3f);
        var width = Math.Min(ImGui.GetContentRegionAvail().X, Theme.Px(420));
        var height = Theme.Px(8);
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, Theme.Px(4));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(width, height), Theme.U32(Theme.Surface), height / 2);
        if (gaugeShown > 0.001f)
            dl.AddRectFilled(pos, pos + new Vector2(width * gaugeShown, height), Theme.U32(target >= 1 ? Theme.Good : Theme.Gold, 0.9f), height / 2);
        foreach (var mark in new[] { 0.25f, 0.5f, 0.75f, 1f })
        {
            var at = pos + new Vector2(width * mark, height / 2);
            var reached = gaugeShown >= mark - 0.002f;
            if (reached)
                Fx.Glow(dl, at, Theme.Px(2.6f), target >= 1 ? Theme.Good : Theme.GoldBright, 0.9f);
            else
                dl.AddCircleFilled(at, Theme.Px(2.6f), Theme.U32(Theme.Line), 12);
        }

        var label = $"{target:0%}";
        dl.AddText(pos + new Vector2(width + Theme.Px(8), (height - ImGui.GetTextLineHeight()) / 2), Theme.U32(Theme.TextMuted), label);
        ImGui.Dummy(new Vector2(width + Theme.Px(8) + ImGui.CalcTextSize(label).X, height + Theme.Px(8)));
    }

    // The piece's icon dropping into its place, a flash and a few sparks as it lands.
    private void DrawDrop(Vector2 cell, uint icon, float age)
    {
        var size = ImGui.GetFrameHeight();
        var dl = ImGui.GetWindowDrawList();
        var fall = Fx.EaseOut(age / 0.35f);
        var at = cell - new Vector2(0, (1 - fall) * size * 1.6f);
        if (age < 0.35f && icon != 0
            && Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(icon)).GetWrapOrDefault() is { } wrap)
            dl.AddImage(wrap.Handle, at, at + new Vector2(size), Vector2.Zero, Vector2.One, Theme.U32(Vector4.One, 0.9f));
        if (age >= 0.35f)
        {
            var flash = 1 - (age - 0.35f) / 0.65f;
            dl.AddRect(cell - new Vector2(Theme.Px(2)), cell + new Vector2(size + Theme.Px(2)), Theme.U32(Theme.GoldBright, flash), Theme.Px(4),
                       ImDrawFlags.None, Theme.Px(2));
            if (!dropSparks.Any && age < 0.4f)
                dropSparks.Burst(cell + new Vector2(size / 2), Theme.GoldBright, 14, 0.6f, 0.6f);
        }

        dropSparks.Draw(dl);
    }

    // The row under the mouse lifts a little: a light rim and a shadow under it.
    private unsafe void DrawLift(GearSlot slot, Vector2 rowStart)
    {
        var table = ImGuiP.GetCurrentTable();
        if (table.Handle == null)
            return;
        var padding = ImGui.GetStyle().CellPadding;
        var min = new Vector2(table.OuterRect.Min.X, rowStart.Y - padding.Y);
        var max = new Vector2(table.OuterRect.Max.X, rowStart.Y + ImGui.GetFrameHeight() + padding.Y);
        var mouse = ImGui.GetMousePos();
        var over = ImGui.IsWindowHovered() && mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y;
        var k = lifted.GetValueOrDefault(slot);
        k = Fx.Enabled ? Math.Clamp(k + (over ? 1 : -1) * Fx.Delta * 8f, 0f, 1f) : 0f;
        lifted[slot] = k;
        if (k <= 0)
            return;
        var dl = ImGui.GetWindowDrawList();
        var shadow = Theme.Px(6) * k;
        uint dark = Theme.U32(new Vector4(0, 0, 0, 1), 0.35f * k), none = Theme.U32(new Vector4(0, 0, 0, 1), 0f);
        dl.AddRectFilledMultiColor(new Vector2(min.X + Theme.Px(3), max.Y), new Vector2(max.X - Theme.Px(3), max.Y + shadow), dark, dark, none, none);
        dl.AddRect(min, max, Theme.U32(Theme.Gold, 0.4f * k), Theme.Px(4), ImDrawFlags.None, Theme.Px(1));
        dl.AddLine(min + new Vector2(Theme.Px(4), 0), new Vector2(max.X - Theme.Px(4), min.Y), Theme.U32(Theme.GoldBright, 0.5f * k), Theme.Px(1));
    }

    // Where the panel behind the pieces goes (relative to the window), from the last frame.
    private (Vector2 Min, Vector2 Max)? piecesBox;

    private void DrawTracker(GearSet set)
    {
        var slots = GearCatalog.SlotsOf(set.Job).Where(s => set.Piece(s) is not null).ToList();
        var owned = GearInventory.Match(set);
        var relics = slots.Where(s => set.Piece(s)!.Relic.Count > 0)
                          .ToDictionary(s => s, s => CheckRelic(set, set.Piece(s)!, owned.GetValueOrDefault(s)));
        var have = slots.Count(s => owned.GetValueOrDefault(s) is not null && (!relics.TryGetValue(s, out var r) || r.Checked == true));
        var planned = slots.Sum(s => set.Piece(s)!.Materia.Count(m => m != 0));
        var melded = slots.Sum(s => GearInventory.MateriaDone(set.Piece(s)!, owned.GetValueOrDefault(s)));

        ImGui.TextColored(have == slots.Count ? Theme.Good : Theme.Text, Loc.F("{0}/{1} pieces", have, slots.Count));
        var worn = slots.Count(s => owned.GetValueOrDefault(s) is { Place: GearPlace.Equipped });
        if (have > 0)
        {
            ImGui.SameLine(0, Theme.Px(6));
            ImGui.TextColored(worn == slots.Count ? Theme.Good : Theme.Warn, Loc.F("({0} equipped)", worn));
        }

        if (planned > 0)
        {
            ImGui.SameLine(0, Theme.Px(16));
            ImGui.TextColored(melded == planned ? Theme.Good : Theme.Text, Loc.F("{0}/{1} materia melded", melded, planned));
        }

        var itemLevel = slots.Select(s => GearCatalog.Item(set.Piece(s)!.ItemId)?.ItemLevel ?? 0).DefaultIfEmpty(0).Average();
        if (itemLevel > 0)
        {
            ImGui.SameLine(0, Theme.Px(16));
            ImGui.TextColored(Theme.TextMuted, Loc.F("Average item level {0:0}", itemLevel));
        }

        Theme.HelpMarker(Loc.T("Read from your character: equipped, armoury chest, inventory and saddlebag (once opened). Retainers are not read."));

        DrawProgressGauge(set, slots.Count + planned, have + melded);

        // Pieces just obtained (not owned on the last look at this set): they drop into their row.
        if (ownedSet != set.Id)
        {
            ownedSet = set.Id;
            ownedBefore.Clear();
            foreach (var s in slots)
                ownedBefore[s] = owned.GetValueOrDefault(s) is not null;
        }

        foreach (var s in slots)
        {
            var now = owned.GetValueOrDefault(s) is not null;
            if (now && ownedBefore.TryGetValue(s, out var before) && !before && Fx.Enabled)
                obtainedAt[s] = Fx.Now;
            ownedBefore[s] = now;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | Theme.FitTable | ImGuiTableFlags.PadOuterX;
        var columns = StatColumns(set);

        // The pieces, stats and note in a panel like the list of the mitigation plans (the child background of the theme), drawn
        // behind them where they were last frame.
        var boxPad = Theme.Px(6);
        ImGui.Dummy(new Vector2(0, boxPad / 2));
        var window = ImGui.GetWindowPos();
        if (piecesBox is { } box)
            ImGui.GetWindowDrawList().AddRectFilled(window + box.Min, window + box.Max, ImGui.GetColorU32(ImGuiCol.ChildBg),
                                                    ImGui.GetStyle().ChildRounding);
        var piecesTop = ImGui.GetCursorScreenPos().Y;
        using (var table = Theme.Table("##geartracker", 5 + columns.Count, flags))
        {
            if (table)
            {
                ImGui.TableSetupColumn(Loc.T("Slot"));
                ImGui.TableSetupColumn(Loc.T("Item"));
                ImGui.TableSetupColumn(Loc.T("iLvl"));
                ImGui.TableSetupColumn(Loc.T("You have it"));
                ImGui.TableSetupColumn(Loc.T("Materia"));
                SetupStatColumns(columns);
                ImGui.TableHeadersRow();

                foreach (var slot in slots)
                {
                    var piece = set.Piece(slot)!;
                    var copy = owned.GetValueOrDefault(slot);
                    var item = GearCatalog.Item(piece.ItemId);
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    var rowStart = ImGui.GetCursorScreenPos();
                    var sinceObtained = obtainedAt.TryGetValue(slot, out var obtained) ? Fx.Now - obtained : 100f;
                    if (sinceObtained < 1f)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, Theme.U32(Theme.Good, 0.35f * (1 - sinceObtained)));

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextMuted, GearCatalog.SlotName(slot));

                    ImGui.TableNextColumn();
                    var itemCell = ImGui.GetCursorScreenPos();
                    DrawItem(piece.ItemId, piece, copy);
                    if (sinceObtained < 1f)
                        DrawDrop(itemCell, item?.Icon ?? 0, sinceObtained);

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextFaint, item?.ItemLevel.ToString() ?? string.Empty);

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    if (copy is null)
                        ImGui.TextColored(Theme.Bad, Loc.T("Missing"));
                    else
                    {
                        // Owned but not worn: in orange, to put on.
                        ImGui.TextColored(copy.Place == GearPlace.Equipped ? Theme.Good : Theme.Warn, PlaceName(copy.Place));
                        if (copy.Place != GearPlace.Equipped && ImGui.IsItemHovered())
                            Theme.Tooltip(Loc.T("You have it, but it is not equipped."));
                    }

                    if (copy is not null && relics.TryGetValue(slot, out var relic))
                        DrawRelicState(set, piece, relic);

                    ImGui.TableNextColumn();
                    DrawMateria(piece, copy);
                    DrawStatCells(piece, columns);
                    DrawLift(slot, rowStart);
                }

                if (set.Food != 0)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextMuted, Loc.T("Food"));
                    ImGui.TableNextColumn();
                    DrawItem(set.Food, new GearPiece { ItemId = set.Food, Note = set.FoodNote }, null);
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    if (GearInventory.HasItem(set.Food))
                        ImGui.TextColored(Theme.Good, Loc.T("Inventory"));
                    else
                        ImGui.TextColored(Theme.Bad, Loc.T("Missing"));
                    ImGui.TableNextColumn();
                    DrawFoodCells(set, columns);
                }
            }
        }

        FitWidth();

        DrawStats(set);

        if (set.Note.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Gold, Loc.T("Note"));
            Theme.Hint(set.Note);
        }

        // The panel's place for the next frame: the pieces, the stats and the note.
        var left = window.X + ImGui.GetWindowContentRegionMin().X - boxPad;
        var right = window.X + ImGui.GetWindowContentRegionMax().X + boxPad;
        var bottom = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + boxPad;
        piecesBox = (new Vector2(left, piecesTop - boxPad) - window, new Vector2(right, bottom) - window);
        ImGui.Dummy(new Vector2(0, boxPad / 2));

        if (set.Source.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextFaint, Loc.F("Imported from {0}", set.Source));
        }
    }

    // The set's stats at level 100: each stat's total, from the gear, the materia and the food, what the meld caps
    // cut, and the global cooldown at its speed.
    private void DrawStats(GearSet set)
    {
        if (set.Pieces.Values.All(p => p.ItemId == 0))
            return;

        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, Loc.T("Stats"));
        Theme.HelpMarker(Loc.T("At level 100, from the gear (high quality when it can be), the materia (up to each item's meld cap) and the food. Main stats without the race and job bonuses."));

        var main = GearStats.MainStat(set.Job);
        var totals = GearStats.Totals(set).Where(t => t.Param == main || t.Param == GearStats.Vitality || GearStats.Secondary.Contains(t.Param))
                              .Where(t => t.Gear + t.Materia + t.Food > 0 || t.Param == main)
                              .ToList();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | Theme.FitTable | ImGuiTableFlags.PadOuterX;
        using (var table = Theme.Table("##gearstats", 7, flags))
        {
            if (table)
            {
                ImGui.TableSetupColumn(Loc.T("Stat"));
                ImGui.TableSetupColumn(Loc.T("Total"));
                ImGui.TableSetupColumn(Loc.T("Effect"));
                ImGui.TableSetupColumn(Loc.T("Gear"));
                ImGui.TableSetupColumn(Loc.T("Materia"));
                ImGui.TableSetupColumn(Loc.T("Food"));
                ImGui.TableSetupColumn(Loc.T("Over cap"));
                ImGui.TableHeadersRow();
                foreach (var t in totals)
                {
                    ImGui.TableNextRow();
                    Theme.RowHover();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, GearStats.Name(t.Param));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(t.Total.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Gold, GearStats.Effect(t.Param, t.Total));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextFaint, t.Gear > 0 ? $"+{t.Gear}" : string.Empty);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextFaint, t.Materia > 0 ? $"+{t.Materia}" : string.Empty);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextFaint, t.Food > 0 ? $"+{t.Food}" : string.Empty);
                    ImGui.TableNextColumn();
                    if (t.Wasted > 0)
                        ImGui.TextColored(Theme.Bad, $"-{t.Wasted}");
                }
            }
        }

        FitWidth();

        var speedStat = GearStats.UsesSpellSpeed(set.Job) ? GearStats.SpellSpeed : GearStats.SkillSpeed;
        var speed = GearStats.Totals(set).First(t => t.Param == speedStat).Total;
        ImGui.TextColored(Theme.TextMuted, Loc.F("GCD {0:0.00}s ({1} {2})", GearStats.Gcd(speed), GearStats.Name(speedStat), speed));
    }

    // The secondary stats found on the set (its items, materia or food): a column each.
    private static List<uint> StatColumns(GearSet set)
    {
        var found = set.Pieces.Values.Where(p => p.ItemId != 0)
                       .SelectMany(p => GearStats.OfPiece(p).Where(s => s.Value.Gear + s.Value.Meld > 0).Select(s => s.Key))
                       .Concat(GearStats.OfFood(set.Food).Select(f => f.Param))
                       .ToHashSet();
        return GearStats.Secondary.Where(found.Contains).ToList();
    }

    private static readonly Dictionary<uint, string> ShortNames = new()
    {
        [GearStats.CriticalHit] = "CRT", [GearStats.Determination] = "DET", [GearStats.DirectHit] = "DH",
        [GearStats.SkillSpeed] = "SKS", [GearStats.SpellSpeed] = "SPS", [GearStats.Tenacity] = "TEN", [GearStats.Piety] = "PIE",
    };

    private static void SetupStatColumns(List<uint> columns)
    {
        foreach (var param in columns)
            ImGui.TableSetupColumn($"{ShortNames.GetValueOrDefault(param, "?")}##stat{param}");
    }

    // A piece's stats, one cell per column: the item's, then what its materia add (in green; the cap's cut in red).
    private static void DrawStatCells(GearPiece piece, List<uint> columns)
    {
        var stats = piece.ItemId != 0 ? GearStats.OfPiece(piece) : [];
        foreach (var param in columns)
        {
            ImGui.TableNextColumn();
            if (!stats.TryGetValue(param, out var s) || s.Gear + s.Meld + s.Wasted == 0)
                continue;

            ImGui.AlignTextToFramePadding();
            if (s.Gear > 0)
                ImGui.TextUnformatted(s.Gear.ToString());
            if (s.Meld > 0)
            {
                if (s.Gear > 0)
                    ImGui.SameLine(0, Theme.Px(3));
                ImGui.TextColored(Theme.Good, $"+{s.Meld}");
            }

            if (s.Wasted > 0)
            {
                ImGui.SameLine(0, Theme.Px(3));
                ImGui.TextColored(Theme.Bad, $"-{s.Wasted}");
                if (ImGui.IsItemHovered())
                    Theme.Tooltip(Loc.F("({0} over the cap)", s.Wasted));
            }

            if (ImGui.IsItemHovered() && s.Wasted == 0)
                Theme.Tooltip(GearStats.Name(param));
        }
    }

    // The food's bonus to each stat, on the set's totals (capped).
    private static void DrawFoodCells(GearSet set, List<uint> columns)
    {
        var totals = GearStats.Totals(set);
        foreach (var param in columns)
        {
            ImGui.TableNextColumn();
            var food = totals.FirstOrDefault(t => t.Param == param)?.Food ?? 0;
            if (food <= 0)
                continue;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Good, $"+{food}");
        }
    }

    // ---- Relics

    // Checks the relic you own (when it can be) and keeps the answer, for when it cannot.
    private (bool? Checked, RelicState State, Dictionary<uint, int> Actual) CheckRelic(GearSet set, GearPiece piece, OwnedItem? copy)
    {
        if (copy is null)
            return (null, RelicState.NotEquipped, []);

        var (state, actual) = GearRelic.Check(set, piece, copy);
        if (state is RelicState.Right or RelicState.Wrong && piece.RelicChecked != (state == RelicState.Right))
        {
            piece.RelicChecked = state == RelicState.Right;
            Config.Save();
        }

        return (piece.RelicChecked, state, actual);
    }

    private static void DrawRelicState(GearSet set, GearPiece piece, (bool? Checked, RelicState State, Dictionary<uint, int> Actual) relic)
    {
        ImGui.SameLine();
        var (text, color) = relic.Checked switch
        {
            true => (Loc.T("right stats"), Theme.Good),
            false => (Loc.T("wrong stats"), Theme.Bad),
            _ => (Loc.T("stats to check"), Theme.Warn),
        };
        ImGui.TextColored(color, text);
        if (!ImGui.IsItemHovered())
            return;

        using var tooltip = ImRaii.Tooltip();
        ImGui.TextColored(Theme.Gold, Loc.T("Relic stats"));
        foreach (var param in GearStats.Secondary.Where(p => piece.Relic.GetValueOrDefault(p) > 0 || relic.Actual.GetValueOrDefault(p) > 0))
        {
            var want = piece.Relic.GetValueOrDefault(param);
            ImGui.TextUnformatted(GearStats.Name(param));
            ImGui.SameLine(Theme.Px(150));
            if (relic.State is RelicState.Right or RelicState.Wrong)
            {
                var have = relic.Actual.GetValueOrDefault(param);
                ImGui.TextColored(have == want ? Theme.Good : Theme.Bad, Loc.F("{0} (set: {1})", have, want));
            }
            else
            {
                ImGui.TextColored(Theme.TextMuted, Loc.F("set: {0}", want));
            }
        }

        ImGui.Separator();
        ImGui.PushTextWrapPos(Theme.Px(360));
        ImGui.TextColored(Theme.TextFaint, relic.State switch
        {
            RelicState.Right or RelicState.Wrong => Loc.T("Read from your character's stats just now."),
            RelicState.NotEquipped => Loc.T("Equip it to check its stats (they are read from your character's stats)."),
            _ => Loc.F("Its stats are read from your character's: equip it on {0}, at level 100, outside a duty.", set.Job),
        });
        ImGui.PopTextWrapPos();
    }

    // The relic's stats to choose, in a pop-up (the secondary stats of the job's role).
    private bool DrawRelicEditor(GearSet set, GearPiece piece)
    {
        var changed = false;
        var label = piece.Relic.Count > 0
                        ? string.Join(" ", GearStats.Secondary.Where(p => piece.Relic.GetValueOrDefault(p) > 0)
                                                  .Select(p => $"{ShortNames.GetValueOrDefault(p, "?")} {piece.Relic[p]}"))
                        : Loc.T("Relic stats");
        if (Theme.GhostButton($"{label}##relic"))
            ImGui.OpenPopup("##relicstats");
        if (ImGui.IsItemHovered())
            Theme.Tooltip(Loc.T("The secondary stats chosen for this relic. The tracker counts it once your relic has them."));

        using var popup = ImRaii.Popup("##relicstats");
        if (!popup)
            return false;

        ImGui.TextColored(Theme.Gold, Loc.T("Relic stats"));
        foreach (var param in RelicStatsOf(set.Job))
        {
            var value = piece.Relic.GetValueOrDefault(param);
            ImGui.SetNextItemWidth(Theme.Px(110));
            if (ImGui.InputInt($"{GearStats.Name(param)}##relic{param}", ref value, 1, 10))
            {
                if (value > 0)
                    piece.Relic[param] = Math.Min(value, 9999);
                else
                    piece.Relic.Remove(param);
                piece.RelicChecked = null;
                changed = true;
            }
        }

        return changed;
    }

    // The secondary stats a relic of the job can have: the ones of every job, then the speed of its kind, then its role's.
    private static IEnumerable<uint> RelicStatsOf(string job)
    {
        yield return GearStats.CriticalHit;
        yield return GearStats.Determination;
        yield return GearStats.DirectHit;
        yield return GearStats.UsesSpellSpeed(job) ? GearStats.SpellSpeed : GearStats.SkillSpeed;
        if (job is "PLD" or "WAR" or "DRK" or "GNB")
            yield return GearStats.Tenacity;
        if (job is "WHM" or "SCH" or "AST" or "SGE")
            yield return GearStats.Piety;
    }

    private static string PlaceName(GearPlace place) => place switch
    {
        GearPlace.Equipped => Loc.T("Equipped"),
        GearPlace.Armoury => Loc.T("Armoury chest"),
        GearPlace.Inventory => Loc.T("Inventory"),
        _ => Loc.T("Saddlebag"),
    };

    // An item: icon and name (its note marked), its tooltip on hover.
    private static void DrawItem(uint id, GearPiece piece, OwnedItem? owned)
    {
        var item = GearCatalog.Item(id);
        var size = ImGui.GetFrameHeight();
        using (ImRaii.Group())
        {
            ActionIcons.Draw(item?.Icon ?? 0, size);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(item is null ? Theme.TextFaint : GearTooltip.RarityColor(item.Rarity), item?.Name ?? Loc.T("Unknown item"));
            if (piece.Note.Length > 0)
            {
                ImGui.SameLine();
                Theme.Icon(FontAwesomeIcon.StickyNote, Theme.TextFaint);
            }
        }

        if (ImGui.IsItemHovered())
            GearTooltip.Show(id, piece, owned);
    }

    // The planned materia, each lit when melded in that slot of the copy you own.
    private static void DrawMateria(GearPiece piece, OwnedItem? copy)
    {
        var size = ImGui.GetFrameHeight() * 0.8f;
        var melded = GearInventory.Melded(piece, copy);
        for (var i = 0; i < piece.Materia.Count; i++)
        {
            var id = piece.Materia[i];
            if (i > 0)
                ImGui.SameLine(0, Theme.Px(2));
            if (id == 0)
            {
                ImGui.Dummy(new Vector2(size, size));
                continue;
            }

            var done = melded[i];
            var pos = ImGui.GetCursorScreenPos();
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, done ? 1f : 0.35f))
                ActionIcons.Draw(GearCatalog.Item(id)?.Icon ?? 0, size);
            if (ImGui.IsItemHovered())
                GearTooltip.Show(id);
        }
    }

    // ---- Editor

    private void DrawEditor(GearSet set)
    {
        var changed = false;
        var name = set.Name;
        ImGui.SetNextItemWidth(Theme.Px(300));
        if (Theme.InputText(Loc.T("Name"), ref name, 80))
        {
            set.Name = name;
            changed = true;
        }

        ImGui.SameLine(0, Theme.Px(20));
        var max = set.MaxItemLevel;
        ImGui.SetNextItemWidth(Theme.Px(110));
        if (ImGui.InputInt(Loc.T("Max item level"), ref max, 5, 10))
        {
            set.MaxItemLevel = Math.Clamp(max, 0, 9999);
            changed = true;
        }

        if (ImGui.IsItemHovered())
            Theme.Tooltip(Loc.T("The item lists stop at this item level (0: every item)."));

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | Theme.FitTable | ImGuiTableFlags.PadOuterX;
        var columns = StatColumns(set);
        using (var table = Theme.Table("##geareditor", 4 + columns.Count, flags))
        {
            if (table)
            {
                ImGui.TableSetupColumn(Loc.T("Slot"));
                ImGui.TableSetupColumn(Loc.T("Item"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(320));
                ImGui.TableSetupColumn(Loc.T("Materia"));
                SetupStatColumns(columns);
                ImGui.TableSetupColumn(Loc.T("Note"), ImGuiTableColumnFlags.WidthFixed, Theme.Px(200));
                ImGui.TableHeadersRow();

                foreach (var slot in GearCatalog.SlotsOf(set.Job))
                {
                    using var id = ImRaii.PushId((int)slot);
                    var piece = set.Edit(slot);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextMuted, GearCatalog.SlotName(slot));

                    ImGui.TableNextColumn();
                    var list = GearCatalog.ForSlot(set.Job, slot);
                    if (Picker("item", piece.ItemId, list, set.MaxItemLevel, Theme.Px(310), piece: piece) is { } picked)
                    {
                        piece.ItemId = picked;
                        FitMateria(piece);
                        changed = true;
                    }

                    ImGui.TableNextColumn();
                    if (piece.ItemId != 0)
                        changed |= DrawMateriaEditor(piece);
                    if (piece.ItemId != 0 && (piece.Relic.Count > 0 || GearStats.IsCustomRelic(piece.ItemId)))
                    {
                        if (MateriaSlots(piece) > 0)
                            ImGui.SameLine();
                        changed |= DrawRelicEditor(set, piece);
                    }
                    DrawStatCells(piece, columns);

                    ImGui.TableNextColumn();
                    var note = piece.Note;
                    ImGui.SetNextItemWidth(Theme.Px(190));
                    if (Theme.InputTextWithHint("##note", Loc.T("Note"), ref note, 200))
                    {
                        piece.Note = note;
                        changed = true;
                    }
                }

                // Food.
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.TextMuted, Loc.T("Food"));
                ImGui.TableNextColumn();
                if (Picker("food", set.Food, GearCatalog.Meals, 0, Theme.Px(310)) is { } food)
                {
                    set.Food = food;
                    changed = true;
                }

                ImGui.TableNextColumn();
                DrawFoodCells(set, columns);
                ImGui.TableNextColumn();
                var foodNote = set.FoodNote;
                ImGui.SetNextItemWidth(Theme.Px(190));
                if (Theme.InputTextWithHint("##foodnote", Loc.T("Note"), ref foodNote, 200))
                {
                    set.FoodNote = foodNote;
                    changed = true;
                }
            }
        }

        FitWidth();

        DrawStats(set);

        ImGui.Spacing();
        ImGui.TextColored(Theme.Gold, Loc.T("Note"));
        var general = set.Note;
        if (ImGui.InputTextMultiline("##gearnote", ref general, 2000, new Vector2(Math.Max(Theme.Px(300), contentRight - ImGui.GetCursorPosX()), Theme.Px(80))))
        {
            set.Note = general;
            changed = true;
        }

        if (changed)
            Config.Save();
    }

    // As many materia slots as the item has, then up to five for an item that can be overmelded.
    private static int MateriaSlots(GearPiece piece) =>
        GearCatalog.Item(piece.ItemId) is { } item ? (item.Overmeld ? 5 : item.MateriaSlots) : 0;

    private static void FitMateria(GearPiece piece)
    {
        var slots = MateriaSlots(piece);
        if (piece.Materia.Count > slots)
            piece.Materia.RemoveRange(slots, piece.Materia.Count - slots);
    }

    private bool DrawMateriaEditor(GearPiece piece)
    {
        var item = GearCatalog.Item(piece.ItemId);
        var slots = MateriaSlots(piece);
        var changed = false;
        while (piece.Materia.Count < slots)
            piece.Materia.Add(0);

        for (var i = 0; i < slots; i++)
        {
            using var id = ImRaii.PushId($"m{i}");
            if (i > 0)
                ImGui.SameLine(0, Theme.Px(2));
            var overmeld = item is not null && i >= item.MateriaSlots;
            if (Picker("materia", piece.Materia[i], GearCatalog.Materia, 0, 0, iconOnly: true, faint: overmeld) is { } picked)
            {
                piece.Materia[i] = picked;
                changed = true;
            }

            if (ImGui.IsItemHovered() && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId))
            {
                if (piece.Materia[i] != 0)
                    GearTooltip.Show(piece.Materia[i]);
                else
                    Theme.Tooltip(overmeld ? Loc.F("{0} (overmeld)", Loc.T("Empty")) : Loc.T("Empty"));
            }
        }

        return changed;
    }

    /// <summary>
    /// A button showing an item, opening a searchable list to pick another (right click: clear). The id picked, null
    /// when nothing changed (0: cleared).
    /// </summary>
    private uint? Picker(string id, uint current, List<GearItem> items, int maxItemLevel, float width, bool iconOnly = false, bool faint = false,
                         GearPiece? piece = null)
    {
        uint? result = null;
        var item = GearCatalog.Item(current);
        var size = ImGui.GetFrameHeight();
        var popup = $"##pick{id}";

        if (iconOnly)
        {
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##btn{id}", new Vector2(size, size)))
            {
                pickSearch = string.Empty;
                ImGui.OpenPopup(popup);
            }

            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(pos, pos + new Vector2(size, size), Theme.U32(Theme.Surface), Theme.Px(4));
            dl.AddRect(pos, pos + new Vector2(size, size), Theme.U32(faint ? Theme.TextFaint : Theme.Line, faint ? 0.5f : 1f), Theme.Px(4));
            if (item is not null)
                ActionIcons.DrawAt(dl, item.Icon, pos + new Vector2(2, 2), size - 4);
        }
        else
        {
            if (item is not null)
            {
                ActionIcons.Draw(item.Icon, size);
                ImGui.SameLine(0, Theme.Px(4));
            }

            var label = item is null ? Loc.T("(none)") : $"{item.Name}  ({item.ItemLevel})";
            using (ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0, 0.5f)))
            {
                if (ImGui.Button($"{label}##btn{id}", new Vector2(width - (item is null ? 0 : size + Theme.Px(4)), 0)))
                {
                    pickSearch = string.Empty;
                    ImGui.OpenPopup(popup);
                }
            }
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && current != 0)
            result = 0;
        if (!iconOnly && current != 0 && ImGui.IsItemHovered() && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId))
            GearTooltip.Show(current, piece);

        using var pop = ImRaii.Popup(popup);
        if (!pop)
            return result;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(Theme.Px(320));
        Theme.InputTextWithHint("##search", Loc.T("Search"), ref pickSearch, 80);

        var shown = items.Where(i => (maxItemLevel <= 0 || i.ItemLevel <= maxItemLevel)
                                     && (pickSearch.Length == 0 || i.Name.Contains(pickSearch, StringComparison.OrdinalIgnoreCase)))
                         .ToList();
        using var child = ImRaii.Child("##picklist", new Vector2(Theme.Px(420), Theme.Px(320)));
        if (!child)
            return result;

        var line = Math.Max(ImGui.GetTextLineHeightWithSpacing(), size * 0.8f + ImGui.GetStyle().ItemSpacing.Y);
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(shown.Count, line);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var it = shown[i];
                ActionIcons.Draw(it.Icon, size * 0.8f);
                ImGui.SameLine();
                if (ImGui.Selectable($"{it.Name}##{it.Id}", it.Id == current))
                {
                    result = it.Id;
                    ImGui.CloseCurrentPopup();
                }

                if (ImGui.IsItemHovered())
                    GearTooltip.Show(it.Id);

                if (it.ItemLevel > 1)
                {
                    ImGui.SameLine(Theme.Px(380));
                    ImGui.TextColored(Theme.TextFaint, it.ItemLevel.ToString());
                }
            }
        }

        clipper.End();
        clipper.Destroy();
        return result;
    }
}
