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

/// <summary>The Progression tab: pull after pull on one encounter.</summary>
public sealed partial class MainWindow
{
    // The pulls of the encounter, their groups and the figures drawn from them: built again only when the history,
    // the filters, the saved groups, the language or the made-up names change (not on every frame).
    private (IReadOnlyList<PullRecord>? Pulls, string Filter, int SavedGroups, string Language, bool Anonymous) progressionKey;
    private int savedGroupsVersion;
    private List<PullRecord> progressionFight = [];
    private List<PullGroup> progressionGroups = [];
    private string? progressionStatsGroup;
    private ProgressionStats? progressionStats;

    private sealed record ProgressionStats(
        List<PullRecord> Pulls, List<double?> Scores, double[] InstanceHours,
        List<(string Cause, int Count, int Last)> Causes);

    /// <summary>
    /// Progression on the chosen encounter: key figures, how far each pull got (and the best so far), the phases
    /// reached in an ultimate and what wiped the party.
    /// </summary>
    private void DrawProgression(IReadOnlyList<PullRecord> pulls)
    {
        if (historyFilter.Length == 0)
        {
            Theme.Hint(Loc.T("Choose an encounter above to follow its progression pull after pull."));
            return;
        }

        var key = (pulls, historyFilter, savedGroupsVersion, Loc.Code, Names.Anonymize);
        if (key != progressionKey)
        {
            progressionKey = key!;
            progressionFight = Progression.PullsOf(pulls, historyFilter);
            progressionGroups = Progression.Groups(progressionFight, plugin.Configuration.SavedGroups);
            progressionStats = null;
        }

        var fight = progressionFight;
        if (fight.Count == 0)
        {
            Theme.Hint(Loc.T("No pull of this encounter in the ticked kinds of content."));
            return;
        }

        var groups = progressionGroups;
        var group = groups.FirstOrDefault(g => g.Key == plugin.Configuration.ProgressionGroup);
        var chosen = group?.Pulls ?? fight;
        DrawProgressionSelection(groups, group, chosen);

        if (progressionStats is null || progressionStatsGroup != plugin.Configuration.ProgressionGroup)
        {
            progressionStatsGroup = plugin.Configuration.ProgressionGroup;
            var counted = chosen.Where(p => !p.ExcludedFromProgression).ToList();
            // What wiped you, most common first.
            var wipeCauses = counted.Select(p => (Pull: p, Cause: Progression.WipeCause(p)))
                                    .Where(c => c.Cause is not null)
                                    .GroupBy(c => c.Cause!)
                                    .Select(g => (Cause: g.Key, Count: g.Count(), Last: counted.IndexOf(g.Last().Pull) + 1))
                                    .OrderByDescending(c => c.Count)
                                    .Take(8)
                                    .ToList();
            progressionStats = new ProgressionStats(counted, counted.Select(Progression.Score).ToList(),
                                                    Progression.InstanceHours(counted), wipeCauses);
        }

        var prog = progressionStats.Pulls;
        if (prog.Count == 0)
        {
            Theme.Hint(Loc.T("Every pull is left out: tick some in Pulls."));
            return;
        }

        var territory = prog[^1].TerritoryId;
        var ultimate = Progression.IsUltimate(territory);
        var scores = progressionStats.Scores;
        var kills = prog.Count(p => p.Outcome == PullOutcome.Kill);
        var best = prog.Where(p => Progression.Score(p) is not null).MaxBy(p => Progression.Score(p)!.Value);

        var cards = new List<Card>
        {
            new Card(Loc.T("Pulls"), prog.Count.ToString(), Theme.Gold, kills > 0 ? (kills == 1 ? Loc.T("1 kill") : Loc.F("{0} kills",kills)) : Loc.T("no kill yet"),
                                kills > 0 ? Theme.Good : null),
        };
        if (best is not null)
        {
            var number = prog.IndexOf(best) + 1;
            cards.Add(new Card(Loc.T("Best pull"), Progression.Label(best), Theme.Good,
                                          Loc.F("pull {0}, {1:dd/MM HH:mm}",number,best.StartUtc.ToLocalTime())));
        }

        var recent = prog.TakeLast(10).Where(p => Progression.Score(p) is not null).ToList();
        if (recent.Count > 0 && recent.MaxBy(p => Progression.Score(p)!.Value) is { } recentBest)
            cards.Add(new Card(Loc.T("Best of last 10"), Progression.Label(recentBest), Theme.Crystal));

        var instanceHours = progressionStats.InstanceHours;
        var played = TimeSpan.FromSeconds(prog.Sum(p => p.DurationSeconds));
        cards.Add(new Card(Loc.T("Time in fight"), NumberFormat.Duration(played), Theme.Gold,
                                      Loc.F("{0} in the instance since {1:dd/MM}",NumberFormat.Duration(TimeSpan.FromHours(instanceHours[^1])),prog[0].StartUtc.ToLocalTime())));
        // A kill is as far as it goes: the first one, else the last enemy cast of the furthest wipe.
        if (prog.FirstOrDefault(p => p.Outcome == PullOutcome.Kill) is { } kill)
        {
            var number = prog.IndexOf(kill) + 1;
            cards.Add(new Card(Loc.T("Furthest mechanic"), Loc.T("Kill"), Theme.Good,
                               Loc.F("pull {0}, {1:dd/MM HH:mm}",number,kill.StartUtc.ToLocalTime()), ValueColor: Theme.Good));
        }
        else if (prog.Where(p => p.LastBossCast.Length > 0)
                     .MaxBy(p => (Progression.Phase(p), p.DurationSeconds)) is { } furthest)
        {
            var number = prog.IndexOf(furthest) + 1;
            cards.Add(new Card(Loc.T("Furthest mechanic"), furthest.LastBossCastText, Theme.Crystal,
                                          Loc.F("pull {0}, at {1}",number,NumberFormat.Duration(TimeSpan.FromSeconds(furthest.DurationSeconds)))));
        }

        DrawCards(cards, uniform: true);

        if (ultimate)
        {
            var phases = prog.Where(p => p.Outcome != PullOutcome.Kill)
                             .GroupBy(Progression.Phase).OrderBy(g => g.Key)
                             .Select(g => $"{UltimateDefinitions.Label(territory, g.Key)} ×{g.Count()}");
            Theme.Hint(Loc.T("Wipes per phase: ") + string.Join("   ", phases));
        }

        var causes = progressionStats.Causes;

        Theme.SectionHeader(Loc.T("Wiped by"), FontAwesomeIcon.Skull);
        var wipes = prog.Count(p => p.Outcome != PullOutcome.Kill);
        ImGui.TextColored(Theme.TextMuted, wipes == 1 ? Loc.T("1 pull without a kill") : Loc.F("{0} pulls without a kill",wipes));
        Theme.HelpMarker(Loc.T("For each wipe, the most common cause of death in its last 20 seconds (or its first death). Needs the deaths of the party, recorded since this version."));
        if (causes.Count == 0)
        {
            Theme.Hint(Loc.T("No wipe with recorded deaths yet."));
        }
        else
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
            using var table = ImRaii.Table("##wipecauses", 3, flags);
            if (table)
            {
                ImGui.TableSetupColumn(Loc.T("Cause"), ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableSetupColumn(Loc.T("Wipes"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Wipes")).X + Theme.Px(8));
                ImGui.TableSetupColumn(Loc.T("Last pull"), ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(Loc.T("Last pull")).X + Theme.Px(8));
                ImGui.TableHeadersRow();
                foreach (var c in causes)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Bad, c.Cause);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(c.Count.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextMuted, $"#{c.Last}");
                }
            }
        }

        Theme.SectionHeader(Loc.T("Pull after pull"), FontAwesomeIcon.ChartLine);
        if (Theme.Chip(Loc.T("Per pull"), !progressionByTime))
            progressionByTime = false;
        ImGui.SameLine(0, Theme.Px(4));
        if (Theme.Chip(Loc.T("Per time in the instance"), progressionByTime))
            progressionByTime = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Hours spent in the instance: the fights and the time between pulls of a same evening (breaks of more than 30 minutes left out)."));

        var xs = progressionByTime ? instanceHours.ToList() : Enumerable.Range(1, prog.Count).Select(i => (double)i).ToList();
        graphTop = ImGui.GetCursorPosY();
        var clicked = DpsGraph.DrawProgression(
            "##progression", xs, progressionByTime ? Loc.T("Time in the instance") : Loc.T("Pull"), scores,
            prog.Select(p => p.Outcome == PullOutcome.Kill).ToList(), Progression.Max(territory),
            ultimate ? Progression.PhaseNames(territory) : null, ImGui.GetContentRegionAvail().Y,
            i => Loc.F("Pull {0}  {1} in the instance\n{2:dd/MM HH:mm}  {3}\n",i + 1,NumberFormat.Duration(TimeSpan.FromHours(instanceHours[i])),prog[i].StartUtc.ToLocalTime(),NumberFormat.Duration(TimeSpan.FromSeconds(prog[i].DurationSeconds)))
                 + Progression.Label(prog[i])
                 + (Progression.WipeCause(prog[i]) is { } cause ? Loc.F("\nWiped by {0}",cause) : string.Empty)
                 + (prog[i].Outcome != PullOutcome.Kill && prog[i].LastBossCast.Length > 0 ? Loc.F("\nLast enemy cast: {0}",prog[i].LastBossCastText) : string.Empty)
                 + Loc.T("\nClick to open this pull."));
        if (clicked is { } index)
        {
            selectedId = prog[index].Id;
            selectedTab = 1; // open it in History
        }
    }

    /// <summary>
    /// Which pulls the progression covers: a group (found from the party members of each pull) and, in a pop-up, one
    /// checkbox per pull to leave some out (saved with the history).
    /// </summary>
    private void DrawProgressionSelection(IReadOnlyList<PullGroup> groups, PullGroup? group, IReadOnlyList<PullRecord> chosen)
    {
        var config = plugin.Configuration;
        static string GroupLabel(PullGroup g) => $"{g.Label} ({g.Pulls.Count})";

        void Choose(string key)
        {
            config.ProgressionGroup = key;
            config.Save();
        }

        ImGui.SetNextItemWidth(Theme.Px(320));
        using (var combo = ImRaii.Combo(Loc.T("Group"), group is null ? Loc.F("All pulls ({0})",groups.Sum(g => g.Pulls.Count)) : GroupLabel(group),
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                // Search by group name or player.
                if (ImGui.IsWindowAppearing())
                {
                    groupSearch = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##groupsearch", Loc.T("Search a group or a player"), ref groupSearch, 64);

                if (groupSearch.Length == 0 && ImGui.Selectable(Loc.F("All pulls ({0})",groups.Sum(g => g.Pulls.Count)), group is null))
                    Choose(string.Empty);

                var shownSaved = false;
                foreach (var g in groups)
                {
                    if (groupSearch.Length > 0
                        && !g.Label.Contains(groupSearch, StringComparison.OrdinalIgnoreCase)
                        && !g.Members.Contains(groupSearch, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Saved groups first, in gold, then a rule before the automatic ones.
                    if (g.Saved is not null)
                        shownSaved = true;
                    else if (shownSaved)
                    {
                        ImGui.Separator();
                        shownSaved = false;
                    }

                    using (ImRaii.PushColor(ImGuiCol.Text, Theme.GoldBright, g.Saved is not null))
                    {
                        if (ImGui.Selectable($"{GroupLabel(g)}###{g.Key}", g == group))
                            Choose(g.Key);
                    }

                    if (ImGui.IsItemHovered() && g.Members.Length > 0)
                        ImGui.SetTooltip(g.Members);
                }
            }
        }

        if (ImGui.IsItemHovered() && group is { Members.Length: > 0 })
            ImGui.SetTooltip(group.Members);

        // Save an automatic group under a name, or rename / forget a saved one.
        if (group is { Key: not "solo" })
        {
            ImGui.SameLine(0, Theme.Px(8));
            if (Theme.GhostButton(group.Saved is null ? Loc.T("Save group") : Loc.T("Rename")))
            {
                groupName = group.Saved?.Name ?? string.Empty;
                ImGui.OpenPopup("##groupname");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(group.Saved is null
                                     ? Loc.T("Give this group a name, to find it easily in the list. Its regular players are kept.")
                                     : Loc.T("Rename this group."));
            }

            if (group.Saved is { } savedGroup)
            {
                ImGui.SameLine(0, Theme.Px(4));
                using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
                {
                    if (Theme.GhostButton(Loc.T("Forget")))
                    {
                        config.SavedGroups.Remove(savedGroup);
                        savedGroupsVersion++;
                        Choose(string.Empty);
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Loc.T("Hold Ctrl to forget this group's name (its pulls are kept)."));
            }

            using var namePopup = ImRaii.Popup("##groupname");
            if (namePopup)
            {
                ImGui.TextColored(Theme.TextMuted, group.Saved is null ? Loc.T("Name of this group") : Loc.T("New name"));
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(Theme.Px(240));
                var done = ImGui.InputTextWithHint("##groupnameinput", Loc.T("e.g. Static"), ref groupName, 48, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                done |= Theme.GhostButton(Loc.T("OK"));
                if (done && groupName.Trim().Length > 0)
                {
                    if (group.Saved is { } existing)
                    {
                        existing.Name = groupName.Trim();
                        savedGroupsVersion++;
                        config.Save();
                    }
                    else
                    {
                        var created = new SavedGroup { Name = groupName.Trim(), Players = group.Regulars.ToList() };
                        config.SavedGroups.Add(created);
                        savedGroupsVersion++;
                        Choose($"saved:{created.Id}");
                    }

                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.SameLine(0, Theme.Px(8));
        var excluded = chosen.Count(p => p.ExcludedFromProgression);
        if (Theme.GhostButton(excluded > 0 ? Loc.F("Pulls ({0}/{1})",chosen.Count - excluded,chosen.Count) : Loc.T("Pulls")))
            ImGui.OpenPopup("##progressionpulls");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T("Tick the pulls to count, e.g. to leave out a night with another group."));

        Theme.HelpMarker(Loc.T("Groups are found from the party members of each pull: a pull goes with the group it shares at least half of its players with. Hover a group for its players.\nSave a group under a name to find it easily: its regular players are kept, and the pulls they play together go to it from then on. Saved groups come first, in gold."));

        using var popup = ImRaii.Popup("##progressionpulls");
        if (!popup)
            return;

        if (Theme.GhostButton(Loc.T("Tick all")))
            SetExcluded(chosen, false);
        ImGui.SameLine();
        if (Theme.GhostButton(Loc.T("Untick all")))
            SetExcluded(chosen, true);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(Theme.TextFaint, Loc.T("Shift+click: up to the last pull clicked"));

        // One block per entry into the duty, newest first: its box ticks or unticks all its pulls at once.
        var runs = Runs(chosen);
        runs.Reverse();
        using var child = ImRaii.Child("##progressionpulllist",
                                       new Vector2(Theme.Px(480), Math.Min(Theme.Px(360), (chosen.Count + runs.Count + 1) * ImGui.GetFrameHeightWithSpacing())));
        if (!child)
            return;

        // Newest first, with the evening it was played and how far it got. Shift+click sets every pull between the last
        // one clicked and this one the same way.
        var list = runs.SelectMany(r => Enumerable.Reverse(r)).ToList();
        var runStart = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var run = runs.First(r => r.Contains(p));
            if (i == runStart)
            {
                runStart += run.Count;
                if (i > 0)
                    ImGui.Spacing();
                var ticked = run.Count(x => !x.ExcludedFromProgression);
                var all = ticked == run.Count;
                if (ImGui.Checkbox($"{Loc.F("Entry {0}", run[0].StartUtc.ToLocalTime().ToString("dd/MM HH:mm"))}###run{run[0].Id}", ref all))
                    SetExcluded(run, !all);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.T("Tick or untick every pull of this entry into the duty."));
                ImGui.SameLine(Theme.Px(170));
                var best = run.OrderByDescending(x => Progression.Score(x) ?? -1).First();
                ImGui.TextColored(Theme.TextMuted, Loc.F("{0}/{1} pulls", ticked, run.Count));
                ImGui.SameLine(Theme.Px(270));
                ImGui.TextColored(best.Outcome == PullOutcome.Kill ? Theme.Good : Theme.TextMuted, Loc.F("best: {0}", Progression.Label(best)));
            }

            using var indent = ImRaii.PushIndent();
            var included = !p.ExcludedFromProgression;
            if (ImGui.Checkbox($"{p.StartUtc.ToLocalTime():HH:mm}###{p.Id}", ref included))
            {
                var anchor = list.FindIndex(x => x.Id == lastTickedPull);
                if (ImGui.GetIO().KeyShift && anchor >= 0)
                    SetExcluded(list.GetRange(Math.Min(anchor, i), Math.Abs(anchor - i) + 1), !included);
                else
                    SetExcluded([p], !included);
                lastTickedPull = p.Id;
            }
            ImGui.SameLine(Theme.Px(150));
            ImGui.TextColored(p.Outcome == PullOutcome.Kill ? Theme.Good : Theme.Text, Progression.Label(p));
            ImGui.SameLine(Theme.Px(270));
            ImGui.TextColored(Theme.TextFaint, groups.FirstOrDefault(g => g.Pulls.Contains(p))?.Label ?? string.Empty);
        }
    }

    /// <summary>
    /// The pulls split by entry into the duty, oldest first. Older pulls, recorded without it: the pulls of the same
    /// zone less than 15 minutes apart go together.
    /// </summary>
    private static List<List<PullRecord>> Runs(IEnumerable<PullRecord> pulls)
    {
        var runs = new List<List<PullRecord>>();
        PullRecord? previous = null;
        foreach (var p in pulls.OrderBy(p => p.StartUtc))
        {
            var same = previous is not null
                       && (p.InstanceRun is { } run
                               ? run == previous.InstanceRun
                               : previous.InstanceRun is null && p.TerritoryId == previous.TerritoryId
                                 && p.StartUtc - previous.StartUtc.AddSeconds(previous.DurationSeconds) < TimeSpan.FromMinutes(15));
            if (!same)
                runs.Add([]);
            runs[^1].Add(p);
            previous = p;
        }

        return runs;
    }

    private void SetExcluded(IEnumerable<PullRecord> pulls, bool excluded)
    {
        foreach (var p in pulls)
        {
            p.ExcludedFromProgression = excluded;
            plugin.History.Changed(p);
        }
    }
}
