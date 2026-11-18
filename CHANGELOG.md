# Changelog

<!-- One "## <version>" section per release. The Release workflow publishes the section of the tagged
     version as the GitHub release notes; the same notes go into pluginmaster.json (shown in Dalamud). -->

## 1.0.7.0

- Mitigation plans (Mitigation plans in Live): the skills each role uses on each cast of a fight, and before the pull, picked by their icon. Made from the casts of a pull in History, imported from a spreadsheet or shared with a code. Followed during the pull, and with your role chosen, the skills you missed show in Personal and on the History timeline.
- History and Progression: the kinds of content go from the hardest (ultimate) to the easiest, and the fights of the list follow them, the latest first; a filter button by the search chooses the expansions shown.
- Casts & debuffs: the enemies' attacks with no cast bar that hurt the party are recorded too (instant raidwides, adds fed to the boss...).
- Rotation errors: Paladin Atonement combo started over too early, Gunbreaker Reign of Beasts combo broken; a broken combo or a lost continuation shows as an error of the combo, not of the action that broke it.

## 1.0.6.0

- Combo errors are now rotation errors, with errors of their own for Paladin, Warrior, Dark Knight and Gunbreaker: buffs that ran out unused, actions used without Surging Tempest or Darkside, Delirium and Gnashing Fang combos broken, lost continuations, a wasted The Blackest Night, and more (hover the "?" for the list).
- Server bar: the two rank elements are now one. Tick Rank for your rank in the party, and click its example in the settings to switch to your rank in your role.
- Screenshots: a camera in the title bar of the main window (instead of the right click on its tabs) and of the opener editor.

## 1.0.5.0

- Opener editor: make your own openers, edit a copy of a built-in one, record one by playing it, and share them with a code (/raidcompanion opener edit, or the pencil in the opener practice).
- Screenshots of the settings, the compact DPS meter and the opener practice (full and compact).
- New plugin icon.
- Lighter on performance during fights.
- Various UI changes and adjustments.
- Dno dymgehk eh lryd. Ryja vih.

## 1.0.4.0

- Removed: ACT support and the FFLogs percentile. The plugin now only uses its built-in tracker and makes no network connection. FFLogs credentials saved in the settings are deleted.
- DPS and uptime graphs: fixed height, zoom with the mouse wheel (drag to move, double click to reset).
- Positionals: every positional action is shown with how many landed (e.g. 2/2 in green, 2/3 in red after a miss).
- GCD uptime: "Late" and "No GCD" are merged into "No GCD".
- Party table and compact meter: new Crit DH column, the share of hits both critical and direct.
- Live and History: each card can be hidden, and a GCD uptime card can be added (Settings > Display).
- Opener practice: the countdown goes from 5 to 16 s.
- Clipping: a table under the clipping timeline shows each clip with the GCD before, the oGCDs woven and the GCD that came late (new pulls).
- Various UI changes and adjustments.

## 1.0.3.0

- Progression: in an ultimate, phases are shown in alternating bands on the graph.
- History: ultimates are listed by their name in the instance filter (e.g. The Epic of Alexander instead of Liminal Space).
- Opener practice: opener names, notes and action names are translated in French and German.
- New language: Al Bhed. Ycg sa ruf E ghuf.
- Action and debuff names (timeline, fight log, mitigation, positionals, combos) follow the plugin language, older pulls included. Causes of death, failed mechanics, DoTs and the last enemy cast too, for new pulls.

## 1.0.2.0

- Fix: in an ultimate, a pull could be shown in the phase reached by the previous pull (e.g. a P1 wipe listed as P2). Pulls already recorded are fixed too.

## 1.0.1.0

- Ultimates: The Epic of Alexander P3 (Alexander Prime) is tracked, with its enrage (4:35) built in.

## 1.0.0.0
