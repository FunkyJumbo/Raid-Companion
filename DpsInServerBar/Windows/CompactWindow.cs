using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DpsInServerBar.Combat;
using DpsInServerBar.Display;
using DpsInServerBar.UI;

namespace DpsInServerBar.Windows;

/// <summary>
/// Small pop-up meter without a title bar: one line per player with DPS or rDPS, share and deaths.
/// Dragged by its background; clicking the metric header switches DPS / rDPS, right click opens a menu.
/// </summary>
public sealed class CompactWindow : Window
{
    private readonly Plugin plugin;
    private IDisposable? themeScope;
    private IDisposable? styleScope;

    public CompactWindow(Plugin plugin)
        : base(Loc.T("DPS meter###DpsInServerBarCompact"),
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
               | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize)
    {
        Loc.Changed += () => WindowName = Loc.T("DPS meter###DpsInServerBarCompact");
        this.plugin = plugin;
        AllowBackgroundBlur = false;
        RespectCloseHotkey = false;
        shot = new WindowShot(this);
    }

    private readonly WindowShot shot;

    public override void PreDraw()
    {
        BgAlpha = Theme.WindowAlpha(plugin.Configuration.CompactOpacity);
        themeScope = Theme.Push(WindowName);
        styleScope = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Theme.Px(10))
                          .Push(ImGuiStyleVar.WindowPadding, Theme.Px(new Vector2(8, 7)));
        shot.PreDraw();
    }

    public override void PostDraw()
    {
        styleScope?.Dispose();
        styleScope = null;
        themeScope?.Dispose();
        themeScope = null;
    }

    public override void Draw()
    {
        shot.Step();
        Theme.BlurBehind(plugin.Configuration.CompactOpacity > 0.05f);
        var config = plugin.Configuration;
        DrawMenu(config);

        // The window takes the size of its content: it cannot be resized by hand.
        var snapshot = plugin.Tracker.Latest;
        if (snapshot is not null)
        {
            DrawTable(snapshot, config);
            bars.Sparks.Draw(ImGui.GetWindowDrawList());
        }
        else
            Theme.Hint(Loc.T("No combat data yet."));

        DrawBreathing();
        DrawPullEnd();
    }

    // ---- In combat, the frame breathes; when a pull ends, the meter freezes like glass and a summary slides up.

    private float breathing;
    private DateTime endShownFor;
    private float endShownAt = -100f;
    private const float EndShown = 5f;

    private void DrawBreathing()
    {
        var inCombat = plugin.Pulls.Current is not null;
        breathing = Fx.Enabled ? Math.Clamp(breathing + (inCombat ? 1 : -1) * Fx.Delta * 2f, 0f, 1f) : 0f;
        if (breathing <= 0)
            return;
        var pulse = 0.5f + 0.5f * MathF.Sin(Fx.Now * 2f);
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min, max, false);
        dl.AddRect(min + new Vector2(0.5f), max - new Vector2(0.5f), Theme.U32(Theme.Gold, breathing * (0.2f + 0.45f * pulse)),
                   ImGui.GetStyle().WindowRounding, ImDrawFlags.None, Theme.Px(1f + pulse));
        dl.PopClipRect();
    }

    private void DrawPullEnd()
    {
        if (plugin.Pulls.LastFinishedUtc != endShownFor)
        {
            endShownFor = plugin.Pulls.LastFinishedUtc;
            if (Fx.Enabled && (DateTime.UtcNow - endShownFor).TotalSeconds < 5)
                endShownAt = Fx.Now;
        }

        var age = Fx.Now - endShownAt;
        if (age is < 0 or > EndShown || plugin.Pulls.LastFinished is not { } pull || plugin.Pulls.Current is not null)
            return;

        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min, max, true);
        var rounding = ImGui.GetStyle().WindowRounding;

        // Glass: a cool sheen settling on the meter, a light passing once over it.
        var glass = Fx.Clamp01(age / 0.3f) * Fx.Clamp01((EndShown - age) / 0.8f);
        dl.AddRectFilled(min, max, Theme.U32(new Vector4(0.8f, 0.9f, 1f, 1f), 0.07f * glass), rounding);
        var pass = age / 0.9f;
        if (pass < 1)
        {
            var band = Theme.Px(40);
            var x = min.X - band + (max.X - min.X + band * 2) * Fx.EaseOut(pass);
            uint none = Theme.U32(Vector4.One, 0f), lit = Theme.U32(Vector4.One, 0.22f);
            dl.AddRectFilledMultiColor(new Vector2(x - band, min.Y), new Vector2(x, max.Y), none, lit, lit, none);
            dl.AddRectFilledMultiColor(new Vector2(x, min.Y), new Vector2(x + band, max.Y), lit, none, none, lit);
        }

        // The summary, sliding up from the bottom edge, and down again.
        var slide = Fx.EaseOut((age - 0.4f) / 0.4f) * (1 - Fx.EaseOut((age - EndShown + 0.5f) / 0.5f));
        if (slide > 0)
        {
            var outcome = pull.Outcome switch
            {
                Tracking.PullOutcome.Kill => Loc.T("Kill"),
                Tracking.PullOutcome.Wipe => Loc.T("Wipe"),
                _ => Loc.T("Pull over"),
            };
            var text = $"{outcome} · {NumberFormat.Duration(TimeSpan.FromSeconds(pull.DurationSeconds))} · {NumberFormat.Format(pull.YourDps, NumberStyle.Grouped)} {Loc.T("DPS")}";
            var pad = Theme.Px(new Vector2(10, 5));
            var size = ImGui.CalcTextSize(text) + pad * 2;
            var at = new Vector2((min.X + max.X - size.X) / 2, max.Y - Theme.Px(6) - size.Y * slide);
            var color = pull.Outcome == Tracking.PullOutcome.Wipe ? Theme.Bad : Theme.Gold;
            dl.AddRectFilled(at, at + size, Theme.U32(Theme.Panel with { W = 1 }, 0.95f), Theme.Px(6));
            dl.AddRect(at, at + size, Theme.U32(color, 0.8f), Theme.Px(6));
            dl.AddText(at + pad, Theme.U32(Theme.Lighten(color, 0.4f)), text);
        }

        dl.PopClipRect();
    }

    // ---- At a pull's start the rows unfold one after the other.
    private DateTime unfoldFor;
    private float unfoldAt = -100f;

    private float Unfold(int row)
    {
        if (plugin.Pulls.Current is { } pull && pull.StartUtc != unfoldFor)
        {
            unfoldFor = pull.StartUtc;
            unfoldAt = Fx.Enabled ? Fx.Now : -100f;
        }

        return Fx.EaseOut((Fx.Now - unfoldAt - row * 0.07f) / 0.3f);
    }

    /// <summary>Right click menu: metric, opacity, main window, history, settings, screenshot, close.</summary>
    private void DrawMenu(Configuration config)
    {
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##compactmenu");

        using var popup = ImRaii.Popup("##compactmenu");
        if (!popup)
            return;

        if (ImGui.MenuItem(Loc.T("DPS"), string.Empty, !config.SortByRdps))
        {
            config.SortByRdps = false;
            config.Save();
        }

        if (ImGui.MenuItem(Loc.T("rDPS"), string.Empty, config.SortByRdps))
        {
            config.SortByRdps = true;
            config.Save();
        }

        if (ImGui.MenuItem(Loc.T("Header"), string.Empty, config.CompactShowHeader))
        {
            config.CompactShowHeader = !config.CompactShowHeader;
            config.Save();
        }

        var percent = (int)Math.Round(config.CompactOpacity * 100);
        ImGui.SetNextItemWidth(Theme.Px(140));
        if (Theme.SliderInt(Loc.T("Opacity"), ref percent, 0, 100, "%d%%"))
            config.CompactOpacity = percent / 100f;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        ImGui.Separator();
        if (ImGui.MenuItem(Loc.T("Main window")))
            plugin.ShowMainWindow();
        if (ImGui.MenuItem(Loc.T("History")))
            plugin.ShowMainWindow(history: true);
        if (ImGui.MenuItem(Loc.T("Settings")))
            plugin.OpenCompactSettings();
        if (ImGui.MenuItem(Loc.T("Screenshot"), false, shot.Ready))
            shot.Request(Loc.T("DPS meter"));
        if (ImGui.MenuItem(Loc.T("Close")))
            plugin.SetCompact(false);
    }

    /// <summary>Draws the meter.</summary>
    // The bars' animations (Appearance settings): gliding, a flash and sparks on a pass, a light on the top one.
    private readonly BarAnimator bars = new();

    private void DrawTable(CombatSnapshot snapshot, Configuration config)
    {
        var byRdps = config.SortByRdps && snapshot.Combatants.Any(c => c.HasRdps);
        double Value(CombatantInfo c) => byRdps && c.HasRdps ? c.RDps : c.EncDps;

        var rows = snapshot.Combatants.OrderByDescending(Value).ToList();
        var top = rows.Count > 0 ? rows.Max(Value) : 0;
        var total = rows.Sum(c => byRdps ? c.HasRdps ? c.RDps : 0 : c.EncDps);

        var iconSize = ImGui.GetTextLineHeight() + Theme.Px(2);

        // Optional columns, chosen in the settings (Display > Compact meter).
        var extras = new List<(string Header, string Widest, Func<CombatantInfo, (string Text, Vector4 Color)> Cell)>();
        var shown = config.CompactColumns;
        if (shown.HasFlag(CompactColumn.Share))
            extras.Add(("%", "100.0%", c => (total > 0 && (!byRdps || c.HasRdps) ? $"{Value(c) / total:0.0%}" : "-", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.Crit))
            extras.Add((Loc.T("Crit"), "100%", c => ($"{c.CritPercent:0}%", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.DirectHit))
            extras.Add((Loc.T("DH"), "100%", c => ($"{c.DirectHitPercent:0}%", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.CritDirectHit))
            extras.Add((Loc.T("Crit DH"), "100%", c => ($"{c.CritDirectHitPercent:0}%", Theme.TextMuted)));
        if (shown.HasFlag(CompactColumn.Deaths))
            extras.Add((Loc.T("Deaths"), Loc.T("Deaths"), c => (c.Deaths.ToString(), c.Deaths > 0 ? Theme.Bad : Theme.TextFaint)));

        // Fixed column widths, sized for the widest possible value, so the window does not twitch as numbers change.
        // The name column leaves a little room for the bar past the longest name.
        var nameWidth = Math.Max(ImGui.CalcTextSize(Loc.T("Player")).X,
                                 rows.Count > 0 ? rows.Max(c => ImGui.CalcTextSize(Names.Show(c.Name)).X) + iconSize + Theme.Px(13) : 0);
        var valueWidth = ImGui.CalcTextSize("000,000").X;
        var widths = extras.Select(e => Math.Max(ImGui.CalcTextSize(e.Widest).X, ImGui.CalcTextSize(e.Header).X)).ToList();
        var columns = 2 + extras.Count;

        // Header background with rounded top corners, following the window's corners (the table's own is square).
        var cellPadding = ImGui.GetStyle().CellPadding;
        var headerHeight = ImGui.GetTextLineHeight() + cellPadding.Y * 2;
        var tableWidth = nameWidth + valueWidth + widths.Sum() + cellPadding.X * 2 * columns;
        var origin = ImGui.GetCursorScreenPos();
        if (config.CompactShowHeader)
            ImGui.GetWindowDrawList().AddRectFilled(origin, origin + new Vector2(tableWidth, headerHeight),
                                                Theme.U32(Theme.Gold, 0.45f), Theme.Px(6), ImDrawFlags.RoundCornersTop);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX;
        using var table = Theme.Table("##compactmeter", columns, flags, new Vector2(tableWidth, 0));
        if (!table)
            return;

        ImGui.TableSetupColumn(Loc.T("Player"), ImGuiTableColumnFlags.WidthFixed, nameWidth);
        ImGui.TableSetupColumn(byRdps ? Loc.T("rDPS") : Loc.T("DPS"), ImGuiTableColumnFlags.WidthFixed, valueWidth);
        for (var i = 0; i < extras.Count; i++)
            ImGui.TableSetupColumn(extras[i].Header, ImGuiTableColumnFlags.WidthFixed, widths[i]);

        // Headers drawn one by one so the metric header can be clicked.
        var clear = Vector4.Zero;
        if (config.CompactShowHeader)
        using (ImRaii.PushColor(ImGuiCol.TableHeaderBg, clear).Push(ImGuiCol.HeaderHovered, clear)
                     .Push(ImGuiCol.HeaderActive, clear).Push(ImGuiCol.Text, Vector4.One))
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var i = 0; i < columns; i++)
            {
                ImGui.TableSetColumnIndex(i);
                ImGui.TableHeader(ImGui.TableGetColumnName(i));
                if (i != 1)
                    continue;

                if (ImGui.IsItemClicked())
                {
                    config.SortByRdps = !config.SortByRdps;
                    config.Save();
                }

                if (ImGui.IsItemHovered())
                    Theme.Tooltip(Loc.T("Click: switch DPS / rDPS\nRight click anywhere: menu"));
            }
        }

        var rank = 0;
        var rowIndex = 0;
        foreach (var c in rows)
        {
            var unfold = Unfold(rowIndex++);
            using var rowFade = unfold < 1 ? ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (0.05f + 0.95f * unfold)) : null;
            ImGui.TableNextRow(ImGuiTableRowFlags.None, iconSize + Theme.Px(2));
            if (c.HasJob)
                rank++;
            Theme.RowHover();
            var color = c.IsYou ? Theme.GoldBright : Theme.Text;
            void Cell(string text, Vector4 textColor)
            {
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextColored(textColor, text);
            }

            ImGui.TableNextColumn();
            var value = Value(c);

            // The bar spans the name and the value columns: drawn in two parts, one per cell, split on the column edge.
            var start = ImGui.GetCursorScreenPos();
            var span = nameWidth + cellPadding.X * 2 + valueWidth;
            var (share, flash) = bars.Bar(snapshot.StartUtc, c.Name, top > 0 ? (float)Math.Clamp(value / top, 0, 1) : 0,
                                          c.HasJob ? rank : 0, start, span, iconSize, c.IsYou);
            var endX = start.X + span * share * unfold;
            var splitX = start.X + nameWidth + cellPadding.X;
            var (dead, sinceRaise) = bars.Life(snapshot.StartUtc, c.Name, plugin.Tracker.IsDead(c.Name));
            var barColor = Theme.U32(BarAnimator.LifeColor(JobInfo.RoleColor(c.Job), dead, sinceRaise), c.IsYou ? 0.34f : 0.2f);
            if (dead > 0)
                color = Theme.TextFaint;
            var drawList = ImGui.GetWindowDrawList();
            if (endX > start.X)
                drawList.AddRectFilled(start, new Vector2(Math.Min(endX, splitX), start.Y + iconSize), barColor, Theme.Px(3),
                                       endX > splitX ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersAll);

            var sinceDeath = bars.SinceDeath(snapshot.StartUtc, c.Name, c.Deaths);
            var shake = BarAnimator.Shake(sinceDeath);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + shake);
            JobInfo.DrawIcon(c.Job, iconSize);
            BarAnimator.DeathOverlay(drawList, start + new Vector2(shake + iconSize / 2, iconSize / 2), start,
                                     start + new Vector2(span, iconSize), sinceDeath);
            ImGui.SameLine(0, Theme.Px(5));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(color, Names.Show(c.Name));

            ImGui.TableNextColumn();
            if (endX > splitX)
                drawList.AddRectFilled(new Vector2(splitX, start.Y), new Vector2(endX, start.Y + iconSize), barColor, Theme.Px(3),
                                       ImDrawFlags.RoundCornersRight);
            BarAnimator.Overlay(drawList, start, new Vector2(endX, start.Y + iconSize), flash, c.IsYou && dead <= 0);
            BarAnimator.Broken(drawList, start, new Vector2(endX, start.Y + iconSize), dead);
            BarAnimator.Raise(drawList, start, start + new Vector2(span, iconSize), sinceRaise);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(color, byRdps && !c.HasRdps ? "-" : NumberFormat.Format(value, NumberStyle.Grouped));

            foreach (var extra in extras)
            {
                var (text, textColor) = extra.Cell(c);
                Cell(text, textColor);
            }
        }
    }
}
