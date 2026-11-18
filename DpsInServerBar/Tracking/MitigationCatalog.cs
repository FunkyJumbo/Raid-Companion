using System;
using System.Collections.Generic;
using System.Linq;

namespace DpsInServerBar.Tracking;

/// <summary>The role a column of a mitigation plan stands for: what its cells can hold.</summary>
public enum PlanRole
{
    Any,
    Tank,
    Healer,
    Melee,
    Ranged,
    Caster,
}

/// <summary>
/// The mitigations (and heals that matter for a plan) of each role and job, by their English name. Each entry is a
/// family: its first name is the one shown (the highest level), the others are the lower levels, which count as the
/// same action (Guardian and Sentinel). Pure data; the game ids come from <see cref="UI.PlanActions"/>.
/// </summary>
public static class MitigationCatalog
{
    /// <summary>One group of the picker: a job ("" for the role actions) and its families of actions.</summary>
    public sealed record Group(PlanRole Role, string Job, string[][] Families);

    public static readonly Group[] Groups =
    [
        new(PlanRole.Tank, "", [["Rampart"], ["Reprisal"], ["Arm's Length"]]),
        new(PlanRole.Tank, "PLD", [["Guardian", "Sentinel"], ["Hallowed Ground"], ["Holy Sheltron", "Sheltron"], ["Intervention"],
                                    ["Cover"], ["Divine Veil"], ["Passage of Arms"], ["Bulwark"], ["Clemency"]]),
        new(PlanRole.Tank, "WAR", [["Damnation", "Vengeance"], ["Holmgang"], ["Bloodwhetting", "Raw Intuition"], ["Nascent Flash"],
                                    ["Thrill of Battle"], ["Shake It Off"], ["Equilibrium"]]),
        new(PlanRole.Tank, "DRK", [["Shadowed Vigil", "Shadow Wall"], ["Living Dead"], ["The Blackest Night"], ["Dark Mind"],
                                    ["Dark Missionary"], ["Oblation"]]),
        new(PlanRole.Tank, "GNB", [["Great Nebula", "Nebula"], ["Superbolide"], ["Heart of Corundum", "Heart of Stone"], ["Camouflage"],
                                    ["Heart of Light"], ["Aurora"]]),

        new(PlanRole.Healer, "WHM", [["Temperance"], ["Divine Caress"], ["Divine Benison"], ["Aquaveil"], ["Liturgy of the Bell"],
                                      ["Plenary Indulgence"], ["Asylum"], ["Benediction"], ["Tetragrammaton"], ["Medica III", "Medica II"],
                                      ["Afflatus Rapture"], ["Cure III"], ["Assize"], ["Regen"]]),
        new(PlanRole.Healer, "SCH", [["Sacred Soil"], ["Expedient"], ["Fey Illumination"], ["Summon Seraph"], ["Consolation"], ["Seraphism"],
                                      ["Recitation"], ["Deployment Tactics"], ["Indomitability"], ["Whispering Dawn"], ["Fey Blessing"],
                                      ["Excogitation"], ["Protraction"], ["Lustrate"], ["Dissipation"], ["Emergency Tactics"],
                                      ["Concitation", "Succor"], ["Adloquium"], ["Accession"]]),
        new(PlanRole.Healer, "AST", [["Collective Unconscious"], ["Neutral Sect"], ["Sun Sign"], ["Celestial Opposition"], ["Earthly Star"],
                                      ["Macrocosmos"], ["Horoscope"], ["Exaltation"], ["Celestial Intersection"], ["Essential Dignity"],
                                      ["Synastry"], ["Lady of Crowns"], ["The Bole"], ["The Ewer"], ["The Spire"], ["The Arrow"],
                                      ["Helios Conjunction", "Aspected Helios"], ["Aspected Benefic"], ["Benefic II"]]),
        new(PlanRole.Healer, "SGE", [["Kerachole"], ["Holos"], ["Panhaima"], ["Physis II", "Physis"], ["Ixochole"], ["Philosophia"],
                                      ["Zoe"], ["Pneuma"], ["Haima"], ["Taurochole"], ["Druochole"], ["Krasis"], ["Soteria"],
                                      ["Eukrasian Prognosis II", "Eukrasian Prognosis"], ["Eukrasian Diagnosis"], ["Kardia"]]),

        new(PlanRole.Melee, "", [["Feint"], ["Bloodbath"], ["Second Wind"], ["Arm's Length"]]),
        new(PlanRole.Melee, "MNK", [["Mantra"], ["Riddle of Earth"]]),
        new(PlanRole.Melee, "NIN", [["Shade Shift"]]),
        new(PlanRole.Melee, "SAM", [["Tengentsu", "Third Eye"]]),
        new(PlanRole.Melee, "RPR", [["Arcane Crest"]]),

        new(PlanRole.Ranged, "", [["Second Wind"], ["Arm's Length"]]),
        new(PlanRole.Ranged, "BRD", [["Troubadour"], ["Nature's Minne"]]),
        new(PlanRole.Ranged, "MCH", [["Tactician"], ["Dismantle"]]),
        new(PlanRole.Ranged, "DNC", [["Shield Samba"], ["Improvisation"], ["Curing Waltz"]]),

        new(PlanRole.Caster, "", [["Addle"]]),
        new(PlanRole.Caster, "BLM", [["Manaward"]]),
        new(PlanRole.Caster, "SMN", [["Radiant Aegis"], ["Rekindle"]]),
        new(PlanRole.Caster, "RDM", [["Magick Barrier"], ["Vercure"]]),
        new(PlanRole.Caster, "PCT", [["Tempera Grassa", "Tempera Coat"]]),
    ];

    private static readonly (string Pattern, PlanRole Role, string Job)[] ColumnNames =
    [
        ("WHM", PlanRole.Healer, "WHM"), ("White Mage", PlanRole.Healer, "WHM"),
        ("SCH", PlanRole.Healer, "SCH"), ("Scholar", PlanRole.Healer, "SCH"),
        ("AST", PlanRole.Healer, "AST"), ("Astrologian", PlanRole.Healer, "AST"),
        ("SGE", PlanRole.Healer, "SGE"), ("Sage", PlanRole.Healer, "SGE"),
        ("PLD", PlanRole.Tank, "PLD"), ("Paladin", PlanRole.Tank, "PLD"),
        ("WAR", PlanRole.Tank, "WAR"), ("Warrior", PlanRole.Tank, "WAR"),
        ("DRK", PlanRole.Tank, "DRK"), ("Dark Knight", PlanRole.Tank, "DRK"),
        ("GNB", PlanRole.Tank, "GNB"), ("Gunbreaker", PlanRole.Tank, "GNB"),
        ("Tank", PlanRole.Tank, ""), ("MT", PlanRole.Tank, ""), ("OT", PlanRole.Tank, ""), ("ST", PlanRole.Tank, ""),
        ("Heal", PlanRole.Healer, ""), ("H1", PlanRole.Healer, ""), ("H2", PlanRole.Healer, ""),
        ("Melee", PlanRole.Melee, ""), ("M1", PlanRole.Melee, ""), ("M2", PlanRole.Melee, ""),
        ("D1", PlanRole.Melee, ""), ("D2", PlanRole.Melee, ""),
        ("Phys", PlanRole.Ranged, ""), ("Ranged", PlanRole.Ranged, ""), ("Range", PlanRole.Ranged, ""), ("R1", PlanRole.Ranged, ""),
        ("D3", PlanRole.Ranged, ""),
        ("Caster", PlanRole.Caster, ""), ("Magic", PlanRole.Caster, ""), ("R2", PlanRole.Caster, ""), ("D4", PlanRole.Caster, ""),
    ];

    /// <summary>The role (and job, when the column names one) of a column, from its name: "Tank 1 (MT)", "Scholar", "R2".</summary>
    public static (PlanRole Role, string Job) RoleOf(string column)
    {
        foreach (var (pattern, role, job) in ColumnNames)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(column, $@"\b{System.Text.RegularExpressions.Regex.Escape(pattern)}",
                                                              System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return (role, job);
        }

        return (PlanRole.Any, string.Empty);
    }

    public static readonly string[] TankJobs = ["PLD", "WAR", "DRK", "GNB"];
    public static readonly string[] HealerJobs = ["WHM", "SCH", "AST", "SGE"];
    public static readonly string[] MeleeJobs = ["MNK", "DRG", "NIN", "SAM", "RPR", "VPR"];
    public static readonly string[] RangedJobs = ["BRD", "MCH", "DNC"];
    public static readonly string[] CasterJobs = ["BLM", "SMN", "RDM", "PCT"];

    /// <summary>DPS columns make no difference between melee, ranged and casters (a ranged may stand in a melee spot).</summary>
    public static bool IsDps(PlanRole role) => role is PlanRole.Melee or PlanRole.Ranged or PlanRole.Caster;

    /// <summary>The jobs that can play a column of this role.</summary>
    public static string[] JobsFor(PlanRole role) => role switch
    {
        PlanRole.Tank => TankJobs,
        PlanRole.Healer => HealerJobs,
        PlanRole.Melee or PlanRole.Ranged or PlanRole.Caster => [.. MeleeJobs, .. RangedJobs, .. CasterJobs],
        _ => [.. TankJobs, .. HealerJobs, .. MeleeJobs, .. RangedJobs, .. CasterJobs],
    };

    public static PlanRole RoleOfJob(string job) =>
        TankJobs.Contains(job) ? PlanRole.Tank
        : HealerJobs.Contains(job) ? PlanRole.Healer
        : MeleeJobs.Contains(job) ? PlanRole.Melee
        : RangedJobs.Contains(job) ? PlanRole.Ranged
        : CasterJobs.Contains(job) ? PlanRole.Caster : PlanRole.Any;

    /// <summary>
    /// The groups a column offers: with a job, its role actions and its own; else the role actions and every job of the
    /// role (all DPS for a DPS column).
    /// </summary>
    public static IEnumerable<Group> For(PlanRole role, string job)
    {
        if (job.Length > 0)
        {
            var jobRole = RoleOfJob(job);
            return Groups.Where(g => g.Job == job || (g.Job.Length == 0 && g.Role == jobRole));
        }

        return Groups.Where(g => role == PlanRole.Any || g.Role == role || (IsDps(role) && IsDps(g.Role)));
    }

    /// <summary>Every English name in the catalog, and the family (all its names) it belongs to.</summary>
    public static IReadOnlyDictionary<string, string[]> Families { get; } =
        Groups.SelectMany(g => g.Families)
              .SelectMany(f => f.Select(name => (name, f)))
              .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
              .ToDictionary(g => g.Key, g => g.First().f, StringComparer.OrdinalIgnoreCase);
}
