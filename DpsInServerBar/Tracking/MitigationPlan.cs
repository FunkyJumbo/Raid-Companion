using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DpsInServerBar.Tracking;

/// <summary>
/// A mitigation plan for one fight: for each enemy cast (a row), what each role (a column) uses, like the plans
/// players keep in a spreadsheet. Rows can be linked to a cast of the fight's timeline, which lets the plan follow a
/// pull in progress.
/// </summary>
public sealed class MitigationPlan
{
    public static readonly string[] DefaultColumns = ["MT", "OT", "H1", "H2", "M1", "M2", "R1", "R2"];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>The fight: its territory, and its name as the History shows it.</summary>
    public uint TerritoryId { get; set; }

    public string Encounter { get; set; } = string.Empty;

    /// <summary>Who does what: roles or jobs ("MT", "Scholar"...).</summary>
    /// <remarks>Replaced when read from the settings, not added to: it starts with the default columns.</remarks>
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<string> Columns { get; set; } = [.. DefaultColumns];

    /// <summary>
    /// Undoes the default columns added in front of the saved ones each time the settings were read (before the fix):
    /// as many copies of MT ... R2 as there were reads, then the plan's own columns. True when some were removed.
    /// </summary>
    public bool RemoveRepeatedDefaultColumns()
    {
        var removed = false;
        while (Columns.Count > DefaultColumns.Length && Columns.Take(DefaultColumns.Length).SequenceEqual(DefaultColumns))
        {
            Columns.RemoveRange(0, DefaultColumns.Length);
            removed = true;
        }

        return removed;
    }

    public List<MitigationRow> Rows { get; set; } = [];

    /// <summary>
    /// The role of each column, chosen in the composition (Tank, Healer, DPS: <see cref="PlanRole.Melee"/> stands for any
    /// DPS); missing or <see cref="PlanRole.Any"/>: read from the column's name (an imported "Scholar", "Tank 1 (MT)").
    /// </summary>
    public List<PlanRole> ColumnRoles { get; set; } = [];

    /// <summary>The tabs of the plan (one per phase...), in order; empty: a single page.</summary>
    public List<string> Tabs { get; set; } = [];

    /// <summary>The role (and job) of a column: chosen in the composition, else from its name.</summary>
    public (PlanRole Role, string Job) RoleOf(int column) =>
        column < ColumnRoles.Count && ColumnRoles[column] != PlanRole.Any ? (ColumnRoles[column], string.Empty)
        : column < Columns.Count ? MitigationCatalog.RoleOf(Columns[column])
        : (PlanRole.Any, string.Empty);

    /// <summary>The tab a row is in: its own when the plan has it, else the first one ("" without tabs).</summary>
    public string TabOf(MitigationRow row) => Tabs.Count == 0 ? string.Empty : Tabs.Contains(row.Tab) ? row.Tab : Tabs[0];

    /// <summary>A new position of the composition, named after its role: MT, OT, then T3...; H1, H2...; D1...; Extra.</summary>
    public void AddColumn(PlanRole role)
    {
        var name = NameFor(role, -1);
        while (ColumnRoles.Count < Columns.Count)
            ColumnRoles.Add(PlanRole.Any);
        Columns.Add(name);
        ColumnRoles.Add(role);
    }

    /// <summary>Gives a position another role, and the name that goes with it.</summary>
    public void SetRole(int column, PlanRole role)
    {
        if (column < 0 || column >= Columns.Count)
            return;
        while (ColumnRoles.Count < Columns.Count)
            ColumnRoles.Add(PlanRole.Any);
        ColumnRoles[column] = role;
        Columns[column] = NameFor(role, column);
    }

    // The name of a position of this role, numbered after the other positions of the role (not counting this one).
    private string NameFor(PlanRole role, int except)
    {
        bool Same(int c) => MitigationCatalog.IsDps(role) ? MitigationCatalog.IsDps(RoleOf(c).Role) : RoleOf(c).Role == role;
        var count = Enumerable.Range(0, Columns.Count).Count(c => c != except && Same(c));
        return role switch
        {
            PlanRole.Tank => count switch { 0 => "MT", 1 => "OT", _ => $"T{count + 1}" },
            PlanRole.Healer => $"H{count + 1}",
            PlanRole.Melee or PlanRole.Ranged or PlanRole.Caster => $"D{count + 1}",
            _ => count == 0 ? "Extra" : $"Extra {count + 1}",
        };
    }

    /// <summary>Removes a position, with what each row had in it.</summary>
    public void RemoveColumn(int column)
    {
        if (column < 0 || column >= Columns.Count)
            return;
        Columns.RemoveAt(column);
        if (column < ColumnRoles.Count)
            ColumnRoles.RemoveAt(column);
        foreach (var row in Rows)
        {
            if (column < row.Cells.Count)
                row.Cells.RemoveAt(column);
            if (column < row.Picks.Count)
                row.Picks.RemoveAt(column);
        }
    }

    /// <summary>The skills of a row's column (an empty list, not stored, when it has none).</summary>
    public static List<PlannedAction> Picks(MitigationRow row, int column) => column < row.Picks.Count ? row.Picks[column] : [];

    /// <summary>The skills of a row's column, to change.</summary>
    public static List<PlannedAction> PicksToEdit(MitigationRow row, int column)
    {
        while (row.Picks.Count <= column)
            row.Picks.Add([]);
        return row.Picks[column];
    }

    /// <summary>The text of a row's column (empty when the row has fewer cells).</summary>
    public static string Cell(MitigationRow row, int column) => column < row.Cells.Count ? row.Cells[column] : string.Empty;

    public static void SetCell(MitigationRow row, int column, string text)
    {
        while (row.Cells.Count <= column)
            row.Cells.Add(string.Empty);
        row.Cells[column] = text;
    }
}

/// <summary>One cast of a mitigation plan, and what each column uses for it.</summary>
public sealed class MitigationRow
{
    /// <summary>The part of the fight it belongs to ("P1: Living Liquid"), shown above the first row of each part.</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>The cast as the plan calls it ("Protean 1", "Doll Feeds"...).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The cast of the timeline it stands for (0: not linked), its name, and which use of it in the pull (from 1).</summary>
    public uint ActionId { get; set; }

    public string Action { get; set; } = string.Empty;
    public int Occurrence { get; set; }

    /// <summary>When it was cast in the pull the plan was made from, seconds from the pull start.</summary>
    public double? AtSeconds { get; set; }

    /// <summary>Free text of each column (as imported, or what no skill says).</summary>
    public List<string> Cells { get; set; } = [];

    /// <summary>The skills of each column.</summary>
    public List<List<PlannedAction>> Picks { get; set; } = [];

    public string Note { get; set; } = string.Empty;

    /// <summary>The pre-pull: skills used before the pull, each at its time (seconds before the pull, negative).</summary>
    public bool Prepull { get; set; }

    /// <summary>A separator: a line across the plan (a limit cut, an add phase...), with <see cref="Name"/> as its label (may be empty).</summary>
    public bool Separator { get; set; }

    /// <summary>The tab the row is in (see <see cref="MitigationPlan.Tabs"/>).</summary>
    public string Tab { get; set; } = string.Empty;

    /// <summary>Pre-pull: when its skills are used, in seconds before the pull (8 for 8 s before); null when not set.</summary>
    public double? PrepullSeconds { get; set; }

    /// <summary>When a pre-pull row's skill is used, seconds from the pull (negative): the row's time, else the skill's own.</summary>
    public static double? PrepullAt(MitigationRow row, PlannedAction pick) =>
        row.PrepullSeconds is { } s ? -Math.Abs(s) : pick.Offset;
}

/// <summary>A skill of a plan's cell; in the pre-pull, with when to use it (seconds from the pull, negative before it).</summary>
public sealed class PlannedAction
{
    public uint ActionId { get; set; }
    public double? Offset { get; set; }
}

/// <summary>A skill of your column of the plan not used in time for its cast (AtSeconds: when the cast landed).</summary>
/// <param name="Used">Used in time (the check passed); false: missed.</param>
public sealed record PlanMiss(double AtSeconds, string Cast, uint ActionId, string Action, bool Used = false);

/// <summary>Plans from a spreadsheet, links to a pull's casts, and where a pull in progress is in a plan. Pure logic.</summary>
public static class MitigationPlans
{
    private static readonly Regex PhaseLine = new(@"^(P\d+\b|Phase\s*\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A plan from cells copied out of a spreadsheet (tab separated, as Google Sheets and Excel copy them) or a CSV
    /// export. The header row is the one naming the roles (Tank, MT, Healer...): its cells are the columns, the cell
    /// left of the first one holds the casts. "P1: ..." lines start a part of the fight; other lines go to the notes.
    /// </summary>
    public static MitigationPlan? FromSheet(string text, out string error, Func<string, uint>? idOf = null)
    {
        error = string.Empty;
        var lines = Parse(text);
        var headerIndex = lines.FindIndex(IsHeader);
        if (headerIndex < 0)
        {
            error = "No header row naming the roles (Tank, MT, Healer...) was found.";
            return null;
        }

        var header = lines[headerIndex];
        var first = header.FindIndex(c => c.Length > 0);
        var last = header.FindLastIndex(c => c.Length > 0);
        var castColumn = Math.Max(0, first - 1);
        var firstColumn = first > 0 ? first : 1;
        var plan = new MitigationPlan { Columns = header.Skip(firstColumn).Take(last - firstColumn + 1).Select(Clean).ToList() };

        var phase = string.Empty;
        MitigationRow? lastRow = null;
        var pendingNote = new List<string>();
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
        {
            var filled = line.Select(Clean).Where(c => c.Length > 0).ToList();
            if (filled.Count == 0 || IsHeader(line))
                continue;

            if (filled.Count == 1 && PhaseLine.IsMatch(filled[0]))
            {
                phase = filled[0];
                lastRow = null;
                continue;
            }

            if (i < headerIndex)
                continue; // title, credits

            var cast = castColumn < line.Count ? Clean(line[castColumn]) : string.Empty;
            if (cast.Length > 0 && castColumn != firstColumn)
            {
                lastRow = new MitigationRow { Phase = phase, Name = cast, Prepull = IsPrepull(cast) };
                for (var c = 0; c < plan.Columns.Count; c++)
                {
                    var cell = firstColumn + c < line.Count ? Clean(line[firstColumn + c]) : string.Empty;
                    if (idOf is not null)
                        cell = ReadSkills(cell, plan.RoleOf(c), lastRow.Prepull, idOf, MitigationPlan.PicksToEdit(lastRow, c));
                    MitigationPlan.SetCell(lastRow, c, cell);
                }
                if (pendingNote.Count > 0)
                {
                    lastRow.Note = string.Join("\n", pendingNote);
                    pendingNote.Clear();
                }

                plan.Rows.Add(lastRow);
                continue;
            }

            // A line of text alone: a note of the row above (or of the next one, at the top of a part).
            var note = string.Join(" ", filled);
            if (lastRow is null)
                pendingNote.Add(note);
            else
                lastRow.Note = lastRow.Note.Length == 0 ? note : lastRow.Note + "\n" + note;
        }

        if (plan.Rows.Count == 0)
        {
            error = "The header was found, but no cast under it.";
            return null;
        }

        SplitPrepull(plan);

        // Columns with nothing in them at all (an empty "Extras") are kept: the plan may fill them later.
        return plan;
    }

    // The header names the roles: at least three of its cells start with a role or a job ("Tank 1 (MT)", "Scholar").
    private static readonly Regex RoleCell = new(
        @"^(tank|healer|melee|ranged?|phys|caster|dps|MT|OT|ST|[HMRD]\d|scholar|sage|white mage|astrologian|extras?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsHeader(List<string> line) => line.Select(Clean).Count(c => RoleCell.IsMatch(c)) >= 3;

    private static string Clean(string cell) => cell.Replace("\r", string.Empty).Trim();

    /// <summary>
    /// A pre-pull row whose skills have different times ("Kera -8s", "Benison -5s") becomes one row per time, each
    /// with its time (the earliest first; the skills without one stay in the first row).
    /// </summary>
    private static void SplitPrepull(MitigationPlan plan)
    {
        for (var i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            if (!row.Prepull || row.PrepullSeconds is not null)
                continue;

            var times = row.Picks.SelectMany(p => p).Where(p => p.Offset is not null).Select(p => -p.Offset!.Value)
                           .Distinct().OrderByDescending(t => t).ToList();
            if (times.Count == 0)
                continue;

            var rows = new List<MitigationRow>();
            if (row.Picks.SelectMany(p => p).Any(p => p.Offset is null) || row.Cells.Any(c => c.Length > 0))
                rows.Add(Slice(row, null));
            rows.AddRange(times.Select(t => Slice(row, t)));
            if (rows.Count > 0)
                rows[0].Note = row.Note;
            plan.Rows.RemoveAt(i);
            plan.Rows.InsertRange(i, rows);
            i += rows.Count - 1;
        }
    }

    // The part of a pre-pull row used at this time (null: the skills with no time, and the texts).
    private static MitigationRow Slice(MitigationRow row, double? seconds) => new()
    {
        Phase = row.Phase,
        Name = row.Name,
        Prepull = true,
        PrepullSeconds = seconds,
        Cells = seconds is null ? [.. row.Cells] : [],
        Picks = row.Picks.Select(c => c.Where(p => seconds is null ? p.Offset is null : p.Offset is { } o && -o == seconds)
                                       .Select(p => new PlannedAction { ActionId = p.ActionId }).ToList()).ToList(),
    };

    public static bool IsPrepull(string name) => Regex.IsMatch(name.Trim(), @"^pre[\s-]?pull$", RegexOptions.IgnoreCase);

    // Short names used in plans, to the English name of the skill.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Kera"] = "Kerachole", ["Ixo"] = "Ixochole", ["Tauro"] = "Taurochole", ["Druo"] = "Druochole", ["Physis"] = "Physis II",
        ["EProg"] = "Eukrasian Prognosis II", ["ZoeEProg"] = "Eukrasian Prognosis II", ["EDiag"] = "Eukrasian Diagnosis",
        ["Soil"] = "Sacred Soil", ["Indom"] = "Indomitability", ["Illum"] = "Fey Illumination", ["Excog"] = "Excogitation",
        ["Adlo"] = "Adloquium", ["Reci"] = "Recitation", ["Bless"] = "Fey Blessing", ["Whisper"] = "Whispering Dawn",
        ["Deploy"] = "Deployment Tactics", ["Seraph"] = "Summon Seraph", ["Seraph1"] = "Summon Seraph", ["Seraph2"] = "Consolation",
        ["Succor"] = "Concitation", ["Fairy Tether"] = "Fey Illumination",
        ["CU"] = "Collective Unconscious", ["Neutral"] = "Neutral Sect", ["Intersection"] = "Celestial Intersection",
        ["Opposition"] = "Celestial Opposition", ["Star"] = "Earthly Star", ["Star Place"] = "Earthly Star", ["ED"] = "Essential Dignity",
        ["AspHelios"] = "Helios Conjunction", ["Benefic 2"] = "Benefic II", ["Benefic2"] = "Benefic II",
        ["Benison"] = "Divine Benison", ["Tetra"] = "Tetragrammaton", ["Plenary"] = "Plenary Indulgence", ["Med2"] = "Medica III",
        ["Medica"] = "Medica III", ["Rapture"] = "Afflatus Rapture", ["Liturgy"] = "Liturgy of the Bell", ["Caress"] = "Divine Caress",
        ["TBN"] = "The Blackest Night", ["Minne"] = "Nature's Minne", ["Samba"] = "Shield Samba", ["Barrier"] = "Magick Barrier",
        ["Veil"] = "Divine Veil", ["PoA"] = "Passage of Arms", ["Wings"] = "Passage of Arms", ["PLD Wings"] = "Passage of Arms",
        ["HoL"] = "Heart of Light", ["Missionary"] = "Dark Missionary", ["SIO"] = "Shake It Off", ["Shake"] = "Shake It Off",
        ["HG"] = "Hallowed Ground", ["Bolide"] = "Superbolide", ["LD"] = "Living Dead", ["Holmgang"] = "Holmgang",
    };

    /// <summary>
    /// The skills named in a cell's text ("Soil/Reci+Adlo", "Kera -8s"), within those of the column's role, added to
    /// <paramref name="picks"/>; returns what is left of the text (the parts that are not a skill).
    /// </summary>
    public static string ReadSkills(string text, (PlanRole Role, string Job) column, bool prepull, Func<string, uint> idOf,
                                    List<PlannedAction> picks)
    {
        if (text.Length == 0)
            return text;

        var allowed = MitigationCatalog.For(column.Role, column.Job)
                                       .SelectMany(g => g.Families).SelectMany(f => f)
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = new List<string>();
        foreach (var raw in Regex.Split(text, @"[/+,\n]"))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;

            // "Kera -8s": a time; "Benison(MT)": a target, dropped from the name.
            double? offset = null;
            var time = Regex.Match(part, @"(-?\d+(?:\.\d+)?)\s*s\b");
            if (time.Success && prepull)
                offset = -Math.Abs(double.Parse(time.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            var name = Regex.Replace(part, @"\(.*?\)|-?\d+(?:\.\d+)?\s*s\b", string.Empty).Trim();
            var english = Aliases.TryGetValue(name, out var full) ? full : name;
            var family = MitigationCatalog.Families.TryGetValue(english, out var f) ? f : null;
            var id = family is not null && allowed.Contains(english) ? idOf(family[0]) : 0;
            if (id == 0)
            {
                left.Add(part);
                continue;
            }

            if (picks.All(p => p.ActionId != id))
                picks.Add(new PlannedAction { ActionId = id, Offset = offset });
        }

        return string.Join(" / ", left);
    }

    /// <summary>Lines of cells: tab separated when the text has tabs, comma separated otherwise; quotes as in CSV.</summary>
    public static List<List<string>> Parse(string text)
    {
        var separator = text.Contains('\t') ? '\t' : ',';
        var lines = new List<List<string>>();
        var line = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            if (ch == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (ch == separator)
            {
                line.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\n')
            {
                line.Add(cell.ToString());
                cell.Clear();
                lines.Add(line);
                line = [];
            }
            else if (ch != '\r')
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || line.Count > 0)
        {
            line.Add(cell.ToString());
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The casts of a pull, each with which use of that cast it is (from 1).</summary>
    public static List<(BossCast Cast, int Occurrence)> Numbered(IEnumerable<BossCast> casts)
    {
        var seen = new Dictionary<uint, int>();
        return casts.OrderBy(c => c.AtSeconds)
                    .Select(c =>
                    {
                        seen[c.ActionId] = seen.GetValueOrDefault(c.ActionId) + 1;
                        return (c, seen[c.ActionId]);
                    })
                    .ToList();
    }

    /// <summary>Links a row to a cast of a pull.</summary>
    public static void Link(MitigationRow row, BossCast cast, int occurrence)
    {
        row.ActionId = cast.ActionId;
        row.Action = cast.Action;
        row.Occurrence = occurrence;
        row.AtSeconds = cast.AtSeconds;
    }

    public static void Unlink(MitigationRow row)
    {
        row.ActionId = 0;
        row.Action = string.Empty;
        row.Occurrence = 0;
        row.AtSeconds = null;
    }

    /// <summary>
    /// Links the rows not linked yet to the casts of a pull by their name (the n-th row named like a cast goes to its
    /// n-th use), with <paramref name="nameOf"/> giving the names a cast can go by. Returns how many rows were linked.
    /// </summary>
    public static int LinkByName(MitigationPlan plan, IEnumerable<BossCast> casts, Func<BossCast, IEnumerable<string>> nameOf)
    {
        var numbered = Numbered(casts);
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var linked = 0;
        foreach (var row in plan.Rows)
        {
            if (row.Separator || row.Prepull)
                continue; // not a cast
            var name = row.Name.Trim();
            used[name] = used.GetValueOrDefault(name) + 1;
            if (row.ActionId != 0)
                continue;

            var matches = numbered.Where(n => nameOf(n.Cast).Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count >= used[name])
            {
                var (cast, occurrence) = matches[used[name] - 1];
                Link(row, cast, occurrence);
                linked++;
            }
        }

        return linked;
    }

    /// <summary>
    /// Where a pull is in a plan, from the casts seen so far: the last row whose cast was seen (-1 before the first one),
    /// and when that cast started. Rows not linked are passed once a row after them is.
    /// </summary>
    public static (int Row, double At) Progress(MitigationPlan plan, IReadOnlyList<BossCast> casts)
    {
        var count = new Dictionary<uint, int>();
        var started = new Dictionary<(uint, int), double>();
        foreach (var c in casts.OrderBy(c => c.AtSeconds))
        {
            count[c.ActionId] = count.GetValueOrDefault(c.ActionId) + 1;
            started[(c.ActionId, count[c.ActionId])] = c.AtSeconds;
        }

        var row = -1;
        var at = 0.0;
        for (var i = 0; i < plan.Rows.Count; i++)
        {
            var r = plan.Rows[i];
            if (r.ActionId != 0 && started.TryGetValue((r.ActionId, Math.Max(1, r.Occurrence)), out var t))
            {
                row = i;
                at = t;
            }
        }

        return (row, at);
    }

    // ------------------------------------------------------------------ Your mitigations

    // A mitigation used this long before a cast lands still covers it (most last 10 to 20 s); a little after still counts.
    private const double Before = 15;
    private const double After = 1;

    /// <summary>
    /// The skills of your column not used in time: for each cast seen, a skill counts when one of its family was used
    /// between 15 s before the cast landed and 1 s after; in the pre-pull, around its time (or before the pull).
    /// Only the casts that landed before <paramref name="until"/> are judged.
    /// </summary>
    /// <param name="uses">Your actions: id and seconds from the pull start (negative before it).</param>
    /// <param name="family">The ids that count as an action (all its levels).</param>
    public static List<PlanMiss> Misses(MitigationPlan plan, int column, IReadOnlyList<BossCast> casts,
                                        IReadOnlyList<(uint ActionId, double At)> uses, Func<uint, IReadOnlyCollection<uint>> family,
                                        double until, Func<uint, string> nameOf, Func<uint, bool>? counts = null) =>
        Checks(plan, column, casts, uses, family, until, nameOf, counts).Where(c => !c.Used).ToList();

    /// <summary>Every check of your column (see <see cref="Misses"/>): each skill for each cast judged, used in time or not.</summary>
    public static List<PlanMiss> Checks(MitigationPlan plan, int column, IReadOnlyList<BossCast> casts,
                                        IReadOnlyList<(uint ActionId, double At)> uses, Func<uint, IReadOnlyCollection<uint>> family,
                                        double until, Func<uint, string> nameOf, Func<uint, bool>? counts = null)
    {
        var misses = new List<PlanMiss>();
        if (column < 0 || column >= plan.Columns.Count)
            return misses;

        bool Used(uint id, double from, double to)
        {
            var ids = family(id);
            return uses.Any(u => u.At >= from && u.At <= to && ids.Contains(u.ActionId));
        }

        var count = new Dictionary<uint, int>();
        var landed = new Dictionary<(uint, int), double>();
        foreach (var c in casts.OrderBy(c => c.AtSeconds))
        {
            count[c.ActionId] = count.GetValueOrDefault(c.ActionId) + 1;
            landed[(c.ActionId, count[c.ActionId])] = c.AtSeconds + c.CastSeconds;
        }

        foreach (var row in plan.Rows)
        {
            var picks = MitigationPlan.Picks(row, column).Where(p => counts?.Invoke(p.ActionId) ?? true).ToList();
            if (picks.Count == 0)
                continue;

            if (row.Prepull)
            {
                if (until < After)
                    continue;
                foreach (var p in picks)
                {
                    var at = MitigationRow.PrepullAt(row, p);
                    var from = at is { } o ? o - 5 : -60;
                    misses.Add(new PlanMiss(at ?? 0, row.Name, p.ActionId, nameOf(p.ActionId), Used(p.ActionId, from, After)));
                }

                continue;
            }

            if (row.ActionId == 0 || !landed.TryGetValue((row.ActionId, Math.Max(1, row.Occurrence)), out var hit) || hit + After > until)
                continue;
            foreach (var p in picks)
                misses.Add(new PlanMiss(hit, row.Name, p.ActionId, nameOf(p.ActionId), Used(p.ActionId, hit - Before, hit + After)));
        }

        return misses;
    }

    // ------------------------------------------------------------------ Share codes

    public const string CodePrefix = "RCMIT1:";

    /// <summary>A text code holding the whole plan, for the rest of the group to import.</summary>
    public static string Export(MitigationPlan plan)
    {
        var copy = JsonSerializer.Deserialize<MitigationPlan>(JsonSerializer.SerializeToUtf8Bytes(plan))!;
        copy.Id = string.Empty;
        var json = JsonSerializer.SerializeToUtf8Bytes(copy);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize))
            deflate.Write(json);
        return CodePrefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>The plan held by a code (a new plan, with an id of its own), null when the text is not a plan code.</summary>
    public static MitigationPlan? Import(string code)
    {
        code = code.Trim();
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(code[CodePrefix.Length..]);
            using var input = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var json = new MemoryStream();
            input.CopyTo(json);
            var plan = JsonSerializer.Deserialize<MitigationPlan>(json.ToArray());
            if (plan is null || plan.Columns.Count == 0)
                return null;
            plan.Id = Guid.NewGuid().ToString("N");
            return plan;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or JsonException)
        {
            return null;
        }
    }
}
