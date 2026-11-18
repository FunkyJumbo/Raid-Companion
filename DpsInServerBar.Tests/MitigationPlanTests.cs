using System.Collections.Generic;
using System.Linq;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class MitigationPlanTests
{
    // As Google Sheets copies cells: tab separated, a cell with a line break in quotes.
    private const string Sheet =
        "\t\tMy savage plan\t\t\n" +
        "\t\tP1: First boss\t\t\n" +
        "\t\tTank 1 (MT)\tTank 2 (OT)\tHealer 1\tHealer 2\n" +
        "\tRaidwide\tReprisal\t\tSoil\tKera\n" +
        "\t\t\tpress Soil early\t\t\n" +
        "\tTankbuster\t\"Rampart\n(on MT)\"\t\tExcog(MT)\t\n" +
        "\t\tP2: Second boss\t\t\n" +
        "\t\tTank 1 (MT)\tTank 2 (OT)\tHealer 1\tHealer 2\n" +
        "\tRaidwide\t\tReprisal\tSacred Soil\tHolos\n";

    [Fact]
    public void SheetGivesColumnsRowsPhasesAndNotes()
    {
        var plan = MitigationPlans.FromSheet(Sheet, out var error);
        Assert.NotNull(plan);
        Assert.Equal("", error);
        Assert.Equal(["Tank 1 (MT)", "Tank 2 (OT)", "Healer 1", "Healer 2"], plan!.Columns);
        Assert.Equal(["Raidwide", "Tankbuster", "Raidwide"], plan.Rows.Select(r => r.Name));
        Assert.Equal(["P1: First boss", "P1: First boss", "P2: Second boss"], plan.Rows.Select(r => r.Phase));
        Assert.Equal("press Soil early", plan.Rows[0].Note);
        Assert.Equal("Rampart\n(on MT)", plan.Rows[1].Cells[0]);
        Assert.Equal("Reprisal", MitigationPlan.Cell(plan.Rows[2], 1));
    }

    [Fact]
    public void CsvWorksToo()
    {
        var plan = MitigationPlans.FromSheet(",MT,OT,H1,H2\nRaidwide,Reprisal,,\"Soil, Fey\",Kera\n", out _);
        Assert.NotNull(plan);
        Assert.Equal("Soil, Fey", plan!.Rows[0].Cells[2]);
    }

    [Fact]
    public void NoHeaderIsAnError()
    {
        Assert.Null(MitigationPlans.FromSheet("Raidwide\tReprisal\n", out var error));
        Assert.NotEqual("", error);
    }

    private static readonly List<BossCast> Pull =
    [
        new(10, "Boss", 100, "Raidwide", 5),
        new(30, "Boss", 200, "Tankbuster", 4),
        new(60, "Boss", 100, "Raidwide", 5),
    ];

    [Fact]
    public void RowsLinkByNameInOrder()
    {
        var plan = MitigationPlans.FromSheet(Sheet, out _)!;
        var linked = MitigationPlans.LinkByName(plan, Pull, c => [c.Action]);
        Assert.Equal(3, linked);
        Assert.Equal((100u, 2), (plan.Rows[2].ActionId, plan.Rows[2].Occurrence));
        Assert.Equal(60, plan.Rows[2].AtSeconds);
    }

    [Fact]
    public void ProgressFollowsTheCastsSeen()
    {
        var plan = MitigationPlans.FromSheet(Sheet, out _)!;
        MitigationPlans.LinkByName(plan, Pull, c => [c.Action]);

        Assert.Equal(-1, MitigationPlans.Progress(plan, []).Row);
        Assert.Equal((0, 12.0), MitigationPlans.Progress(plan, [new(12, "Boss", 100, "Raidwide", 5)]));
        var seen = new List<BossCast> { new(12, "Boss", 100, "Raidwide", 5), new(33, "Boss", 200, "Tankbuster", 4), new(70, "Boss", 100, "Raidwide", 5) };
        Assert.Equal((2, 70.0), MitigationPlans.Progress(plan, seen));
    }

    // Fake game ids: one per English name.
    private static uint Id(string english) => (uint)(english.GetHashCode() & 0x7fff) + 1;

    [Theory]
    [InlineData("Tank 1 (MT)", PlanRole.Tank, "")]
    [InlineData("OT", PlanRole.Tank, "")]
    [InlineData("Scholar", PlanRole.Healer, "SCH")]
    [InlineData("White Mage", PlanRole.Healer, "WHM")]
    [InlineData("H2", PlanRole.Healer, "")]
    [InlineData("Melee 1", PlanRole.Melee, "")]
    [InlineData("Phys Range", PlanRole.Ranged, "")]
    [InlineData("R2", PlanRole.Caster, "")]
    [InlineData("Extras", PlanRole.Any, "")]
    public void ColumnsHaveARole(string column, PlanRole role, string job) =>
        Assert.Equal((role, job), MitigationCatalog.RoleOf(column));

    [Fact]
    public void CellTextGivesTheSkillsOfItsRole()
    {
        var picks = new List<PlannedAction>();
        var left = MitigationPlans.ReadSkills("Soil/Reci+Adlo/Feint/Party Mit", (PlanRole.Healer, "SCH"), false, Id, picks);
        Assert.Equal([Id("Sacred Soil"), Id("Recitation"), Id("Adloquium")], picks.Select(p => p.ActionId));
        Assert.Equal("Feint / Party Mit", left); // Feint is not a healer skill
    }

    [Fact]
    public void PrepullTimesAreRead()
    {
        // One pre-pull row per time: the skills with no time first, then the earliest.
        var plan = MitigationPlans.FromSheet(",,MT,OT,H1,H2\n,Prepull,,,Succor,ZoeEProg -8s / Kera -15s\n,Raidwide,Reprisal,,,\n", out _, Id)!;
        Assert.Equal([true, true, true, false], plan.Rows.Select(r => r.Prepull));
        Assert.Equal([null, 15, 8, null], plan.Rows.Select(r => r.PrepullSeconds));
        Assert.Equal(Id("Concitation"), MitigationPlan.Picks(plan.Rows[0], 2).Single().ActionId);
        Assert.Equal(Id("Kerachole"), MitigationPlan.Picks(plan.Rows[1], 3).Single().ActionId);
        Assert.Equal(Id("Eukrasian Prognosis II"), MitigationPlan.Picks(plan.Rows[2], 3).Single().ActionId);
        Assert.Equal(Id("Reprisal"), MitigationPlan.Picks(plan.Rows[3], 0).Single().ActionId);
    }

    [Fact]
    public void OnlyTheSkillsThatCountAreChecked()
    {
        var plan = MitigationPlans.FromSheet(",,MT,OT,H1,H2\n,Raidwide,Reprisal/Shake It Off,,,\n", out _, Id)!;
        MitigationPlans.LinkByName(plan, Pull, c => [c.Action]);
        var misses = MitigationPlans.Misses(plan, 0, Pull, [], id => [id], double.MaxValue, _ => "x", id => id == Id("Reprisal"));
        Assert.Equal([Id("Reprisal")], misses.Select(m => m.ActionId));
    }

    [Fact]
    public void MissesAreTheSkillsNotUsedInTime()
    {
        var plan = MitigationPlans.FromSheet(",,MT,OT,H1,H2\n,Prepull,Rampart -5s,,,\n,Raidwide,Reprisal,,,\n,Tankbuster,Rampart,,,\n", out _, Id)!;
        MitigationPlans.LinkByName(plan, Pull, c => [c.Action]);
        IReadOnlyCollection<uint> Family(uint id) => [id];

        // Rampart 6 s before the pull, Reprisal 5 s before the raidwide lands (10 + 5 = 15), nothing for the tankbuster.
        var uses = new List<(uint, double)> { (Id("Rampart"), -6), (Id("Reprisal"), 10) };
        var misses = MitigationPlans.Misses(plan, 0, Pull, uses, Family, double.MaxValue, _ => "x");
        Assert.Equal([("Tankbuster", Id("Rampart"))], misses.Select(m => (m.Cast, m.ActionId)));

        // The tankbuster has not landed yet at 20 s: not judged.
        Assert.Empty(MitigationPlans.Misses(plan, 0, Pull, uses, Family, 20, _ => "x"));
    }

    [Fact]
    public void ShareCodeRoundTrips()
    {
        var plan = MitigationPlans.FromSheet(Sheet, out _, Id)!;
        plan.TerritoryId = 887;
        plan.Encounter = "TEA";
        var code = MitigationPlans.Export(plan);
        Assert.StartsWith(MitigationPlans.CodePrefix, code);

        var copy = MitigationPlans.Import(code)!;
        Assert.NotEqual(plan.Id, copy.Id);
        Assert.Equal((887u, "TEA"), (copy.TerritoryId, copy.Encounter));
        Assert.Equal(plan.Rows.Count, copy.Rows.Count);
        Assert.Equal(MitigationPlan.Picks(plan.Rows[0], 2).Select(p => p.ActionId), MitigationPlan.Picks(copy.Rows[0], 2).Select(p => p.ActionId));
        Assert.Null(MitigationPlans.Import("not a code"));
    }

    private static HashSet<string> Offered(PlanRole role, string job) =>
        MitigationCatalog.For(role, job).SelectMany(g => g.Families).Select(f => f[0]).ToHashSet();

    [Fact]
    public void DpsColumnsOfferEveryDps()
    {
        var melee = Offered(PlanRole.Melee, "");
        Assert.Contains("Feint", melee);
        Assert.Contains("Addle", melee);
        Assert.Contains("Tactician", melee);
        Assert.Contains("Magick Barrier", melee);
        Assert.DoesNotContain("Rampart", melee);
    }

    [Fact]
    public void AJobOffersItsOwnSkillsOnly()
    {
        var paladin = Offered(PlanRole.Tank, "PLD");
        Assert.Contains("Rampart", paladin);
        Assert.Contains("Divine Veil", paladin);
        Assert.DoesNotContain("Shake It Off", paladin);

        var bard = Offered(PlanRole.Melee, "BRD");
        Assert.Contains("Troubadour", bard);
        Assert.Contains("Second Wind", bard);
        Assert.DoesNotContain("Feint", bard);

        Assert.Equal(13, MitigationCatalog.JobsFor(PlanRole.Caster).Length);
    }
}
