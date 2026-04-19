namespace Desuwatch.App.ViewModels;

/// <summary>
/// Severity tier for an in-flight cycle budget projection. Drives both
/// the cycle card subtitle tone and whether the hotspot banner escalates.
/// </summary>
public enum BudgetSeverity
{
    /// <summary>No cap configured — projection is not meaningful.</summary>
    Unknown,

    /// <summary>Projected &lt;= 85% of cap at cycle end.</summary>
    OnTrack,

    /// <summary>Projected 85–100% of cap. Subtle hint, no banner.</summary>
    Watch,

    /// <summary>Projected 100–125% of cap. Banner lights up.</summary>
    Warning,

    /// <summary>Projected &gt;125% or already over the cap.</summary>
    OverBudget
}

/// <summary>
/// Result of projecting current cycle usage forward to the end of the
/// billing cycle. All byte fields are absolute; pct is 0–1+ (1.0 = cap).
/// <para>
/// <see cref="EtaLocal"/> is the moment the running projection crosses
/// the cap; null if the projection stays below the cap for the whole
/// cycle.
/// </para>
/// </summary>
public readonly record struct BudgetProjection(
    BudgetSeverity Severity,
    long ProjectedBytes,
    double ProjectedFractionOfCap,
    DateTime? EtaLocal,
    long BytesPerSecondRate);

/// <summary>
/// Projects cycle-end usage from recent throughput, and classifies the
/// result into a severity tier. Pure — no state, no I/O.
/// <para>
/// v1 model is a straight linear extrapolation: <c>projected = used +
/// rate * secondsRemaining</c>. This is deliberately simple. It reacts
/// quickly to sustained changes in behavior (leaving the hotspot, or
/// starting a big download) and doesn't pretend to model diurnal or
/// weekly patterns it can't actually predict from the data we have.
/// </para>
/// </summary>
public static class BudgetProjector
{
    // Severity thresholds as fractions of the cap at projected cycle end.
    private const double WatchFraction = 0.85;
    private const double WarningFraction = 1.00;
    private const double OverBudgetFraction = 1.25;

    public static BudgetProjection Project(
        long bytesUsedThisCycle,
        long monthlyCapBytes,
        DateTime cycleStartLocal,
        DateTime cycleEndLocal,
        DateTime nowLocal,
        long bytesPerSecondRate)
    {
        if (monthlyCapBytes <= 0)
        {
            return new BudgetProjection(
                BudgetSeverity.Unknown, bytesUsedThisCycle, 0, null, bytesPerSecondRate);
        }

        // Already over the cap — projection is moot, classify and bail.
        if (bytesUsedThisCycle >= monthlyCapBytes)
        {
            var overFraction = (double)bytesUsedThisCycle / monthlyCapBytes;
            return new BudgetProjection(
                BudgetSeverity.OverBudget, bytesUsedThisCycle, overFraction, nowLocal, bytesPerSecondRate);
        }

        var secondsRemaining = Math.Max(0, (cycleEndLocal - nowLocal).TotalSeconds);
        var projectedBytes = bytesUsedThisCycle + (long)(bytesPerSecondRate * secondsRemaining);
        var projectedFraction = (double)projectedBytes / monthlyCapBytes;

        DateTime? eta = null;
        if (bytesPerSecondRate > 0 && projectedBytes > monthlyCapBytes)
        {
            var bytesUntilCap = monthlyCapBytes - bytesUsedThisCycle;
            var secondsUntilCap = (double)bytesUntilCap / bytesPerSecondRate;
            eta = nowLocal.AddSeconds(secondsUntilCap);

            // Clamp ETA to the cycle window. A rate that projects past
            // the cap at some absurd future date (> cycle end) means
            // severity should be Watch/OnTrack, not ETA-in-2027.
            if (eta > cycleEndLocal) eta = null;
        }

        var severity = projectedFraction switch
        {
            >= OverBudgetFraction => BudgetSeverity.OverBudget,
            >= WarningFraction => BudgetSeverity.Warning,
            >= WatchFraction => BudgetSeverity.Watch,
            _ => BudgetSeverity.OnTrack
        };

        return new BudgetProjection(
            severity, projectedBytes, projectedFraction, eta, bytesPerSecondRate);
    }

    /// <summary>
    /// Formats an ETA as a short, friendly string for UI display.
    /// "today 3pm", "tomorrow", "Thu Apr 23", etc.
    /// </summary>
    public static string FormatEta(DateTime etaLocal, DateTime nowLocal)
    {
        var etaDate = etaLocal.Date;
        var today = nowLocal.Date;
        var delta = (etaDate - today).Days;

        return delta switch
        {
            0 => $"today {etaLocal:h tt}".ToLowerInvariant(),
            1 => "tomorrow",
            < 7 => etaLocal.ToString("ddd"),
            _ => etaLocal.ToString("ddd MMM d")
        };
    }
}