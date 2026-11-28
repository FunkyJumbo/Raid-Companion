using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// The welcome window: what the plugin does, a page per part of it. Motes of aether rise behind it, and each page draws
/// a constellation of its own in the header while its text slides in. Opens by itself the first time
/// (/raidcompanion welcome later).
/// </summary>
public sealed class WelcomeWindow : Window
{
    private readonly Plugin plugin;
    private IDisposable? themeScope;

    private readonly AetherMotes motes = new(70);
    private readonly Constellation constellation = new();
    private readonly RaidScene scene = new();
    private readonly ParticleTitle finale = new();

    // The point under the mouse (last frame), lit.
    private int hoveredPoint = -1;

    private int page;
    // The page change: when, and which way (1 next, -1 back), for the text sliding in.
    private float turned = -100f;
    private int direction = 1;

    // Scene: the page shows the little pixel fight (RaidScene) instead of points.
    private sealed record Page(FontAwesomeIcon Icon, string Title, string Lead, string[] Points, bool Scene = false);

    // The fight's page, second, only with sprites in the sprites folder.
    private static Page[] Pages =>
        RaidScene.Available ? [AllPages[0], ScenePage, ..AllPages[1..]] : AllPages;

    private static Page ScenePage =>
        new(FontAwesomeIcon.Users, Loc.T("Into the fight"), Loc.T("Your party is ready, the boss is waiting."), [], true);

    private static Page[] AllPages =>
    [
        new(FontAwesomeIcon.Star, Loc.T("Welcome to Raid Companion"),
            Loc.T("Your companion for raids, from the first pull to the clear."),
            [
                Loc.T("Your DPS and the party's in the server info bar, all fight long."),
                Loc.T("A Live view of the fight, your pulls kept in a history, your progression per fight."),
                Loc.T("Mitigation plans, opener practice and best in slot gear sets."),
            ]),
        new(FontAwesomeIcon.ChartLine, Loc.T("Live"),
            Loc.T("The fight in progress, at a glance."),
            [
                Loc.T("DPS, rDPS and rank, the party's DPS against the enrage, the boss's HP and when it should die."),
                Loc.T("Your own play: GCD uptime, gauge, positionals, combos, DoTs, overheal, tank swaps."),
                Loc.T("Each death with the mitigation that was up, and a compact meter to keep on screen."),
            ]),
        new(FontAwesomeIcon.History, Loc.T("History and progression"),
            Loc.T("Every pull is kept, to look back at it."),
            [
                Loc.T("A pull's details: players, deaths, mistakes, rotation, timeline and graphs."),
                Loc.T("Two pulls side by side, to see what changed."),
                Loc.T("Your progression per fight: how far each pull went, and your best."),
            ]),
        new(FontAwesomeIcon.ShieldAlt, Loc.T("Mitigation plans and openers"),
            Loc.T("Prepare the fight, then check it during the pull."),
            [
                Loc.T("Who mitigates what for each cast; the cast in progress and the next one lit during the pull."),
                Loc.T("Practice your job's opener: each action used in the right order is checked off."),
            ]),
        new(FontAwesomeIcon.Tshirt, Loc.T("BiS gearsets"),
            Loc.T("Your best in slot sets, and what is still missing."),
            [
                Loc.T("Made in an editor, imported from xivgear.app or etro.gg, or shared with a code."),
                Loc.T("The pieces and materia you own, the stats, the meld caps and the relic checked."),
                Loc.T("The set lit in your inventory, the shops, the loot and the materia melding window."),
            ]),
        new(FontAwesomeIcon.Palette, Loc.T("Make it yours"),
            Loc.T("Everything can be set to your liking in the settings."),
            [
                Loc.T("What the server info bar shows, its colors and presets."),
                Loc.T("Themes, glass effect, the animations, the language."),
                Loc.T("Open the plugin with /raidcompanion, and this window again with /raidcompanion welcome."),
            ]),
    ];

    public WelcomeWindow(Plugin plugin)
        : base(Loc.T("Raid Companion") + "###DpsInServerBarWelcome",
               ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size = new Vector2(640, 430);
        SizeCondition = ImGuiCond.Always;
        AllowBackgroundBlur = false;
        constellation.Reset(0);
    }

    public void Show()
    {
        Turn(0);
        IsOpen = true;
        BringToFront();
    }

    public override void OnClose()
    {
        if (plugin.Configuration.WelcomeShown)
            return;
        plugin.Configuration.WelcomeShown = true;
        plugin.Configuration.Save();
    }

    public override void PreDraw() => themeScope = Theme.Push(WindowName);

    public override void PostDraw()
    {
        themeScope?.Dispose();
        themeScope = null;
    }

    private void Turn(int to)
    {
        to = Math.Clamp(to, 0, Pages.Length - 1);
        direction = to >= page ? 1 : -1;
        page = to;
        turned = Fx.Enabled ? Fx.Now : -100f;
        constellation.Reset(to + 1);
        if (to == Pages.Length - 1)
            finale.Start();
    }

    public override void Draw()
    {
        Theme.BlurBehind();
        var pages = Pages;
        var current = pages[Math.Clamp(page, 0, pages.Length - 1)];
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var winMin = ImGui.GetWindowPos();
        var winMax = winMin + ImGui.GetWindowSize();

        // Behind everything: the motes over the whole window, the constellation in the header.
        motes.Draw(dl, winMin + new Vector2(0, ImGui.GetFrameHeight()), winMax, Theme.Gold);
        var header = Theme.Px(120);
        var last = page == pages.Length - 1;
        if (last)
        {
            // The last page: "Raid Companion" made of the motes flying together, in the header.
            var size = ImGui.GetFontSize() * 2.2f;
            var titleWidth = ImGui.CalcTextSize("Raid Companion").X * 2.2f;
            finale.Draw(dl, origin + new Vector2((width - titleWidth) / 2, Theme.Px(6)), size, "Raid Companion", Theme.GoldBright,
                        winMin, winMax);
        }
        else
        {
            constellation.Draw(dl, origin, origin + new Vector2(width, header), Theme.Gold);
        }

        // The page's text, sliding in from the side it comes from, and fading in.
        var k = Fx.EaseOut((Fx.Now - turned) / 0.45f);
        var slide = (1 - k) * Theme.Px(36) * direction;
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.05f + 0.95f * k))
        {
            // Icon and title at the bottom of the header.
            ImGui.SetCursorScreenPos(origin + new Vector2(slide, header - Theme.Px(44)));
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(Theme.Gold, current.Icon.ToIconString());
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.SetWindowFontScale(1.45f);
            ImGui.TextColored(Theme.GoldBright, current.Title);
            ImGui.SetWindowFontScale(1f);

            ImGui.SetCursorScreenPos(origin + new Vector2(slide, header - Theme.Px(14)));
            ImGui.TextColored(Theme.TextMuted, current.Lead);

            ImGui.SetCursorScreenPos(origin + new Vector2(slide, header + Theme.Px(16)));
            Theme.Rule(2);
            ImGui.Dummy(new Vector2(0, Theme.Px(6)));
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, Theme.Px(10))))
            {
                var nowHovered = -1;
                for (var i = 0; i < current.Points.Length; i++)
                {
                    var lit = hoveredPoint == i && Fx.Enabled;
                    // Each point a little after the one above.
                    var kp = Fx.EaseOut((Fx.Now - turned - 0.1f - i * 0.08f) / 0.45f);
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + slide + (1 - kp) * Theme.Px(16) * direction);
                    using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.05f + 0.95f * kp))
                    using (ImRaii.Group())
                    {
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                            ImGui.TextColored(lit ? Theme.GoldBright : Theme.Gold, FontAwesomeIcon.AngleRight.ToIconString());
                        ImGui.SameLine(0, Theme.Px(lit ? 11 : 8));
                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - Theme.Px(40));
                        ImGui.TextColored(lit ? Theme.GoldBright : Theme.Text, current.Points[i]);
                        ImGui.PopTextWrapPos();
                    }

                    // The point under the mouse lights up: a soft band behind it, its arrow nudged.
                    if (ImGui.IsItemHovered())
                        nowHovered = i;
                    if (lit)
                    {
                        var pad = Theme.Px(new Vector2(6, 3));
                        dl.AddRectFilled(ImGui.GetItemRectMin() - pad, ImGui.GetItemRectMax() + pad, Theme.U32(Theme.Gold, 0.08f), Theme.Px(Theme.Radius));
                        HoverLight.Draw(ImGui.GetItemRectMin() - pad, ImGui.GetItemRectMax() + pad);
                    }
                }

                hoveredPoint = nowHovered;
            }
        }

        // The fight's page: the scene under the rule, down to the footer, fading in with the page.
        if (current.Scene)
        {
            var top = origin.Y + header + Theme.Px(30);
            var bottom = winMin.Y + ImGui.GetWindowContentRegionMax().Y - ImGui.GetFrameHeight() - Theme.Px(8);
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.05f + 0.95f * k))
                scene.Draw(new Vector2(origin.X + slide, top), new Vector2(origin.X + width + slide, bottom));
        }

        DrawFooter(pages.Length);

        if (ImGui.IsWindowFocused())
        {
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) && page < pages.Length - 1)
                Turn(page + 1);
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) && page > 0)
                Turn(page - 1);
        }
    }

    // Dots of the pages (click one to go there), Back, and Next or the last page's buttons.
    private void DrawFooter(int count)
    {
        var style = ImGui.GetStyle();
        var height = ImGui.GetFrameHeight();
        var bottom = ImGui.GetWindowContentRegionMax().Y - height;
        ImGui.SetCursorPosY(bottom);

        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var radius = Theme.Px(3.5f);
        var step = Theme.Px(16);
        for (var i = 0; i < count; i++)
        {
            var center = start + new Vector2(radius + i * step, height / 2);
            ImGui.SetCursorScreenPos(center - new Vector2(step / 2, height / 2));
            if (ImGui.InvisibleButton($"##page{i}", new Vector2(step, height)) && i != page)
                Turn(i);
            var on = i == page;
            if (on)
                Fx.Glow(dl, center, radius, Theme.Gold, 1f);
            else
                dl.AddCircleFilled(center, radius * 0.8f, Theme.U32(ImGui.IsItemHovered() ? Theme.GoldDim : Theme.TextFaint), 12);
        }

        var last = page == count - 1;
        var next = last ? Loc.T("Let's go") : Loc.T("Next");
        var back = Loc.T("Back");
        var settings = Loc.T("Open the settings");
        float Width(string label) => ImGui.CalcTextSize(label).X + style.FramePadding.X * 2;
        var right = Width(next) + style.ItemSpacing.X + (page > 0 ? Width(back) + style.ItemSpacing.X : 0)
                    + (last ? Width(settings) + style.ItemSpacing.X : 0);
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - right + style.ItemSpacing.X, bottom));

        if (last)
        {
            if (Theme.GhostButton(settings))
                plugin.OpenSettings();
            ImGui.SameLine();
        }

        if (page > 0)
        {
            if (Theme.GhostButton(back))
                Turn(page - 1);
            ImGui.SameLine();
        }

        if (Theme.PrimaryButton(next))
        {
            if (last)
                IsOpen = false;
            else
                Turn(page + 1);
        }
    }
}
