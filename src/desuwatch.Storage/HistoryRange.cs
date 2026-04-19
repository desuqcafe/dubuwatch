namespace Desuwatch.App.ViewModels;

/// <summary>
/// Preset time ranges offered by the history view. Custom ranges are not
/// supported in v1 — four presets cover the cases that matter (today,
/// current cycle, last week, last month) and keep the picker UI simple.
/// </summary>
public enum HistoryRange
{
    Today,
    ThisCycle,
    Last7Days,
    Last30Days
}

/// <summary>
/// Resolves a <see cref="HistoryRange"/> to a concrete UTC window. All
/// ranges are anchored to "now" at the moment of resolution — callers
/// should re-resolve when the user navigates to a new time period.
/// </summary>
public static class HistoryRangeResolver
{
    public readonly record struct Window(DateTime FromUtc, DateTime ToUtc, string Label);

    public static Window Resolve(HistoryRange range, DateTime nowUtc, int billingCycleResetDay)
    {
        var nowLocal = nowUtc.ToLocalTime();

        return range switch
        {
            HistoryRange.Today => new Window(
                nowLocal.Date.ToUniversalTime(),
                nowUtc,
                "today"),

            HistoryRange.ThisCycle => new Window(
                ComputeCycleStartLocal(nowLocal, billingCycleResetDay).ToUniversalTime(),
                nowUtc,
                "this cycle"),

            HistoryRange.Last7Days => new Window(
                nowUtc - TimeSpan.FromDays(7),
                nowUtc,
                "last 7 days"),

            HistoryRange.Last30Days => new Window(
                nowUtc - TimeSpan.FromDays(30),
                nowUtc,
                "last 30 days"),

            _ => throw new ArgumentOutOfRangeException(nameof(range))
        };
    }

    private static DateTime ComputeCycleStartLocal(DateTime nowLocal, int resetDay)
    {
        var clamped = Math.Clamp(resetDay, 1, 28);
        var thisMonthReset = new DateTime(
            nowLocal.Year, nowLocal.Month, clamped, 0, 0, 0, DateTimeKind.Local);
        if (nowLocal >= thisMonthReset) return thisMonthReset;
        return thisMonthReset.AddMonths(-1);
    }
}