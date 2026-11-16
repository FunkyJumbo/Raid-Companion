using System;

namespace DpsInServerBar.Tracking;

/// <summary>
/// A combo the game keeps apart from the basic 1-2-3 (its steps do not break the basic one, and are not in the
/// combo data the main tracker reads), and that some actions break before it is finished: Dark Knight Delirium
/// (Scarlet Delirium, Comeuppance, Torcleaver), Gunbreaker Gnashing Fang (Gnashing Fang, Savage Claw, Wicked Talon).
/// Pure logic, no game access.
/// </summary>
public sealed class SideCombo
{
    private readonly uint[] steps;
    private readonly uint[] breakers;
    private readonly double timeout;
    private uint waiting; // last step done, 0 when none is waiting for the next
    private double doneAt;

    /// <param name="steps">The steps, in order.</param>
    /// <param name="breakers">Actions that break it when used before its last step.</param>
    /// <param name="timeout">Seconds after a step when the combo is over anyway (its timer).</param>
    public SideCombo(uint[] steps, uint[] breakers, double timeout = 30)
    {
        this.steps = steps;
        this.breakers = breakers;
        this.timeout = timeout;
    }

    /// <summary>A breaker used before the end: time, the breaker, the step left waiting for the next.</summary>
    public event Action<double, uint, uint>? Broken;

    /// <summary>One of your actions landed.</summary>
    public void OnAction(uint actionId, double now)
    {
        if (waiting != 0 && now - doneAt > timeout)
            waiting = 0;

        var step = Array.IndexOf(steps, actionId);
        if (step >= 0)
        {
            waiting = step < steps.Length - 1 ? actionId : 0;
            doneAt = now;
            return;
        }

        if (waiting != 0 && Array.IndexOf(breakers, actionId) >= 0)
        {
            Broken?.Invoke(now, actionId, waiting);
            waiting = 0;
        }
    }

    /// <summary>The combo is over (its buff gone, a new fight, a death).</summary>
    public void Cancel() => waiting = 0;
}
