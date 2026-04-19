namespace Desuwatch.App.ViewModels;

/// <summary>
/// One row in the history panel's "top apps" list. Static — represents
/// an app's totals over a fixed historical window, not a live process.
/// Share is precomputed as a 0–100 percentage for the visual weight bar.
/// </summary>
public sealed class HistoryAppRow
{
	public string ProcessName { get; }
	public long BytesSent { get; }
	public long BytesReceived { get; }
	public long TotalBytes { get; }
	public double SharePercent { get; }
	public DateTime? FirstSeenLocal { get; }
	public bool IsNewInRange { get; }

	public HistoryAppRow(
		string processName,
		long bytesSent,
		long bytesReceived,
		long rangeTotal,
		DateTime? firstSeenLocal,
		bool isNewInRange)
	{
		ProcessName = processName;
		BytesSent = bytesSent;
		BytesReceived = bytesReceived;
		TotalBytes = bytesSent + bytesReceived;
		SharePercent = rangeTotal > 0
			? Math.Clamp((double)TotalBytes / rangeTotal * 100, 0, 100)
			: 0;
		FirstSeenLocal = firstSeenLocal;
		IsNewInRange = isNewInRange;
	}

	public string SubtitleText
	{
		get
		{
			if (IsNewInRange && FirstSeenLocal is { } fs)
				return $"{SharePercent:0.#}% of period · new · first seen {fs:MMM d}";
			return $"{SharePercent:0.#}% of period";
		}
	}
}