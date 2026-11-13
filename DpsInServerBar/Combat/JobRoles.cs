using System;
using System.Collections.Generic;

namespace DpsInServerBar.Combat;

public enum JobRole
{
    Tank,
    Healer,
    Dps,
}

public static class JobRoles
{
    private static readonly HashSet<string> Tanks = new(StringComparer.OrdinalIgnoreCase) { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };
    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase) { "WHM", "SCH", "AST", "SGE", "CNJ" };

    /// <summary>Role of an English job abbreviation (e.g. "DRK").</summary>
    public static JobRole Of(string job)
    {
        return Tanks.Contains(job) ? JobRole.Tank : Healers.Contains(job) ? JobRole.Healer : JobRole.Dps;
    }

    public static string Label(JobRole role) => role switch
    {
        JobRole.Tank => Loc.T("tanks"),
        JobRole.Healer => Loc.T("healers"),
        _ => Loc.T("DPS"),
    };
}
