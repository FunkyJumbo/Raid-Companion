using System;
using System.Collections.Generic;
using System.Linq;
using DpsInServerBar.Opener;
using DpsInServerBar.Rdps;
using DpsInServerBar.Tracking;
using Xunit;

namespace DpsInServerBar.Tests;

public class AlBhedTests
{
    [Fact]
    public void CiphersLettersAndKeepsTheRest() =>
        Assert.Equal("Rammu, Funmt! 123", Loc.AlBhed("Hello, World! 123"));

    [Fact]
    public void KeepsFormatItemsUnlessToldOtherwise()
    {
        Assert.Equal("Bimm {0:0.0%} uv {name}", Loc.AlBhed("Pull {0:0.0%} of {name}"));
        Assert.Equal("{hysa}", Loc.AlBhed("{name}", keepFormatItems: false));
    }

    [Fact]
    public void IsABijectionOnLetters()
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        Assert.Equal(26, Loc.AlBhed(letters).Distinct().Count());
    }
}

public class TimelineTests
{
    [Fact]
    public void ResampleInterpolatesFromZeroAndHoldsTheLastValue()
    {
        var points = new List<(double T, double Value)> { (2, 200), (4, 600) };
        Assert.Equal([0, 100, 200, 400, 600, 600], Timeline.Resample(points, 5));
    }

    [Fact]
    public void ResampleOfNothingIsFlat() => Assert.Equal([0, 0, 0], Timeline.Resample([], 2.9));

    [Fact]
    public void AverageIsDamageOverElapsedTime() =>
        Assert.Equal([100f, 100f, 150f, 200f], Timeline.Average([0, 100, 300, 600]));

    [Fact]
    public void RollingLooksBackOverTheWindow() =>
        Assert.Equal([100f, 100f, 200f, 300f], Timeline.Rolling([0, 100, 300, 600], 1));

    [Fact]
    public void UptimeIsNaNWhereNothingWasAttackable()
    {
        var uptime = Timeline.Uptime([1f, 0f, 1f], [0.5f, 0f, 1f], 1);
        Assert.Equal(50f, uptime[0]);
        Assert.True(float.IsNaN(uptime[1]));
        Assert.Equal(100f, uptime[2]);
    }
}

public class OpenerCodeTests
{
    private static CustomOpener Sample() => new()
    {
        Job = "VPR",
        Name = "Test opener",
        Steps =
        [
            new CustomOpenerStep { ActionIds = [34606], Kind = StepKind.Gcd, Prepull = 1.5 },
            new CustomOpenerStep { ActionIds = [34647, 34648], Kind = StepKind.Ogcd },
            new CustomOpenerStep { Kind = StepKind.Potion },
        ],
        Notes = ["First note", "Second note"],
        Ignored = ["Sprint"],
    };

    [Fact]
    public void ACodeGivesTheSameOpenerBack()
    {
        var source = Sample();
        var imported = CustomOpeners.Import(CustomOpeners.Export(source), out var error);

        Assert.NotNull(imported);
        Assert.Equal(string.Empty, error);
        Assert.NotEqual(source.Id, imported.Id); // a new opener of yours
        Assert.Equal(source.Job, imported.Job);
        Assert.Equal(source.Name, imported.Name);
        Assert.Equal(source.Notes, imported.Notes);
        Assert.Equal(source.Ignored, imported.Ignored);
        Assert.Equal(source.Steps.Count, imported.Steps.Count);
        for (var i = 0; i < source.Steps.Count; i++)
        {
            Assert.Equal(source.Steps[i].ActionIds, imported.Steps[i].ActionIds);
            Assert.Equal(source.Steps[i].Kind, imported.Steps[i].Kind);
            Assert.Equal(source.Steps[i].Prepull, imported.Steps[i].Prepull);
        }
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("RCOPENER1:not base64 !!")]
    [InlineData("RCOPENER1:AAAA")]
    public void ABadCodeIsRefusedWithAReason(string code)
    {
        Assert.Null(CustomOpeners.Import(code, out var error));
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void ACodeCutShortIsRefused()
    {
        var code = CustomOpeners.Export(Sample());
        Assert.Null(CustomOpeners.Import(code[..(code.Length / 2)], out _));
    }
}

public class RdpsTests
{
    [Fact]
    public void AHitWithoutBuffsGivesNothingAway()
    {
        var credited = 0.0;
        RdpsMath.Allocate(new HitContext { Damage = 10_000, BaseCritRate = 0.2, BaseDirectHitRate = 0.3, Buffs = [] },
                          (_, amount) => credited += amount);
        Assert.Equal(0, credited);
    }

    [Fact]
    public void ADamageBuffIsCreditedItsShare()
    {
        var credits = new Dictionary<uint, double>();
        RdpsMath.Allocate(
            new HitContext
            {
                Damage = 10_500, BaseCritRate = 0.2, BaseDirectHitRate = 0.3,
                Buffs = [new BuffEffect(42, 1.05, 0, 0)],
            },
            (source, amount) => credits[source] = credits.GetValueOrDefault(source) + amount);

        // 10,500 with a 5% buff: 500 came from the buff.
        Assert.Equal(500, Assert.Single(credits, c => c.Key == 42).Value, 1);
    }

    [Fact]
    public void TheRateEstimatorFindsTheRate()
    {
        var estimator = new RateEstimator(0.2, 2);
        for (var i = 0; i < 1000; i++)
            estimator.Add(0, i % 4 == 0); // 25%
        Assert.Equal(0.25, estimator.Estimate(), 2);
    }
}
