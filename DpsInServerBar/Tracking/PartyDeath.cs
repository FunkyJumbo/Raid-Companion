using System.Collections.Generic;
using System.Text.Json.Serialization;
using DpsInServerBar.UI;

namespace DpsInServerBar.Tracking;

/// <summary>One hit taken shortly before a death.</summary>
/// <param name="SecondsBefore">How long before the death it landed.</param>
/// <param name="AbilityKey">Key of <paramref name="Ability"/> to show it in the plugin language, empty for older pulls.</param>
public sealed record DeathHit(double SecondsBefore, string Ability, string Source, long Damage, string AbilityKey = "")
{
    [JsonIgnore] public string AbilityText => GameNames.Resolve(AbilityKey, Ability);
}

/// <summary>
/// A party member's death and what caused it. <see cref="AtSeconds"/> counts from the start of the pull.
/// </summary>
/// <param name="Cause">The ability that took the last HP, or a guess ("falling off the arena", "Doom (debuff)").</param>
/// <param name="Damage">Damage of that ability, 0 when there was no lethal hit.</param>
/// <param name="Recap">The last hits taken before the death, oldest first.</param>
public sealed record PartyDeath(
    double AtSeconds, string Player, string Job, bool IsYou, string Cause, string Source, long Damage, uint HpBefore,
    List<DeathHit> Recap, string CauseKey = "")
{
    /// <summary><see cref="Cause"/> in the plugin language (as saved for older pulls).</summary>
    [JsonIgnore] public string CauseText => GameNames.Resolve(CauseKey, Cause);
}
