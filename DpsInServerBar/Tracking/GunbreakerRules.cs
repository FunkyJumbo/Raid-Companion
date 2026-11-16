namespace DpsInServerBar.Tracking;

/// <summary>Gunbreaker actions, and its Gnashing Fang combo (followed apart from the basic combos, see <see cref="SideCombo"/>).</summary>
public static class GunbreakerRules
{
    public const uint JobId = 37;
    public const uint GnashingFang = 16146, SavageClaw = 16147, WickedTalon = 16150;
    public const uint JugularRip = 16156, AbdomenTear = 16157, EyeGouge = 16158, Hypervelocity = 25759, FatedBrand = 36936;
    public const uint SonicBreak = 16153, DoubleDown = 25760, ReignOfBeasts = 36937;

    // Keen Edge, Brutal Shell, Solid Barrel, Demon Slice, Demon Slaughter.
    private static readonly uint[] BasicSteps = [16137, 16139, 16145, 16141, 16149];

    /// <summary>Gnashing Fang, Savage Claw, Wicked Talon: broken by the basic combos, Sonic Break and Double Down.</summary>
    public static SideCombo GnashingFangCombo() =>
        new([GnashingFang, SavageClaw, WickedTalon], [.. BasicSteps, SonicBreak, DoubleDown]);
}
