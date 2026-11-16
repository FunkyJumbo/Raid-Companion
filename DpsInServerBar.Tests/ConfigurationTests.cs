using System.Linq;
using Xunit;

namespace DpsInServerBar.Tests;

public class ConfigurationTests
{
    private static BarElementSetting Element(BarElement element, bool enabled) => new() { Element = element, Enabled = enabled };

    [Fact]
    public void TheRoleRankElementBecomesAModeOfRank()
    {
        var config = new Configuration
        {
            Version = 11,
            BarLayout = [Element(BarElement.Rank, false), Element(BarElement.RoleGap, true), Element(BarElement.Value, true)],
            BarPresets =
            [
                new BarPreset { Name = "Role", Layout = [Element(BarElement.RoleGap, true), Element(BarElement.Rank, false)] },
                new BarPreset { Name = "Party", Layout = [Element(BarElement.Rank, true), Element(BarElement.RoleGap, false)] },
            ],
        };

        Assert.True(config.Migrate());

        Assert.Equal(12, config.Version);
        Assert.True(config.RankInRole);
        Assert.True(config.IsShown(BarElement.Rank));
        Assert.DoesNotContain(config.BarLayout, e => e.Element == BarElement.RoleGap);

        Assert.True(config.BarPresets[0].RankInRole);
        Assert.True(config.BarPresets[0].Layout.Single(e => e.Element == BarElement.Rank).Enabled);
        Assert.False(config.BarPresets[1].RankInRole);
        Assert.All(config.BarPresets, p => Assert.DoesNotContain(p.Layout, e => e.Element == BarElement.RoleGap));
    }

    [Fact]
    public void ARankInThePartyStaysAsItWas()
    {
        var config = new Configuration
        {
            Version = 11,
            BarLayout = [Element(BarElement.Rank, true), Element(BarElement.RoleGap, false), Element(BarElement.Value, true)],
        };

        config.Migrate();

        Assert.False(config.RankInRole);
        Assert.True(config.IsShown(BarElement.Rank));
    }

    [Fact]
    public void AnUpToDateConfigurationIsLeftAlone()
    {
        var config = new Configuration();
        config.Migrate();
        config.RankInRole = true;

        Assert.False(config.Migrate());
        Assert.True(config.RankInRole);
    }

    [Fact]
    public void APresetCarriesTheRankMode()
    {
        var config = new Configuration { RankInRole = true };
        var preset = new BarPreset();
        preset.CaptureFrom(config);
        Assert.True(preset.Matches(config));

        config.RankInRole = false;
        Assert.False(preset.Matches(config));

        preset.ApplyTo(config);
        Assert.True(config.RankInRole);
    }
}
