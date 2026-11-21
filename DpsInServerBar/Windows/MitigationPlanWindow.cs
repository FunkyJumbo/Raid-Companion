using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// One mitigation plan, shown on its own as a compact pop-up (like the compact meter and opener practice): no title
/// bar, dragged by its background, the plan's name then its table, everything else in the right click menu (your
/// column, the check of your mitigations, background, share, edit). Never wider than the plan; as tall as wished.
/// During a pull of its fight, the cast in progress and the next one are lit.
/// </summary>
public sealed class MitigationPlanWindow : Window
{
    private readonly Plugin plugin;
    private readonly MitigationTable table;
    private readonly WindowShot shot;
    private IDisposable? themeScope;
    private IDisposable? styleScope;
    private IDisposable? colorScope;
    private string planId = string.Empty;
    private string message = string.Empty;

    public MitigationPlanWindow(Plugin plugin)
        : base(Loc.T("Mitigation plan###DpsInServerBarMitigationPlan"),
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
               | ImGuiWindowFlags.NoResize)
    {
        Loc.Changed += () => WindowName = Loc.T("Mitigation plan###DpsInServerBarMitigationPlan");
        this.plugin = plugin;
        table = new MitigationTable(plugin);
        Size = new Vector2(900, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        AllowBackgroundBlur = false;
        shot = new WindowShot(this);
    }

    /// <summary>Asked to edit the plan shown (the editor is in the Mitigation plans window).</summary>
    public event Action<MitigationPlan>? EditRequested;

    public void Show(MitigationPlan plan)
    {
        planId = plan.Id;
        message = string.Empty;
        IsOpen = true;
        BringToFront();
    }

    private MitigationPlan? Plan() => plugin.Configuration.MitigationPlans.FirstOrDefault(p => p.Id == planId);

    private float Opacity => Math.Clamp(plugin.Configuration.MitigationOpacity, 0f, 1f);

    public override void PreDraw()
    {
        BgAlpha = Theme.WindowAlpha(Opacity); // the background only, not the icons
        themeScope = Theme.Push();
        styleScope = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Theme.Px(10))
                          .Push(ImGuiStyleVar.WindowPadding, Theme.Px(new Vector2(8, 7)));
        // The border fades with the background: no frame left around the plan once the background is gone.
        colorScope = ImRaii.PushColor(ImGuiCol.Border, Theme.Alpha(Theme.Line, Theme.Line.W * Opacity));

        // Exactly the size of the plan (not resized by hand): as wide as its columns, as tall as its rows (the tab shown,
        // your position only...), up to most of the screen.
        SizeConstraints = null;
        if (Plan() is { } plan)
        {
            var shown = table.ShownColumns(plan, plugin.Configuration.MitigationOnlyMine).Count;
            var width = MitigationTable.WindowWidthFor(MitigationTable.PlanWidth(plan, shown)) / ImGuiHelpers.GlobalScale;
            var screen = ImGui.GetMainViewport().Size.Y * 0.9f;
            var height = table.NaturalHeight > 0 ? Math.Min(table.NaturalHeight, screen) / ImGuiHelpers.GlobalScale : 420;
            Size = new Vector2(width, height);
            SizeCondition = ImGuiCond.Always;
        }

        shot.PreDraw();
    }

    public override void PostDraw()
    {
        colorScope?.Dispose();
        colorScope = null;
        styleScope?.Dispose();
        styleScope = null;
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        shot.Step();
        Theme.BlurBehind(Opacity > 0.05f);
        if (Plan() is not { } plan)
        {
            Theme.Hint(Loc.T("This plan was deleted."));
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                IsOpen = false;
            return;
        }

        DrawMenu(plan);

        // Your position or the whole group, edit and close: at the right of the tabs, or of the title without tabs.
        var toolbar = (ToolbarWidth(), (System.Action)(() => DrawToolbar(plan)));
        var titleTop = ImGui.GetCursorPos();
        // The title stops before the buttons (on its line when there are no tabs): cut with "..." at its end.
        var titleEnd = ImGui.GetWindowContentRegionMax().X - (plan.Tabs.Count == 0 ? toolbar.Item1 + Theme.Px(6) : 0);
        float Room() => titleEnd - ImGui.GetCursorPosX();
        ImGui.TextColored(Theme.Gold, Fit(plan.Name, Room()));
        var encounter = plugin.ContentCategories.DisplayName(plan.TerritoryId, plan.Encounter) ?? plan.Encounter;
        ImGui.SameLine();
        if (Room() > ImGui.CalcTextSize("M...").X)
            ImGui.TextColored(Theme.TextFaint, Fit(encounter, Room()));
        else
            ImGui.NewLine();

        if (table.Following)
        {
            ImGui.SameLine();
            if (Room() > ImGui.CalcTextSize(Loc.T("following the pull")).X + Theme.Px(16))
                Theme.Badge(Loc.T("following the pull"), Theme.Good);
            else
                ImGui.NewLine();
        }

        if (message.Length > 0)
        {
            ImGui.SameLine();
            if (Room() > ImGui.CalcTextSize("M...").X)
                ImGui.TextColored(Theme.Good, Fit(message, Room()));
            else
                ImGui.NewLine();
        }

        if (plan.Tabs.Count == 0)
        {
            var below = ImGui.GetCursorPos();
            ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - toolbar.Item1, titleTop.Y - Theme.Px(3)));
            DrawToolbar(plan);
            ImGui.SetCursorPos(below);
        }

        table.DrawPlan(plan, plugin.Configuration.MitigationOnlyMine, toolbar);
    }

    // The text, cut with "..." to fit in the width.
    private static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(text[..length].TrimEnd() + "...").X > width)
            length--;
        return length > 0 ? text[..length].TrimEnd() + "..." : string.Empty;
    }

    private static readonly FontAwesomeIcon[] ToolbarIcons = [FontAwesomeIcon.Users, FontAwesomeIcon.Pen, FontAwesomeIcon.Times];

    private static float ToolbarWidth()
    {
        var padding = ImGui.GetStyle().FramePadding.X * 2;
        float width;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            width = ToolbarIcons.Sum(i => ImGui.CalcTextSize(i.ToIconString()).X + padding);
        return width + Theme.Px(2) * (ToolbarIcons.Length - 1);
    }

    private void DrawToolbar(MitigationPlan plan)
    {
        var config = plugin.Configuration;
        var mine = plugin.PlanCheck.MyColumn(plan) >= 0;
        var onlyMine = config.MitigationOnlyMine && mine;
        // The icon of the view you switch to: one person from the group, the group from your position.
        var icon = onlyMine ? FontAwesomeIcon.Users : FontAwesomeIcon.User;
        var tip = onlyMine ? Loc.T("Show the whole group")
                  : mine ? Loc.T("Show your position only")
                  : Loc.T("Choose your column (right click) to show your position only.");
        using (ImRaii.Disabled(!mine))
        {
            if (Theme.IconButton("planview", icon, tip, onlyMine ? Theme.Gold : null))
            {
                config.MitigationOnlyMine = !config.MitigationOnlyMine;
                config.Save();
            }
        }

        if (!mine && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tip);

        ImGui.SameLine(0, Theme.Px(2));
        if (Theme.IconButton("planedit", FontAwesomeIcon.Pen, Loc.T("Edit this plan")))
            EditRequested?.Invoke(plan);
        ImGui.SameLine(0, Theme.Px(2));
        if (Theme.IconButton("planclose", FontAwesomeIcon.Times, Loc.T("Close")))
            IsOpen = false;
    }

    /// <summary>Right click menu: your column, the check of your mitigations, background, share, edit, screenshot, close.</summary>
    private void DrawMenu(MitigationPlan plan)
    {
        var config = plugin.Configuration;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Right)
            && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId))
            ImGui.OpenPopup("##mitplanmenu");

        using var popup = ImRaii.Popup("##mitplanmenu");
        if (!popup)
            return;

        table.DrawMine(plan);
        var onlyMine = config.MitigationOnlyMine;
        using (ImRaii.Disabled(plugin.PlanCheck.MyColumn(plan) < 0))
        {
            if (ImGui.Checkbox(Loc.T("My position only"), ref onlyMine))
            {
                config.MitigationOnlyMine = onlyMine;
                config.Save();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("A compact plan: only your column (My column above), and the casts where it has something."));
        ImGui.Separator();

        var percent = (int)Math.Round(config.MitigationOpacity * 100);
        ImGui.SetNextItemWidth(Theme.Px(150));
        if (ImGui.SliderInt(Loc.T("Background"), ref percent, 0, 100, "%d%%"))
            config.MitigationOpacity = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        ImGui.Separator();
        if (ImGui.MenuItem(Loc.T("Share (copy its code)")))
        {
            ImGui.SetClipboardText(MitigationPlans.Export(plan));
            message = Loc.T("Code copied.");
        }

        if (ImGui.MenuItem(Loc.T("Edit this plan")))
            EditRequested?.Invoke(plan);
        if (ImGui.MenuItem(Loc.T("Screenshot"), false, shot.Ready))
            shot.Request(plan.Name);
        if (ImGui.MenuItem(Loc.T("Close")))
            IsOpen = false;
    }
}
