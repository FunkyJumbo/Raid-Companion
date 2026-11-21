using System;
using System.Collections.Generic;
using System.Linq;
using DpsInServerBar.Native;

namespace DpsInServerBar.Tracking;

/// <summary>
/// How long each mitigation lasts (its effect, not its cooldown), by English name, to tell which ones were up when an
/// enemy attack landed. Typical values at the level cap; a few vary with traits or levels. Pure data.
/// </summary>
public static class MitigationDurations
{
    private static readonly Dictionary<string, double> Seconds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Role actions
        ["Rampart"] = 20, ["Reprisal"] = 15, ["Addle"] = 15, ["Feint"] = 15, ["Arm's Length"] = 6, ["Bloodbath"] = 20,
        ["Second Wind"] = 1,
        // Paladin
        ["Sentinel"] = 15, ["Guardian"] = 15, ["Hallowed Ground"] = 10, ["Sheltron"] = 8, ["Holy Sheltron"] = 8,
        ["Intervention"] = 8, ["Cover"] = 12, ["Divine Veil"] = 30, ["Passage of Arms"] = 18, ["Bulwark"] = 10,
        // Warrior
        ["Vengeance"] = 15, ["Damnation"] = 15, ["Holmgang"] = 10, ["Raw Intuition"] = 6, ["Bloodwhetting"] = 8,
        ["Nascent Flash"] = 8, ["Thrill of Battle"] = 10, ["Shake It Off"] = 30,
        // Dark Knight
        ["Shadow Wall"] = 15, ["Shadowed Vigil"] = 15, ["Living Dead"] = 10, ["The Blackest Night"] = 7, ["Dark Mind"] = 10,
        ["Dark Missionary"] = 15, ["Oblation"] = 10,
        // Gunbreaker
        ["Nebula"] = 15, ["Great Nebula"] = 15, ["Superbolide"] = 10, ["Heart of Stone"] = 7, ["Heart of Corundum"] = 8,
        ["Camouflage"] = 20, ["Heart of Light"] = 15, ["Aurora"] = 18,
        // White Mage
        ["Temperance"] = 20, ["Divine Caress"] = 10, ["Divine Benison"] = 15, ["Aquaveil"] = 8, ["Liturgy of the Bell"] = 20,
        ["Plenary Indulgence"] = 10, ["Asylum"] = 24,
        // Scholar
        ["Sacred Soil"] = 15, ["Expedient"] = 20, ["Fey Illumination"] = 20, ["Protraction"] = 10, ["Deployment Tactics"] = 30,
        ["Summon Seraph"] = 22, ["Seraphism"] = 20, ["Excogitation"] = 45,
        // Astrologian
        ["Collective Unconscious"] = 5, ["Neutral Sect"] = 20, ["Exaltation"] = 8, ["Celestial Intersection"] = 30,
        ["Macrocosmos"] = 15, ["Sun Sign"] = 15,
        // Sage
        ["Kerachole"] = 15, ["Holos"] = 20, ["Panhaima"] = 15, ["Haima"] = 15, ["Taurochole"] = 10, ["Physis II"] = 15,
        // Melee
        ["Riddle of Earth"] = 10, ["Mantra"] = 15, ["Shade Shift"] = 20, ["Third Eye"] = 4, ["Tengentsu"] = 4, ["Arcane Crest"] = 5,
        // Physical ranged
        ["Troubadour"] = 15, ["Nature's Minne"] = 15, ["Tactician"] = 15, ["Dismantle"] = 10, ["Shield Samba"] = 15,
        ["Improvisation"] = 15,
        // Casters
        ["Manaward"] = 20, ["Radiant Aegis"] = 30, ["Magick Barrier"] = 10, ["Tempera Coat"] = 10, ["Tempera Grassa"] = 10,
    };

    // On their user only.
    private static readonly HashSet<string> OnSelf = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rampart", "Arm's Length", "Bloodbath", "Second Wind", "Sentinel", "Guardian", "Hallowed Ground", "Sheltron", "Holy Sheltron",
        "Bulwark", "Vengeance", "Damnation", "Holmgang", "Raw Intuition", "Bloodwhetting", "Thrill of Battle", "Shadow Wall",
        "Shadowed Vigil", "Living Dead", "Dark Mind", "Nebula", "Great Nebula", "Superbolide", "Camouflage", "Riddle of Earth",
        "Shade Shift", "Third Eye", "Tengentsu", "Arcane Crest", "Manaward", "Radiant Aegis", "Tempera Coat",
    };

    // On one party member, chosen by its user.
    private static readonly HashSet<string> OnOne = new(StringComparer.OrdinalIgnoreCase)
    {
        "Intervention", "Cover", "Nascent Flash", "The Blackest Night", "Oblation", "Heart of Stone", "Heart of Corundum", "Aurora",
        "Divine Benison", "Aquaveil", "Protraction", "Excogitation", "Exaltation", "Celestial Intersection", "Haima", "Taurochole",
    };

    /// <summary>Whether a mitigation is known (one with a duration here, not a heal).</summary>
    public static bool IsKnown(string englishName) => Seconds.ContainsKey(englishName);

    /// <summary>
    /// The party member a use protected alone: its user for a buff of their own, its target for one put on someone,
    /// null for one on the whole party (or the enemy), or when its target is not known (older pulls).
    /// </summary>
    public static string? ProtectedOne(MitigationUse use, string englishName) =>
        OnSelf.Contains(englishName) ? use.Player
        : OnOne.Contains(englishName) ? (use.Target.Length > 0 ? use.Target : string.Empty)
        : null;

    /// <summary>How long a mitigation lasts, by its English name; 10 s when unknown.</summary>
    public static double Of(string englishName) => Seconds.GetValueOrDefault(englishName, 10);

    /// <summary>
    /// The mitigations up at a moment: used at most their duration before it (and not after it).
    /// </summary>
    /// <param name="englishName">The English name of a use's action (its duration).</param>
    public static List<MitigationUse> ActiveAt(IEnumerable<MitigationUse> uses, double at, Func<MitigationUse, string> englishName) =>
        uses.Where(u => IsKnown(englishName(u)) && u.AtSeconds <= at && at - u.AtSeconds <= Of(englishName(u)))
            .OrderBy(u => u.AtSeconds)
            .ToList();
}
