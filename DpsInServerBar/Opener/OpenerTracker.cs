using System;
using DpsInServerBar.Native;

namespace DpsInServerBar.Opener;

/// <summary>
/// Reports the actions you use (as confirmed by the server), only while enabled, i.e. while the opener practice
/// window is open or the editor records. Fed by the built-in tracker's action effect hook.
/// </summary>
public sealed class OpenerTracker : IDisposable
{
    private readonly NativeCombatTracker tracker;

    public OpenerTracker(NativeCombatTracker tracker)
    {
        this.tracker = tracker;
        tracker.LocalEffect += OnLocalEffect;
    }

    /// <summary>(action type: 1 = action, 2 = item; action or item id). Raised on the game thread.</summary>
    public event Action<byte, uint>? ActionUsed;

    /// <summary>False when the game hook could not be created.</summary>
    public bool Available => tracker.IsReady;

    public bool Enabled { get; set; }

    public void Dispose() => tracker.LocalEffect -= OnLocalEffect;

    private void OnLocalEffect(byte type, uint id)
    {
        if (!Enabled || type is not (1 or 2) || id is 7 or 8) // 7 / 8: auto-attacks
            return;

        try
        {
            ActionUsed?.Invoke(type, id);
        }
        catch (Exception ex)
        {
            // Never let the opener practice break the damage meter, which shares the hook.
            Plugin.Log.Warning($"Opener practice: {ex.Message}");
        }
    }
}
