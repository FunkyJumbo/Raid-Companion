using System.Text.Json.Serialization;
using DpsInServerBar.UI;

namespace DpsInServerBar.Tracking;

/// <summary>
/// A failed mechanic: an enemy action put a penalty debuff (Vulnerability Up, Damage Down...) on a party member.
/// <see cref="AtSeconds"/> counts from the start of combat.
/// </summary>
public sealed record MechanicMistake(
    double AtSeconds, string Player, string Job, bool IsYou, string Status, string Ability, string Source, uint StatusId = 0,
    uint AbilityId = 0)
{
    /// <summary><see cref="Status"/> in the plugin language (as saved for older pulls).</summary>
    [JsonIgnore] public string StatusText => GameNames.Status(StatusId, Status);

    /// <summary><see cref="Ability"/> in the plugin language (as saved for older pulls).</summary>
    [JsonIgnore] public string AbilityText => GameNames.Action(AbilityId, Ability);
}
