using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace DpsInServerBar.Gear;

/// <summary>The equipment slots of a gear set, in the order of the character window.</summary>
public enum GearSlot
{
    Weapon,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    RingLeft,
    RingRight,
}

/// <summary>A piece of a gear set: the item, the materia to meld into it (item ids, in slot order) and a note.</summary>
public sealed class GearPiece
{
    public uint ItemId { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<uint> Materia { get; set; } = [];

    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// The secondary stats chosen for a relic whose stats are set by the player (BaseParam row to value), empty for
    /// other items.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<uint, int> Relic { get; set; } = new();

    /// <summary>
    /// The last check of the relic you own: true when its stats were the set's, false when not, null before any (it
    /// can only be checked with the relic equipped, on the job).
    /// </summary>
    public bool? RelicChecked { get; set; }
}

/// <summary>
/// A best in slot set for a job: a piece per slot, the food, notes, and the highest item level its items may have
/// (the item lists of the editor stop there).
/// </summary>
public sealed class GearSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>English job abbreviation (e.g. "SGE").</summary>
    public string Job { get; set; } = string.Empty;

    /// <summary>The editor lists items up to this item level (0: no limit).</summary>
    public int MaxItemLevel { get; set; }

    public string Note { get; set; } = string.Empty;

    /// <summary>Where it came from (an xivgear or etro link), empty when made here.</summary>
    public string Source { get; set; } = string.Empty;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<GearSlot, GearPiece> Pieces { get; set; } = new();

    public uint Food { get; set; }
    public string FoodNote { get; set; } = string.Empty;

    public GearPiece? Piece(GearSlot slot) => Pieces.TryGetValue(slot, out var piece) && piece.ItemId != 0 ? piece : null;

    /// <summary>The piece of a slot, created empty when missing (for the editor).</summary>
    public GearPiece Edit(GearSlot slot)
    {
        if (!Pieces.TryGetValue(slot, out var piece))
            Pieces[slot] = piece = new GearPiece();
        return piece;
    }

    /// <summary>A copy with an id of its own.</summary>
    public GearSet Copy(string name)
    {
        var copy = JsonConvert.DeserializeObject<GearSet>(JsonConvert.SerializeObject(this))!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = name;
        return copy;
    }
}
