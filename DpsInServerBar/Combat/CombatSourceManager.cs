using System;
using DpsInServerBar.Native;

namespace DpsInServerBar.Combat;

/// <summary>Owns the built-in tracker, the only data source.</summary>
public sealed class CombatSourceManager : IDisposable
{
    public CombatSourceManager(NativeCombatTracker native) => Native = native;

    public NativeCombatTracker Native { get; }

    public ICombatSource Current => Native;

    /// <summary>Latest snapshot of the built-in tracker.</summary>
    public CombatSnapshot? Latest => Native.Latest;

    /// <summary>Starts the built-in tracker.</summary>
    public void Apply() => Native.SetEnabled(true);

    public void Tick() => Native.Tick();

    public void Dispose() => Native.Dispose();
}
