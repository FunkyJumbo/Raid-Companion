using System.Collections.Generic;
using System.Linq;
using DpsInServerBar.Native;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class PlanCompositionTests
{
    [Fact]
    public void PositionsAreNamedAfterTheirRole()
    {
        var plan = new MitigationPlan { Columns = [] };
        plan.AddColumn(PlanRole.Tank);
        plan.AddColumn(PlanRole.Tank);
        plan.AddColumn(PlanRole.Melee);
        plan.AddColumn(PlanRole.Melee);
        plan.AddColumn(PlanRole.Healer);
        plan.AddColumn(PlanRole.Any);
        Assert.Equal(["MT", "OT", "D1", "D2", "H1", "Extra"], plan.Columns);
        Assert.Equal(PlanRole.Melee, plan.RoleOf(2).Role);
        Assert.Equal(PlanRole.Any, plan.RoleOf(5).Role);
    }

    [Fact]
    public void ARoleChangeRenamesThePosition()
    {
        var plan = new MitigationPlan { Columns = [] };
        plan.AddColumn(PlanRole.Tank);
        plan.AddColumn(PlanRole.Melee);
        plan.SetRole(1, PlanRole.Tank);
        Assert.Equal(["MT", "OT"], plan.Columns);
        Assert.Equal(PlanRole.Tank, plan.RoleOf(1).Role);
    }

    [Fact]
    public void RemovingAPositionTakesItsCellsAlong()
    {
        var plan = new MitigationPlan { Columns = ["MT", "OT", "H1"] };
        var row = new MitigationRow { Cells = ["a", "b", "c"], Picks = [[], [new PlannedAction { ActionId = 7 }], []] };
        plan.Rows.Add(row);
        plan.RemoveColumn(1);
        Assert.Equal(["MT", "H1"], plan.Columns);
        Assert.Equal(["a", "c"], row.Cells);
        Assert.Equal(2, row.Picks.Count);
        Assert.Empty(row.Picks[1]);
    }

    [Fact]
    public void RowsWithoutATabGoInTheFirst()
    {
        var plan = new MitigationPlan { Tabs = ["P1", "P2"] };
        Assert.Equal("P1", plan.TabOf(new MitigationRow()));
        Assert.Equal("P2", plan.TabOf(new MitigationRow { Tab = "P2" }));
        Assert.Equal("", new MitigationPlan().TabOf(new MitigationRow { Tab = "P2" }));
    }

    [Fact]
    public void MitigationsUpWhenAnAttackLands()
    {
        var uses = new List<MitigationUse>
        {
            new(10, "A", "WAR", false, 1, "Reprisal"),  // 15 s: up at 20
            new(2, "B", "WHM", false, 2, "Temperance"), // 20 s: up at 20
            new(1, "C", "SCH", false, 3, "Sacred Soil"), // 15 s: over at 20
            new(25, "D", "PLD", false, 4, "Rampart"),   // after
        };
        var up = MitigationDurations.ActiveAt(uses, 20, u => u.Action);
        Assert.Equal(["Temperance", "Reprisal"], up.Select(u => u.Action));
    }

    [Fact]
    public void AMitigationProtectsThePartyItsUserOrItsTarget()
    {
        Assert.Null(MitigationDurations.ProtectedOne(new(0, "A", "WAR", false, 1, "Reprisal"), "Reprisal"));
        Assert.Equal("A", MitigationDurations.ProtectedOne(new(0, "A", "WAR", false, 1, "Rampart"), "Rampart"));
        Assert.Equal("B", MitigationDurations.ProtectedOne(new(0, "A", "SGE", false, 1, "Haima", "B"), "Haima"));
        Assert.Equal("", MitigationDurations.ProtectedOne(new(0, "A", "SGE", false, 1, "Haima"), "Haima"));
    }

    [Fact]
    public void HealsAreNotMitigations()
    {
        var uses = new List<MitigationUse> { new(10, "A", "WHM", true, 1, "Cure II"), new(10, "A", "WHM", true, 2, "Temperance") };
        Assert.Equal(["Temperance"], MitigationDurations.ActiveAt(uses, 12, u => u.Action).Select(u => u.Action));
    }
}
