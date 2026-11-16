using System.Collections.Generic;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class PaladinRulesTests
{
    private static (PaladinRules Rules, List<double> Mistakes) Make(int level = 100)
    {
        var rules = new PaladinRules();
        var mistakes = new List<double>();
        rules.Mistake += (at, _) => mistakes.Add(at);
        rules.Update(level, 0, 0);
        return (rules, mistakes);
    }

    [Fact]
    public void HolySpiritOnRequiescatChargeIsAMistake()
    {
        var (rules, mistakes) = Make();
        rules.Update(100, 4, 1.0);
        rules.OnAction(PaladinRules.HolySpirit, 1.1);
        rules.Update(100, 3, 1.2);
        Assert.Equal([1.1], mistakes);
    }

    [Fact]
    public void HolyCircleOnRequiescatChargeIsAMistake()
    {
        var (rules, mistakes) = Make();
        rules.Update(100, 4, 1.0);
        rules.OnAction(PaladinRules.HolyCircle, 1.1);
        rules.Update(100, 3, 1.2);
        Assert.Equal([1.1], mistakes);
    }

    [Fact]
    public void ChargeDroppedBeforeTheActionIsReported()
    {
        var (rules, mistakes) = Make();
        rules.Update(100, 4, 1.0);
        rules.Update(100, 3, 1.05); // status update came before the action
        rules.OnAction(PaladinRules.HolySpirit, 1.1);
        rules.Update(100, 3, 1.12);
        Assert.Single(mistakes);
    }

    [Fact]
    public void HolySpiritUnderDivineMightIsFine()
    {
        var (rules, mistakes) = Make();
        rules.Update(100, 4, 1.0);
        rules.OnAction(PaladinRules.HolySpirit, 1.1); // Divine Might spent, Requiescat untouched
        for (var t = 1.2; t < 3; t += 0.1)
            rules.Update(100, 4, t);
        Assert.Empty(mistakes);
    }

    [Fact]
    public void ConfiteorChargeBeforeIsNotBlamedOnHolySpirit()
    {
        var (rules, mistakes) = Make();
        rules.Update(100, 4, 1.0);
        rules.Update(100, 3, 1.1); // Confiteor
        rules.OnAction(PaladinRules.HolySpirit, 3.6);
        rules.Update(100, 3, 3.7);
        rules.Update(100, 3, 4.5);
        Assert.Empty(mistakes);
    }

    [Fact]
    public void BelowLevel80HolySpiritIsTheRightSpender()
    {
        var (rules, mistakes) = Make(79);
        rules.Update(79, 4, 1.0);
        rules.OnAction(PaladinRules.HolySpirit, 1.1);
        rules.Update(79, 3, 1.2);
        Assert.Empty(mistakes);
    }

    [Fact]
    public void BuffRunningOutWithChargesLeftIsNotASpend()
    {
        var (rules, mistakes) = Make();
        rules.Update(100, 2, 1.0);
        rules.OnAction(PaladinRules.HolySpirit, 1.1);
        rules.Update(100, 0, 1.2);
        Assert.Empty(mistakes);
    }
}
