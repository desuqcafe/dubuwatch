namespace Desuwatch.App;

/// <summary>
/// How desuwatch responds when a metered connection is combined with an
/// over-budget projection. Surfaced via the banner and applied by the
/// main view model.
/// </summary>
public enum ProtectionMode
{
    /// <summary>
    /// Banner shows a "Pause background services" action button; nothing
    /// happens automatically. Default — respects the user's agency.
    /// </summary>
    Ask,

    /// <summary>
    /// When the trigger condition fires, desuwatch automatically stops
    /// allowlisted services. Banner transitions to a confirmation state.
    /// </summary>
    Auto,

    /// <summary>
    /// Banner stays passive; no action button, no auto-intervention.
    /// Effectively disables the feature while preserving the detection.
    /// </summary>
    NotifyOnly
}

/// <summary>
/// User-facing configuration for proactive hotspot protection. Lives as
/// its own class so <see cref="DesuwatchSettings"/> stays readable as
/// the settings surface grows.
/// <para>
/// The allowlist is curated: each property maps to a specific, known
/// Windows service that has been vetted as safe to pause on a metered
/// connection. No free-form service names — a user putting
/// <c>spoolsv</c> in here shouldn't be able to break printing.
/// </para>
/// </summary>
public sealed class HotspotProtectionSettings
{
    public ProtectionMode Mode { get; set; } = ProtectionMode.Ask;

    /// <summary>
    /// Windows Update service (wuauserv). The single biggest hotspot
    /// bandwidth offender. On by default.
    /// </summary>
    public bool PauseWindowsUpdate { get; set; } = true;

    /// <summary>
    /// Delivery Optimization (DoSvc). Peer-to-peer and direct download
    /// transport for Windows Update and Store downloads. Also a major
    /// offender; on by default.
    /// </summary>
    public bool PauseDeliveryOptimization { get; set; } = true;

    /// <summary>
    /// Update Orchestrator (UsoSvc). Coordinates Windows Update scans
    /// and scheduling; on its own it doesn't transfer much. Off by
    /// default — included for completeness for users who want the full
    /// sweep.
    /// </summary>
    public bool PauseUpdateOrchestrator { get; set; } = false;

    /// <summary>
    /// Background Intelligent Transfer Service (BITS). Used by many
    /// apps beyond Windows Update (AV definition updates, app installs);
    /// pausing it is broader than the others. Off by default.
    /// </summary>
    public bool PauseBackgroundTransfer { get; set; } = false;

    /// <summary>
    /// Yields the short service names the user has currently opted into.
    /// Consumed by <see cref="Desuwatch.Capture.ServiceProtectionController"/>.
    /// </summary>
    public IEnumerable<string> EnabledServices()
    {
        if (PauseWindowsUpdate) yield return "wuauserv";
        if (PauseDeliveryOptimization) yield return "DoSvc";
        if (PauseUpdateOrchestrator) yield return "UsoSvc";
        if (PauseBackgroundTransfer) yield return "BITS";
    }

    /// <summary>
    /// True if at least one service is selected. Used to hide the action
    /// button when the allowlist is empty — offering to pause nothing
    /// would be confusing.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAnyServiceSelected =>
        PauseWindowsUpdate ||
        PauseDeliveryOptimization ||
        PauseUpdateOrchestrator ||
        PauseBackgroundTransfer;

    /// <summary>
    /// True when the current configuration won't actually do anything —
    /// Ask or Auto mode is selected but the allowlist is empty, so the
    /// feature has been silently disabled. Used by the settings panel
    /// to surface a visible warning rather than let the user think
    /// protection is working when it isn't.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmptyConfigWarningVisible =>
        Mode != ProtectionMode.NotifyOnly && !HasAnyServiceSelected;
}