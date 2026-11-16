using System.Collections.Generic;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class BuffExpiryTests
{
    private const ushort GoringReady = 3847;

    private static (BuffExpiry Tracker, List<(ushort Status, int Left)> Expired) Make()
    {
        var tracker = new BuffExpiry();
        tracker.SetJob(PaladinRules.JobId);
        var expired = new List<(ushort, int)>();
        tracker.Expired += (_, buff, left, _) => expired.Add((buff.StatusId, left));
        return (tracker, expired);
    }

    private static (ushort, float, int)[] On(ushort id, float remaining, int param = 0) => [(id, remaining, param)];

    [Fact]
    public void BuffRunningOutIsCounted()
    {
        var (t, expired) = Make();
        t.Update(On(GoringReady, 5), false, 1);
        t.Update(On(GoringReady, 0.1f), false, 5.9);
        t.Update([], false, 6.0);
        Assert.Equal([(GoringReady, 0)], expired);
    }

    [Fact]
    public void BuffSpentIsNotCounted()
    {
        var (t, expired) = Make();
        t.Update(On(GoringReady, 20), false, 1);
        t.OnAction(PaladinRules.GoringBlade, 1.05);
        t.Update([], false, 1.1);
        Assert.Empty(expired);
    }

    [Fact]
    public void BuffSpentInItsLastSecondIsNotCounted()
    {
        var (t, expired) = Make();
        t.Update(On(GoringReady, 0.5f), false, 1);
        t.OnAction(PaladinRules.GoringBlade, 1.05);
        t.Update([], false, 1.1);
        Assert.Empty(expired);
    }

    [Fact]
    public void DeathIsNoFault()
    {
        var (t, expired) = Make();
        t.Update(On(GoringReady, 0.5f), false, 1);
        t.Update([], true, 1.1);
        Assert.Empty(expired);
    }

    [Fact]
    public void RequiescatRunningOutCountsItsChargesLeft()
    {
        var (t, expired) = Make();
        t.Update(On(PaladinRules.RequiescatStatus, 0.2f, 2), false, 1);
        t.Update([], false, 1.1);
        Assert.Equal([(PaladinRules.RequiescatStatus, 2)], expired);
    }

    [Fact]
    public void RequiescatLastChargeSpentIsFine()
    {
        var (t, expired) = Make();
        t.Update(On(PaladinRules.RequiescatStatus, 0.4f, 1), false, 1);
        t.OnAction(PaladinRules.BladeOfValor, 1.05);
        t.Update([], false, 1.1);
        Assert.Empty(expired);
    }

    [Fact]
    public void BerserkSpentByAnyWeaponskill()
    {
        var expired = new List<int>();
        var t = new BuffExpiry();
        t.SetJob(21);
        t.Expired += (_, _, left, _) => expired.Add(left);
        t.Update([(86, 0.3f, 1)], false, 1);
        t.OnAction(31, 1.05); // Heavy Swing
        t.Update([], false, 1.1);
        Assert.Empty(expired);
        Assert.All(BuffExpiry.Jobs[21], b => Assert.NotNull(b.Consumers));
    }

    [Fact]
    public void ContinuationReplacedByTheNextGcdIsLost()
    {
        var lost = new List<bool>();
        var t = new BuffExpiry();
        t.SetJob(GunbreakerRules.JobId);
        t.Expired += (_, _, _, early) => lost.Add(early);
        t.Update([(1842, 9f, 0)], false, 1);   // Ready to Rip
        t.OnAction(GunbreakerRules.SavageClaw, 3.4);
        t.Update([(1843, 10f, 0)], false, 3.5); // Ready to Tear replaced it
        Assert.Equal([true], lost);
    }

    [Fact]
    public void ContinuationUsedIsFine()
    {
        var lost = new List<bool>();
        var t = new BuffExpiry();
        t.SetJob(GunbreakerRules.JobId);
        t.Expired += (_, _, _, early) => lost.Add(early);
        t.Update([(1842, 9f, 0)], false, 1);
        t.OnAction(GunbreakerRules.JugularRip, 1.6);
        t.Update([], false, 1.7);
        Assert.Empty(lost);
    }

    [Fact]
    public void OtherJobsFollowNothing()
    {
        var tracker = new BuffExpiry();
        tracker.SetJob(24); // White Mage
        Assert.False(tracker.Active);
    }
}
