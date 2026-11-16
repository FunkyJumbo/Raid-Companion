using System.Collections.Generic;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class KeepUpBuffTests
{
    private static (KeepUpBuff Rule, List<uint> Missing) Make(int level = 100)
    {
        var rule = new KeepUpBuff();
        rule.SetJob(21);
        var missing = new List<uint>();
        rule.Missing += (_, action, _) => missing.Add(action);
        rule.Update(level, false);
        return (rule, missing);
    }

    [Fact]
    public void OpenerHasThreeGcdsAndTheirOgcds()
    {
        var (r, missing) = Make();
        r.OnOffensive(31, true, 0);     // Heavy Swing
        r.OnOffensive(7389, false, 0.5); // an oGCD in between
        r.OnOffensive(37, true, 2.5);   // Maim
        r.OnOffensive(45, true, 5);     // Storm's Eye
        r.Update(100, true);
        r.OnOffensive(3549, true, 7.5);
        Assert.Empty(missing);
    }

    [Fact]
    public void ActionsAfterTheGraceAreCounted()
    {
        var (r, missing) = Make();
        r.OnOffensive(31, true, 0);
        r.OnOffensive(37, true, 2.5);
        r.OnOffensive(31, true, 5);     // the third GCD is not Storm's Eye
        r.OnOffensive(16465, false, 5.5);
        Assert.Equal([31u, 16465u], missing);
    }

    [Fact]
    public void FallingOffGivesTheGraceAgain()
    {
        var (r, missing) = Make();
        r.Update(100, true);
        r.Update(100, false); // downtime: it ran out
        r.OnOffensive(31, true, 60);
        r.OnOffensive(37, true, 62.5);
        Assert.Empty(missing);
        r.OnOffensive(3549, true, 67.5); // Storm's Eye did not take: still not up
        Assert.Single(missing);
    }

    [Fact]
    public void DarksideHasOneGcdAndItsApplierIsFree()
    {
        var rule = new KeepUpBuff();
        rule.SetJob(32);
        var missing = new List<uint>();
        rule.Missing += (_, action, _) => missing.Add(action);
        rule.Update(100, false);
        rule.OnOffensive(3617, true, 0);      // Hard Slash
        rule.OnOffensive(16470, false, 0.7);  // Edge of Shadow puts it up
        rule.OnOffensive(3623, true, 2.5);    // still not shown: counted
        Assert.Equal([3623u], missing);
    }

    [Fact]
    public void EachTimeWithoutTheBuffHasItsOwnNumber()
    {
        var rule = new KeepUpBuff();
        rule.SetJob(32);
        var outages = new List<int>();
        rule.Missing += (_, _, outage) => outages.Add(outage);
        rule.Update(100, false);
        rule.OnOffensive(3617, true, 0);
        rule.OnOffensive(3623, true, 2.5);
        rule.OnOffensive(3632, true, 5);
        rule.Update(100, true);
        rule.Update(100, false);
        rule.OnOffensive(3617, true, 60);
        rule.OnOffensive(3623, true, 62.5);
        Assert.Equal(3, outages.Count);
        Assert.Equal(outages[0], outages[1]);
        Assert.NotEqual(outages[1], outages[2]);
    }

    [Fact]
    public void BelowItsLevelNothingIsCounted()
    {
        var (r, missing) = Make(45);
        for (var i = 0; i < 6; i++)
            r.OnOffensive(31, true, i * 2.5);
        Assert.Empty(missing);
    }
}
