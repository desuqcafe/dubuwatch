using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Desuwatch.Capture;

/// <summary>
/// Stops and restarts a user-configured allowlist of Windows services as
/// a proactive response to metered-connection + over-budget conditions.
/// <para>
/// Only ever touches services the user has opted into. Remembers which
/// services <em>we</em> stopped (vs. services that were already stopped
/// before we were invoked), so <see cref="ResumeProtected"/> only
/// restarts what we paused — never flips on a service the user had
/// deliberately disabled.
/// </para>
/// <para>
/// All operations are best-effort. Stopping a Windows service can fail
/// for reasons outside our control (pending operations, protected
/// dependencies); we log and move on rather than bubble exceptions,
/// because "we tried to help and partially succeeded" is more useful
/// than "we tried to help and exploded."
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceProtectionController
{
    // Timeout for waiting on a service state transition. Windows Update
    // in particular can take several seconds to actually stop when it's
    // mid-download; 10s is the documented recommendation.
    private static readonly TimeSpan StateTransitionTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly HashSet<string> _servicesWePaused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if we currently hold any services in a paused state.
    /// </summary>
    public bool IsProtectionActive
    {
        get
        {
            lock (_gate) return _servicesWePaused.Count > 0;
        }
    }

    /// <summary>
    /// Snapshot of service short-names (e.g. "wuauserv") we have paused
    /// and are responsible for resuming.
    /// </summary>
    public IReadOnlyList<string> ProtectedServices
    {
        get
        {
            lock (_gate) return _servicesWePaused.ToArray();
        }
    }

    /// <summary>
    /// Attempt to stop each service in <paramref name="serviceNames"/>.
    /// Services already stopped (by the user or another tool) are left
    /// alone and not tracked — we only resume what we actually paused.
    /// </summary>
    /// <returns>
    /// The subset of <paramref name="serviceNames"/> that were actually
    /// running before we stopped them. Callers can use this for UI
    /// feedback ("paused Windows Update, Delivery Optimization").
    /// </returns>
    public IReadOnlyList<string> PauseProtected(IEnumerable<string> serviceNames)
    {
        var stopped = new List<string>();

        foreach (var name in serviceNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (TryStopService(name))
            {
                lock (_gate) _servicesWePaused.Add(name);
                stopped.Add(name);
            }
        }

        return stopped;
    }

    /// <summary>
    /// Attempts to stop a single service, with one retry on timeout.
    /// Windows Update (wuauserv) in particular is known to return
    /// "service could not be stopped" on the first Stop call when it's
    /// mid-download; a second attempt after a short delay usually
    /// succeeds. See the handoff doc's Session 10 research notes.
    /// </summary>
    private bool TryStopService(string name)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var svc = new ServiceController(name);

                // Touching Status throws if the service doesn't exist;
                // catching below handles that cleanly.
                if (svc.Status == ServiceControllerStatus.Stopped ||
                    svc.Status == ServiceControllerStatus.StopPending)
                {
                    return false;
                }

                if (!svc.CanStop)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[desuwatch] service '{name}' does not support Stop; skipping");
                    return false;
                }

                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, StateTransitionTimeout);
                return true;
            }
            catch (System.ServiceProcess.TimeoutException) when (attempt == 1)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[desuwatch] '{name}' didn't stop in time on attempt 1; retrying");
                System.Threading.Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                // Typical cases: service doesn't exist on this SKU,
                // access denied (shouldn't happen since we're admin),
                // or the service refused to stop outright.
                System.Diagnostics.Debug.WriteLine(
                    $"[desuwatch] failed to stop '{name}': {ex.Message}");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Restart every service we previously paused. Safe to call when no
    /// services are paused (no-op). Clears the tracked set on completion
    /// regardless of per-service success — if a service refuses to
    /// restart we've done what we can; Windows will recover via its own
    /// startup triggers.
    /// </summary>
    public IReadOnlyList<string> ResumeProtected()
    {
        string[] toResume;
        lock (_gate)
        {
            if (_servicesWePaused.Count == 0) return Array.Empty<string>();
            toResume = _servicesWePaused.ToArray();
            _servicesWePaused.Clear();
        }

        var resumed = new List<string>();

        foreach (var name in toResume)
        {
            try
            {
                using var svc = new ServiceController(name);

                if (svc.Status == ServiceControllerStatus.Running ||
                    svc.Status == ServiceControllerStatus.StartPending)
                {
                    resumed.Add(name);
                    continue;
                }

                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, StateTransitionTimeout);
                resumed.Add(name);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[desuwatch] failed to resume '{name}': {ex.Message}");
            }
        }

        return resumed;
    }

    /// <summary>
    /// Human-readable label for a service short name. Used in UI strings.
    /// Falls back to the raw name when no friendly label is known.
    /// </summary>
    public static string GetDisplayName(string serviceShortName) =>
        serviceShortName.ToLowerInvariant() switch
        {
            "wuauserv" => "Windows Update",
            "dosvc"    => "Delivery Optimization",
            "usosvc"   => "Update Orchestrator",
            "bits"     => "Background Transfer",
            _          => serviceShortName
        };
}