namespace Desuwatch.App.ViewModels;

/// <summary>
/// One row in the "new this period" section of the history panel.
/// Surfaces apps whose first-seen timestamp lands inside the selected
/// range — useful for noticing new services that have quietly started
/// making network calls.
/// </summary>
public sealed class HistoryFirstSeenRow
{
	public string ProcessName { get; }
	public DateTime FirstSeenLocal { get; }

	public HistoryFirstSeenRow(string processName, DateTime firstSeenLocal)
	{
		ProcessName = processName;
		FirstSeenLocal = firstSeenLocal;
	}

	public string TimeLabel => FirstSeenLocal.ToString("MMM d · HH:mm");
}