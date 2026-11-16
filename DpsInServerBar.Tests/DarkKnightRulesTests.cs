using System.Collections.Generic;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class DarkKnightRulesTests
{
    private sealed class Log
    {
        public readonly List<(uint Action, uint Step)> Broken = [];
        public readonly List<double> Shields = [];
        public readonly List<double> Salts = [];
    }

    private static (DarkKnightRules Rules, Log Log) Make()
    {
        var rules = new DarkKnightRules();
        var log = new Log();
        rules.DeliriumBroken += (_, a, s) => log.Broken.Add((a, s));
        rules.ShieldUnbroken += log.Shields.Add;
        rules.SaltUnused += log.Salts.Add;
        rules.Update(100, false, false, false, 0);
        return (rules, log);
    }

    [Fact]
    public void BasicStepBreaksTheDeliriumCombo()
    {
        var (r, log) = Make();
        r.Update(100, true, false, false, 1);
        r.OnAction(DarkKnightRules.ScarletDelirium, 1);
        r.OnAction(3617, 3.5); // Hard Slash
        Assert.Equal([(3617u, DarkKnightRules.ScarletDelirium)], log.Broken);
    }

    [Fact]
    public void FinishedDeliriumComboIsFine()
    {
        var (r, log) = Make();
        r.Update(100, true, false, false, 1);
        r.OnAction(DarkKnightRules.ScarletDelirium, 1);
        r.OnAction(DarkKnightRules.Comeuppance, 3.5);
        r.OnAction(DarkKnightRules.Torcleaver, 6);
        r.OnAction(3617, 8.5);
        Assert.Empty(log.Broken);
    }

    [Fact]
    public void BlackestNightThatBreaksIsFine()
    {
        var (r, log) = Make();
        r.OnAction(DarkKnightRules.TheBlackestNight, 1);
        r.Update(100, false, false, false, 3);
        r.Update(100, false, true, false, 3.1); // Dark Arts
        r.Update(100, false, true, false, 10);
        Assert.Empty(log.Shields);
    }

    [Fact]
    public void BlackestNightRunningOutWholeIsCounted()
    {
        var (r, log) = Make();
        r.OnAction(DarkKnightRules.TheBlackestNight, 1);
        r.Update(100, false, false, false, 5);
        r.Update(100, false, false, false, 9);
        Assert.Equal([1.0], log.Shields);
    }

    [Fact]
    public void BlackestNightWhileDarkArtsIsUpIsNotJudged()
    {
        var (r, log) = Make();
        r.Update(100, false, true, false, 1);
        r.OnAction(DarkKnightRules.TheBlackestNight, 1);
        r.Update(100, false, true, false, 3);
        r.Update(100, false, true, false, 10);
        Assert.Empty(log.Shields);
    }

    [Fact]
    public void SaltedEarthWithoutSaltAndDarknessIsCounted()
    {
        var (r, log) = Make();
        r.OnAction(DarkKnightRules.SaltedEarth, 1);
        r.Update(100, false, false, false, 17);
        Assert.Equal([1.0], log.Salts);

        r.OnAction(DarkKnightRules.SaltedEarth, 20);
        r.OnAction(DarkKnightRules.SaltAndDarkness, 25);
        r.Update(100, false, false, false, 40);
        Assert.Single(log.Salts);
    }
}
