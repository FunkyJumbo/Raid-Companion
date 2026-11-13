namespace DpsInServerBar.Combat;

/// <summary>Something that produces <see cref="CombatSnapshot"/>s (the built-in tracker).</summary>
public interface ICombatSource
{
    string Name { get; }

    /// <summary>True when the source is able to produce data right now.</summary>
    bool IsReady { get; }

    /// <summary>Short human readable status, for tooltips and the main window.</summary>
    string StatusText { get; }

    CombatSnapshot? Latest { get; }
}
