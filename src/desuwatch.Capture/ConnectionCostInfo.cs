namespace Desuwatch.Capture;

/// <summary>
/// Immutable snapshot of the current internet connection's cost characteristics
/// as reported by Windows. Drives hotspot-aware behavior in the UI.
/// </summary>
public readonly record struct ConnectionCostInfo(
	CostLevel Level,
	bool IsRoaming,
	bool IsOverDataLimit,
	bool ApproachingDataLimit,
	string? ProfileName)
{
	/// <summary>True when the user should treat this connection as expensive.</summary>
	public bool IsMetered => Level != CostLevel.Unrestricted && Level != CostLevel.Unknown;

	public static ConnectionCostInfo Unknown { get; } =
		new(CostLevel.Unknown, false, false, false, null);
}

public enum CostLevel
{
	Unknown = 0,
	Unrestricted,
	/// <summary>Capped plan with a known monthly allowance (e.g. 20GB wifi).</summary>
	Fixed,
	/// <summary>Pay-per-byte (e.g. cellular, roaming).</summary>
	Variable
}