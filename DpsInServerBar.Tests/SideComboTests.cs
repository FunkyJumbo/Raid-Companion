using System.Collections.Generic;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class SideComboTests
{
    private static (SideCombo Combo, List<(uint Action, uint Step)> Broken) Make()
    {
        var combo = GunbreakerRules.GnashingFangCombo();
        var broken = new List<(uint, uint)>();
        combo.Broken += (_, action, step) => broken.Add((action, step));
        return (combo, broken);
    }

    [Theory]
    [InlineData(16137u)] // Keen Edge
    [InlineData(16141u)] // Demon Slice
    [InlineData(GunbreakerRules.SonicBreak)]
    [InlineData(GunbreakerRules.DoubleDown)]
    public void BreakersBreakGnashingFang(uint breaker)
    {
        var (c, broken) = Make();
        c.OnAction(GunbreakerRules.GnashingFang, 0);
        c.OnAction(GunbreakerRules.JugularRip, 0.7);
        c.OnAction(GunbreakerRules.SavageClaw, 2.5);
        c.OnAction(breaker, 5);
        Assert.Equal([(breaker, GunbreakerRules.SavageClaw)], broken);
    }

    [Fact]
    public void FinishedComboIsFine()
    {
        var (c, broken) = Make();
        c.OnAction(GunbreakerRules.GnashingFang, 0);
        c.OnAction(GunbreakerRules.SavageClaw, 2.5);
        c.OnAction(GunbreakerRules.WickedTalon, 5);
        c.OnAction(GunbreakerRules.SonicBreak, 7.5);
        Assert.Empty(broken);
    }

    [Fact]
    public void TimedOutComboIsOver()
    {
        var (c, broken) = Make();
        c.OnAction(GunbreakerRules.GnashingFang, 0);
        c.OnAction(16137, 40);
        Assert.Empty(broken);
    }

    [Fact]
    public void GnashingFangBreaksReign()
    {
        var combo = GunbreakerRules.ReignCombo();
        var broken = new List<uint>();
        combo.Broken += (_, action, _) => broken.Add(action);
        combo.OnAction(GunbreakerRules.ReignOfBeasts, 0);
        combo.OnAction(GunbreakerRules.SonicBreak, 2.5); // not a breaker
        combo.OnAction(GunbreakerRules.GnashingFang, 5);
        Assert.Equal([GunbreakerRules.GnashingFang], broken);
    }

    [Fact]
    public void BasicAoeBreaksReign()
    {
        var combo = GunbreakerRules.ReignCombo();
        var broken = new List<uint>();
        combo.Broken += (_, action, _) => broken.Add(action);
        combo.OnAction(GunbreakerRules.ReignOfBeasts, 0);
        combo.OnAction(GunbreakerRules.NobleBlood, 2.5);
        combo.OnAction(16149, 5); // Demon Slaughter
        combo.OnAction(GunbreakerRules.ReignOfBeasts, 30);
        combo.OnAction(GunbreakerRules.FatedCircle, 32.5);
        Assert.Equal([16149u, GunbreakerRules.FatedCircle], broken);
    }
}
