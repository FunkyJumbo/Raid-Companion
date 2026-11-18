using System;
using System.Collections.Generic;
using System.Linq;
using DpsInServerBar.UI;

namespace DpsInServerBar.Tracking;

/// <summary>
/// Checks your mitigations against the plan you use for the fight (the active plan of the fight and your column in
/// it): the skills of your column not used in time for their cast. Keeps your recent actions, those just before the
/// pull included (for the pre-pull). Game thread only.
/// </summary>
public sealed class PlanCheck
{
    private const int MaxUses = 2000;

    private readonly Plugin plugin;
    private readonly List<(DateTime At, uint ActionId)> uses = [];
    private DateTime nextRefresh;
    private PullInProgress? livePull;
    private IReadOnlyList<PlanMiss> live = [];

    public PlanCheck(Plugin plugin) => this.plugin = plugin;

    /// <summary>One of your actions landed (from the action effect hook).</summary>
    public void OnActionUsed(uint actionId)
    {
        if (uses.Count >= MaxUses)
            uses.RemoveRange(0, MaxUses / 2);
        uses.Add((DateTime.UtcNow, actionId));
    }

    /// <summary>The plan used to check you in this fight, and your column in it (-1 when none is chosen).</summary>
    public (MitigationPlan? Plan, int Column) For(uint territory)
    {
        var config = plugin.Configuration;
        if (!config.MitigationActive.TryGetValue(territory, out var id)
            || config.MitigationPlans.FirstOrDefault(p => p.Id == id) is not { } plan)
            return (null, -1);
        return (plan, MyColumn(plan));
    }

    /// <summary>Your column in a plan: the one you chose for it, else the one named like the last you chose.</summary>
    public int MyColumn(MitigationPlan plan)
    {
        var config = plugin.Configuration;
        if (config.MitigationMyColumn.TryGetValue(plan.Id, out var column) && column < plan.Columns.Count)
            return column;
        return plan.Columns.FindIndex(c => c.Equals(config.MitigationMySlot, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The misses of the pull in progress (or of the last one), refreshed twice per second.</summary>
    public IReadOnlyList<PlanMiss> Live
    {
        get
        {
            var pull = plugin.Pulls.Current;
            if (pull is null || DateTime.UtcNow < nextRefresh)
                return live;
            if (!ReferenceEquals(pull, livePull))
            {
                livePull = pull;
                live = [];
            }

            nextRefresh = DateTime.UtcNow.AddSeconds(0.5);
            live = Compute(pull.TerritoryId, pull.StartUtc, (DateTime.UtcNow - pull.StartUtc).TotalSeconds) ?? [];
            return live;
        }
    }

    /// <summary>Whether you are checked in this fight (a plan is active and you have a column in it).</summary>
    public bool Checks(uint territory) => For(territory) is { Plan: not null, Column: >= 0 };

    /// <summary>The misses of a whole pull, at its end; null when no plan checks you in this fight.</summary>
    public List<PlanMiss>? Final(uint territory, DateTime startUtc) => Compute(territory, startUtc, double.MaxValue);

    private List<PlanMiss>? Compute(uint territory, DateTime startUtc, double until)
    {
        var (plan, column) = For(territory);
        if (plan is null || column < 0)
            return null;

        var mine = uses.Where(u => u.At >= startUtc.AddSeconds(-90))
                       .Select(u => (u.ActionId, (u.At - startUtc).TotalSeconds))
                       .ToList();
        // Only the skills of the job played in your column: the one chosen for it, else your own.
        var job = plugin.Configuration.MitigationColumnJobs.TryGetValue($"{plan.Id}:{column}", out var chosen) ? chosen : plan.RoleOf(column).Job;
        if (job.Length == 0)
            job = plugin.Tracker.Latest?.You?.Job ?? string.Empty;
        var offered = job.Length > 0 && MitigationCatalog.RoleOfJob(job) != PlanRole.Any ? PlanActions.Offered(plan.RoleOf(column).Role, job) : null;
        return MitigationPlans.Misses(plan, column, plugin.FightLog.CastsSince(startUtc), mine, PlanActions.Family, until,
                                      id => GameNames.Action(id, $"#{id}"), offered is null ? null : offered.Contains);
    }
}
