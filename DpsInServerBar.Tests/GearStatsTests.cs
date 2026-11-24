using DpsInServerBar.Gear;
using Xunit;

namespace DpsInServerBar.Tests;

public class GearStatsTests
{
    [Theory]
    [InlineData(420, 2.50)]  // no speed: the base 2.5 s
    [InlineData(1000, 2.43)]
    [InlineData(2000, 2.31)]
    public void GlobalCooldownFollowsSpeed(int speed, double gcd) => Assert.Equal(gcd, GearStats.Gcd(speed), 2);

    [Fact]
    public void RatesAtLevel100()
    {
        Assert.Equal(5.0, GearStats.CritRate(420));
        Assert.Equal(140.0, GearStats.CritDamage(420));
        Assert.Equal(0.0, GearStats.DirectHitRate(420));
        Assert.Equal(0.0, GearStats.DeterminationBonus(440));
        Assert.Equal(25.0, GearStats.CritRate(3200));
        Assert.Equal(200, GearStats.PietyMp(440));
    }

    [Fact]
    public void MateriaCountInAnyOrder()
    {
        var piece = new GearPiece { ItemId = 1, Materia = [10, 20, 10] };
        var copy = new OwnedItem(1, GearPlace.Equipped, [20, 10]);
        Assert.Equal([true, true, false], GearInventory.Melded(piece, copy));
        Assert.Equal(2, GearInventory.MateriaDone(piece, copy));
        Assert.Equal(0, GearInventory.MateriaDone(piece, null));
    }
}
