namespace SloChaosLab.Slo;

/// <summary>Error-budget arithmetic, kept in one place so the runner, the rules and the write-ups agree.</summary>
public static class SloMath
{
    /// <summary>Burn rate: how many times faster than sustainable the budget is being spent. 1 spends it exactly over the period.</summary>
    public static double BurnRate(double errorRatio, double objective)
    {
        ValidateObjective(objective);
        ArgumentOutOfRangeException.ThrowIfNegative(errorRatio);
        return errorRatio / (1 - objective);
    }

    /// <summary>
    /// Fraction of the period's error budget that <paramref name="badEvents"/> consume when the service normally
    /// receives <paramref name="eventsPerSecond"/>. Experiments run minutes at a fixed rate, so the budget for the
    /// whole period is <c>(1 - objective) x rate x period</c> events. Can exceed 1 (budget exhausted).
    /// </summary>
    public static double BudgetConsumed(long badEvents, double eventsPerSecond, double objective, TimeSpan period)
    {
        ValidateObjective(objective);
        ArgumentOutOfRangeException.ThrowIfNegative(badEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventsPerSecond);
        var budgetEvents = (1 - objective) * eventsPerSecond * period.TotalSeconds;
        return badEvents / budgetEvents;
    }

    /// <summary>Budget consumed by burning at <paramref name="burnRate"/> for <paramref name="duration"/>.</summary>
    public static double BudgetConsumedByBurn(double burnRate, TimeSpan duration, TimeSpan period) =>
        burnRate * duration.TotalSeconds / period.TotalSeconds;

    /// <summary>Time until a full budget is gone at a constant burn rate; infinite when nothing is burning.</summary>
    public static TimeSpan TimeToExhaustion(double burnRate, TimeSpan period) =>
        burnRate <= 0 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(period.TotalSeconds / burnRate);

    /// <summary>Fraction of good events, or 1 when there were no eligible events (no traffic burns no budget).</summary>
    public static double Compliance(long goodEvents, long totalEvents)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(goodEvents);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalEvents, goodEvents);
        return totalEvents == 0 ? 1 : (double)goodEvents / totalEvents;
    }

    private static void ValidateObjective(double objective)
    {
        if (objective is <= 0 or >= 1 || double.IsNaN(objective))
        {
            throw new ArgumentOutOfRangeException(nameof(objective), objective, "An objective is a fraction strictly between 0 and 1.");
        }
    }
}
