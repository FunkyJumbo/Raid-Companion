using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Tracking;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// One mitigation plan, shown on its own: the table of its casts and skills, your column, whether it checks your
/// mitigations, and its share code. During a pull of its fight, the cast in progress and the next one are lit.
/// </summary>
public sealed class MitigationPlanWindow : Window
{
    private readonly Plugin plugin;
    private readonly MitigationTable table;
    private readonly WindowShot shot;
    private IDisposable? themeScope;
    private string planId = string.Empty;
    private string message = string.Empty;

    public MitigationPlanWindow(Plugin plugin) : base(Loc.T("Mitigation plan###DpsInServerBarMitigationPlan"))
    {
        Loc.Changed += () => WindowName = Title();
        this.plugin = plugin;
        table = new MitigationTable(plugin);
        Size = new Vector2(1100, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 240), MaximumSize = new Vector2(5000, 5000) };
        AllowBackgroundBlur = false;
        shot = new WindowShot(this);
        TitleBarButtons.Add(shot.TitleBarButton(() => Plan()?.Name ?? Loc.T("Mitigation plan")));
    }

    /// <summary>Asked to edit the plan shown (the editor is in the Mitigation plans window).</summary>
    public event Action<MitigationPlan>? EditRequested;

    public string PlanId => planId;

    public void Show(MitigationPlan plan)
    {
        planId = plan.Id;
        message = string.Empty;
        WindowName = Title();
        IsOpen = true;
        BringToFront();
    }

    private MitigationPlan? Plan() => plugin.Configuration.MitigationPlans.FirstOrDefault(p => p.Id == planId);

    private string Title() =>
        Plan() is { } plan ? $"{plan.Name}  ·  {plan.Encounter}###DpsInServerBarMitigationPlan" : Loc.T("Mitigation plan###DpsInServerBarMitigationPlan");

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
        Theme.BlurBehind();
        if (Plan() is not { } plan)
        {
            Theme.Hint(Loc.T("This plan was deleted."));
            return;
        }

        WindowName = Title();
        table.DrawMine(plan);

        ImGui.SameLine(0, Theme.Px(14));
        if (Theme.IconButton("shareplan", FontAwesomeIcon.ShareAlt,
                             Loc.T("Share: copies a code holding the whole plan. The others paste it with the paste button.")))
        {
            ImGui.SetClipboardText(MitigationPlans.Export(plan));
            message = Loc.T("Code copied: paste it in the party chat or anywhere, the others import it with the paste button.");
        }

        ImGui.SameLine();
        if (Theme.IconButton("editplan", FontAwesomeIcon.Pen, Loc.T("Edit this plan")))
            EditRequested?.Invoke(plan);

        if (table.Following)
        {
            ImGui.SameLine();
            Theme.Badge(Loc.T("following the pull"), Theme.Good);
        }

        if (message.Length > 0)
            ImGui.TextColored(Theme.Good, message);

        table.DrawPlan(plan);
    }
}
