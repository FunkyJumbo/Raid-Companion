using System;
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

        Assert.Equal(14, config.Version);
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

        config.Migrate();
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

    [Fact]
    public void PlanColumnsAreReadAsSaved()
    {
        var saved = new Configuration { Version = 13 };
        saved.MitigationPlans.Add(new Tracking.MitigationPlan { Name = "Plan", Columns = ["Tank 1 (MT)", "Scholar"] });
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(saved);

        // Read twice, as at each start of the game: no column added.
        var once = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!;
        var twice = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(Newtonsoft.Json.JsonConvert.SerializeObject(once))!;
        Assert.Equal(["Tank 1 (MT)", "Scholar"], twice.MitigationPlans.Single().Columns);
    }

    [Fact]
    public void ColumnsRepeatedByOlderReadsAreRemovedOnce()
    {
        var defaults = Tracking.MitigationPlan.DefaultColumns;
        var config = new Configuration { Version = 12 };
        config.MitigationPlans.Add(new Tracking.MitigationPlan { Columns = [.. defaults, .. defaults, .. defaults] });
        config.MitigationPlans.Add(new Tracking.MitigationPlan { Columns = [.. defaults, "Tank 1 (MT)", "Scholar"] });

        config.Migrate();

        Assert.Equal(defaults, config.MitigationPlans[0].Columns);
        Assert.Equal(["Tank 1 (MT)", "Scholar"], config.MitigationPlans[1].Columns);
        Assert.Equal(14, config.Version);
    }

    [Fact]
    public void TheGlassThemeOfATestBuildBecomesFrostWithTheGlassEffect()
    {
        var config = new Configuration { Version = 13, ThemePreset = (UI.ThemePreset)6 };

        Assert.True(config.Migrate());

        Assert.Equal(UI.ThemePreset.Frost, config.ThemePreset);
        Assert.True(config.GlassEffect);
    }

    [Fact]
    public void AThemeCodeRoundTrips()
    {
        var frost = UI.ThemePalette.For(UI.ThemePreset.Frost);
        var copy = UI.ThemePalette.Import(frost.Export("My frost"))!;
        Assert.Equal("My frost", copy.Name);
        Assert.Equal(frost.Entries().Select(e => e.Get()).Select(Round), copy.Palette.Entries().Select(e => e.Get()).Select(Round));
        Assert.Null(UI.ThemePalette.Import("not a code"));
    }

    private static System.Numerics.Vector4 Round(System.Numerics.Vector4 v) =>
        new(MathF.Round(v.X * 255), MathF.Round(v.Y * 255), MathF.Round(v.Z * 255), MathF.Round(v.W * 255));

    [Fact]
    public void ANewInstallIsMidnightGlassWithBlur12()
    {
        var config = new Configuration();
        config.Migrate();
        Assert.Equal(UI.ThemePreset.Midnight, config.ThemePreset);
        Assert.True(config.GlassEffect);
        Assert.Equal(12f, config.GlassBlur);
        Assert.Equal(1f, config.BackgroundOpacity);
        Assert.True(config.Animations);
        Assert.False(config.ThemeAmbience);
        Assert.False(config.GameAmbience);
        Assert.True(config.ViewBackdrops);
        Assert.False(config.TextOutline);
    }

    [Fact]
    public void SettingsFromBeforeTheGlassEffectKeepTheirSolidLook()
    {
        // As read from a 1.0.7.0 config: no GlassEffect saved, so the default (on) until migrated.
        var config = new Configuration { Version = 13, ThemePreset = UI.ThemePreset.Crimson };
        config.Migrate();
        Assert.False(config.GlassEffect);
    }
}
