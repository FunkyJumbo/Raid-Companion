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

/// <summary>Live encounter breakdown and pull history, opened with right click on the bar entry or /raidcompanion.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly DpsGraph graph = new();

    private int selectedTab;
    private string historyFilter = string.Empty;
    private Guid? selectedId;
    private Guid? compareId;
    // The Progression tab: the History's filters, then the progression of the chosen encounter.
    private bool ShowProgression => selectedTab == 2;
    private string groupSearch = string.Empty;
    private string encounterSearch = string.Empty;
    private bool progressionByTime;

    // History: the table of enemy casts and party debuffs under the timeline.
    private bool showFightLog;
    private bool fightLogCasts = true;
    private bool fightLogDebuffs = true;
    private string fightLogSearch = string.Empty;
    private Guid? lastTickedPull; // anchor of a Shift+click range in the Pulls pop-up
    private string groupName = string.Empty;

    /// <summary>Height the selected pull's details need to be fully visible (graph at its smallest), from the last frame.</summary>
    private float detailsHeight;

    /// <summary>Height added to the pull list by dragging its grip.</summary>
    private float pullListExtra;

    // Height the window needs to show everything, from the last frame (pixels); null when a part grows with the
    // window (a graph): the window then has no height limit.
    private float? fitHeight;
    private IDisposable? themeScope;

    public MainWindow(Plugin plugin) : base(Loc.T("Raid Companion###DpsInServerBarMain"))
    {
        Loc.Changed += () => WindowName = Loc.T("Raid Companion###DpsInServerBarMain");
        this.plugin = plugin;
        AllowBackgroundBlur = false;
        Size = new Vector2(760, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        shot = new WindowShot(this);
    }

    /// <summary>History tab with this pull selected (when it is still recorded).</summary>
    public void ShowPull(Guid pullId)
    {
        ShowHistory();
        if (plugin.History.Pulls.FirstOrDefault(p => p.Id == pullId) is not { } pull)
            return;

        selectedId = pullId;
        // Another encounter picked in the filter: show them all, so the pull shows in the list.
        if (historyFilter.Length > 0 && !Progression.Encounter(pull).Equals(historyFilter, StringComparison.OrdinalIgnoreCase))
            historyFilter = string.Empty;
    }

    public void ShowHistory()
    {
        IsOpen = true;
        selectedTab = 1;
    }

    public override void PreDraw()
    {
        themeScope = Theme.Push();

        // Back to a view: the height it had when it was left.
        if (restoreHeight is { } restore)
        {
            Size = new Vector2(windowSize.X, restore) / ImGuiHelpers.GlobalScale;
            SizeCondition = ImGuiCond.Always;
            restoreHeight = null;
            fitHeight = null; // the limit of the view left does not apply
        }
        else if (SizeCondition == ImGuiCond.Always)
        {
            Size = null;
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        // No taller than the content: no empty space at the bottom.
        var maxHeight = fitHeight is { } fit ? Math.Max(MinHeight, fit / ImGuiHelpers.GlobalScale) : float.MaxValue;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, MinHeight), MaximumSize = new Vector2(float.MaxValue, maxHeight) };
        shot.PreDraw();
    }

    private const float MinHeight = 380;

    // Height of each view (Live, History, Progression) when it was last shown, to give it back when coming back.
    private readonly Dictionary<int, float> viewHeights = new();
    private int lastView;
    private Vector2 windowSize;
    private float? restoreHeight;

    private int CurrentView => selectedTab;

    // Screenshot of a view (right click on its tab): the view is shown, the window grows to its whole content,
    // is captured and saved, then gets its size and place back.
    private readonly WindowShot shot;
    private static string[] TabNames => [Loc.T("Live"), Loc.T("History"), Loc.T("Progression")];

    // History's pull list: its height last drawn, and the one kept during a screenshot (the window grows, not the list).
    private float pullListHeight, pullListFullHeight;
    private float? shotPullListHeight;
    private float shotListDelta;

    // Top of the graph that fills the rest of the window (Live, Progression), to know the height of everything above.
    private float? graphTop;

    private void StartScreenshot(int tab)
    {
        selectedTab = tab;
        shot.Request(TabNames[tab], ShotHeight, () =>
        {
            shotPullListHeight = selectedTab == 1 && pullListHeight > 0 ? pullListHeight : null;
            shotListDelta = shotPullListHeight is null ? 0 : pullListFullHeight - pullListHeight;
        });
    }

    // Height of the whole view: everything shown, a graph filling the window at a readable height.
    private float ContentHeight() =>
        fitHeight ?? (graphTop is { } top
                          ? Math.Max(windowSize.Y, top + Theme.Px(300) + ImGui.GetStyle().WindowPadding.Y)
                          : windowSize.Y);

    // The first reading still counts the pull list at its full height: the list keeps the one it has.
    private float? ShotHeight()
    {
        var height = ContentHeight() - shotListDelta;
        shotListDelta = 0;
        return height;
    }

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        shot.Step();
        Theme.TabStrip("main", ref selectedTab, tab =>
                       {
                           if (ImGui.MenuItem(Loc.T("Screenshot"), false, shot.Ready))
                               StartScreenshot(tab);
                           if (ImGui.IsItemHovered())
                               ImGui.SetTooltip(Loc.T("Saves a picture of this view, the window grown to show all of it, in Pictures\\Raid Companion."));
                       },
                       (FontAwesomeIcon.Bolt, Loc.T("Live")), (FontAwesomeIcon.History, Loc.T("History")), (FontAwesomeIcon.ChartLine, Loc.T("Progression")));
        DrawSourceAndSettings();
        Theme.Rule(2);

        fitHeight = null;
        graphTop = null;
        if (selectedTab == 0)
        {
            DrawLive();
            fitHeight = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y;
        }
        else
        {
            // History: its sections a little further apart.
            Theme.SectionSpacing = Theme.Px(15);
            try
            {
                DrawHistory();
            }
            finally
            {
                Theme.SectionSpacing = 0;
            }
        }

        // Another view now: remember this one's height, and give the new one its own.
        windowSize = ImGui.GetWindowSize();

        var view = CurrentView;
        if (view != lastView)
        {
            viewHeights[lastView] = windowSize.Y;
            if (viewHeights.TryGetValue(view, out var height))
                restoreHeight = height;
            lastView = view;
        }
    }

    /// <summary>At the right of the tabs: data source state and the settings button (the title bar already names the plugin).</summary>
    private void DrawSourceAndSettings()
    {
        var source = plugin.Tracker;
        var status = $"{source.Name}: {source.StatusText}";
        var badgeWidth = ImGui.CalcTextSize(status).X + Theme.Px(14);
        var buttonWidth = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - badgeWidth - buttonWidth - spacing);

        // Centered on the height of the tabs.
        var y = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(y + Theme.Px(5));
        Theme.Badge(status, source.IsReady ? Theme.Good : Theme.Bad);
        ImGui.SameLine();
        ImGui.SetCursorPosY(y + Theme.Px(1));
        if (Theme.IconButton("settings", FontAwesomeIcon.Cog, Loc.T("Settings")))
            plugin.ToggleConfigUi();
    }
}
