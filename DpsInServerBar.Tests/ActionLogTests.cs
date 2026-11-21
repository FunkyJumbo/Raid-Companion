using System;
using System.Linq;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class ActionLogTests
{
    [Fact]
    public void ActionsAreTimedFromThePullAndAutoAttacksLeftOut()
    {
        var log = new ActionLog();
        log.OnActionUsed(7);    // auto-attack
        log.OnActionUsed(3617); // Hard Slash
        var start = DateTime.UtcNow.AddSeconds(-1);

        var uses = log.Since(start, before: 30);
        Assert.Equal([3617u], uses.Select(u => u.ActionId));
        Assert.InRange(uses[0].AtSeconds, 0.5, 2);

        // Not before the pull when nothing before is asked for.
        Assert.Empty(log.Since(DateTime.UtcNow.AddSeconds(5)));
    }

    [Fact]
    public void ItemsAreKeptApartFromActions()
    {
        var log = new ActionLog();
        log.OnItemUsed(1_044_165); // a high quality tincture
        var use = log.Since(DateTime.UtcNow.AddSeconds(-1)).Single();
        Assert.True(use.Item);
        Assert.Equal(44165u, use.ActionId);
    }

    [Fact]
    public void ThePrepullStopsAtTheEndOfTheFightBefore()
    {
        var log = new ActionLog();
        log.OnActionUsed(3617);                         // the last action of the previous pull
        log.OnCombatEnded(DateTime.UtcNow.AddMilliseconds(1));
        System.Threading.Thread.Sleep(5);
        log.OnActionUsed(3623);                         // after it: the pre-pull of the next one
        var start = DateTime.UtcNow.AddSeconds(1);

        Assert.Equal([3623u], log.Since(start, before: 30).Select(u => u.ActionId));
    }
}
