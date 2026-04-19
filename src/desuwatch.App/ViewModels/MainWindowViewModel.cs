using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Desuwatch.Capture;
using Desuwatch.App;
using Desuwatch.Storage;
using CommunityToolkit.Mvvm.Input;

namespace Desuwatch.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
	private readonly INetworkCaptureEngine _engine;
	private readonly ConnectionCostMonitor _costMonitor;
	private readonly DnsResolver _dnsResolver;
	private readonly HistoryStore _historyStore;
	private readonly DnsCacheStore _dnsCacheStore;
	private readonly DesuwatchSettings _settings;
	private readonly ServiceProtectionController _protection = new();
	private readonly Dictionary<int, AppUsageViewModel> _byPid = new();
	private readonly DispatcherTimer _sparklineTimer;
	private readonly AggregateSparklineBuffer _heartbeatBuffer = new(60);
	private const double HeartbeatWidth = 560;
	private const double HeartbeatHeight = 32;

	[ObservableProperty] private string _statusText = "Starting capture...";
	[ObservableProperty] private string _statusPillText = "starting";
	[ObservableProperty] private bool _isCapturing;
	[ObservableProperty] private Points _heartbeatPoints = new();
	[ObservableProperty] private bool _isSettingsOpen;
	[ObservableProperty] private bool _isHistoryOpen;
	[ObservableProperty] private string _cycleCapSubtitle = "";
	[ObservableProperty] private long _totalBytesSent;
	[ObservableProperty] private long _totalBytesReceived;
	[ObservableProperty] private long _todayTotalBytes;
	[ObservableProperty] private long _cycleTotalBytes;
	[ObservableProperty] private string _cycleStartDisplay = "";
	[ObservableProperty] private long _currentBytesPerSecondSent;
	[ObservableProperty] private long _currentBytesPerSecondReceived;
	[ObservableProperty] private int _activeAppCount;
	[ObservableProperty] private ConnectionCostInfo _connectionCost = ConnectionCostInfo.Unknown;
	[ObservableProperty] private string _hotspotBannerText = "";
	[ObservableProperty] private bool _showHotspotBanner;
	[ObservableProperty] private bool _isBannerCritical;
	[ObservableProperty] private bool _isProtectionActive;
	[ObservableProperty] private string _protectionStatusText = "";
	[ObservableProperty] private bool _canOfferProtection;
	[ObservableProperty] private bool _isProtectionBannerState;

	// Rolling 24h bytes-per-second rate used by the budget projector.
	// Refreshed on minute-bucket rollover alongside the period totals.
	private long _rollingRateBytesPerSecond;
	private BudgetProjection _lastProjection;

	// Cached DB portion of the "today" and "cycle" totals. Re-queried only
	// when the minute-bucket flips; the live counters are layered on top
	// each tick for smooth updates without hammering SQLite.
	private long _todayDbBytes;
	private long _cycleDbBytes;
	private long _lastQueriedMinuteBucket = -1;
	private DateTime _lastQueriedLocalDay = DateTime.MinValue;
	private DateTime _lastQueriedCycleStartLocal = DateTime.MinValue;

	public ObservableCollection<AppUsageViewModel> Apps { get; } = new();

	/// <summary>
	/// Exposed so views can read/mutate-and-save settings (e.g. the
	/// close-confirmation dialog's "Don't ask again" flow).
	/// </summary>
	public DesuwatchSettings Settings => _settings;

	/// <summary>
	/// Child VM for the history panel overlay. Instantiated eagerly
	/// alongside the main VM because its queries are cheap (DB-indexed)
	/// and keeping a single instance lets the panel remember the user's
	/// last-selected range across open/close cycles.
	/// </summary>
	public HistoryViewModel History { get; }

	public MainWindowViewModel()
		: this(BuildDefaults())
	{
	}

	private static Dependencies BuildDefaults()
	{
		var settings = DesuwatchSettings.LoadOrCreate();
		var dnsCacheStore = new DnsCacheStore(
			settings.DnsCacheDbPath, settings.FlushInterval, settings.DnsPositiveRetention);
		return new Dependencies(
			settings,
			new EtwNetworkCaptureEngine(),
			new ConnectionCostMonitor(),
			new DnsResolver(dnsCacheStore),
			dnsCacheStore,
			new HistoryStore(settings.HistoryDbPath, settings.FlushInterval, settings.Retention));
	}

	internal record Dependencies(
		DesuwatchSettings Settings,
		INetworkCaptureEngine Engine,
		ConnectionCostMonitor CostMonitor,
		DnsResolver DnsResolver,
		DnsCacheStore DnsCacheStore,
		HistoryStore HistoryStore);

	internal MainWindowViewModel(Dependencies deps)
	{
		_settings = deps.Settings;
		_engine = deps.Engine;
		_costMonitor = deps.CostMonitor;
		_dnsResolver = deps.DnsResolver;
		_dnsCacheStore = deps.DnsCacheStore;
		_historyStore = deps.HistoryStore;

		_engine.EventObserved += OnEventObserved;
		_costMonitor.CostChanged += OnCostChanged;
		_dnsResolver.HostResolved += OnHostResolved;
		_dnsCacheStore.Start();
		_dnsResolver.Start();
		_historyStore.Start();

		try
		{
			_engine.Start();
			StatusText = "Capturing (kernel ETW)";
			StatusPillText = "capturing · kernel ETW";
			IsCapturing = true;
		}
		catch (UnauthorizedAccessException ex)
		{
			StatusText = "Needs admin: " + ex.Message;
			StatusPillText = "needs admin";
			IsCapturing = false;
		}
		catch (Exception ex)
		{
			StatusText = "Capture failed: " + ex.Message;
			StatusPillText = "capture failed";
			IsCapturing = false;
		}

		_costMonitor.Start();
		ApplyCost(_costMonitor.Current);

		_sparklineTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1)
		};
		_sparklineTimer.Tick += OnSparklineTick;
		_sparklineTimer.Start();

		History = new HistoryViewModel(_historyStore, _settings);
	}

	private void OnSparklineTick(object? sender, EventArgs e)
	{
#if DEBUG
		var tickStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
		var now = DateTime.UtcNow;

		long tickSent = 0, tickReceived = 0, active = 0;
		foreach (var app in Apps)
		{
			app.Tick();
			PersistAppTick(now, app);

			tickSent += app.LastTickSent;
			tickReceived += app.LastTickReceived;
			if (app.IsRecentlyActive) active++;
		}

		CurrentBytesPerSecondSent = tickSent;
		CurrentBytesPerSecondReceived = tickReceived;
		ActiveAppCount = (int)active;

		// Feed the heartbeat chart with aggregate throughput this tick.
		// Rebuild the Points collection fresh every tick — Avalonia's
		// Polyline does not observe mutations to an existing instance.
		_heartbeatBuffer.Push(tickSent + tickReceived);
		HeartbeatPoints = _heartbeatBuffer.BuildPoints(HeartbeatWidth, HeartbeatHeight);

		ReorderApps();
		MarkMostActive();
		RefreshPeriodTotals(now);

#if DEBUG
		tickStopwatch.Stop();
		// Log only when tick duration exceeds a threshold that would
		// actually be user-visible. 50ms = 5% of our 1s tick budget;
		// anything below that is firmly in the "don't worry about it"
		// zone. Release builds strip this entirely.
		if (tickStopwatch.ElapsedMilliseconds > 50)
		{
			System.Diagnostics.Debug.WriteLine(
				$"[desuwatch] slow tick: {tickStopwatch.ElapsedMilliseconds}ms, " +
				$"apps={Apps.Count}");
		}
#endif
	}

	private void PersistAppTick(DateTime tickUtc, AppUsageViewModel app)
	{
		if (app.LastTickSent > 0 || app.LastTickReceived > 0)
		{
			_historyStore.RecordAppDelta(
				tickUtc, app.ProcessName, app.LastTickSent, app.LastTickReceived);
		}

		if (app.LastTickAnomalous)
		{
			_historyStore.RecordAnomaly(new AnomalyRecord(
				tickUtc, app.ProcessName, HostDomain: null,
				app.LastAnomalyBytes, app.LastAnomalyMean, app.LastAnomalyStdDev));
		}

		foreach (var host in app.Hosts)
		{
			if (host.LastTickSent > 0 || host.LastTickReceived > 0)
			{
				_historyStore.RecordHostDelta(
					tickUtc, app.ProcessName, host.DisplayName,
					host.LastTickSent, host.LastTickReceived);
			}

			if (host.LastTickAnomalous)
			{
				_historyStore.RecordAnomaly(new AnomalyRecord(
					tickUtc, app.ProcessName, host.DisplayName,
					host.LastAnomalyBytes, host.LastAnomalyMean, host.LastAnomalyStdDev));
			}
		}
	}

	private void ReorderApps()
	{
		// Re-sort once per second on the tick rather than on every event.
		// Using Move (not Remove+Insert) keeps the UI diff minimal, which
		// plays nicely with row-level animations.
		for (var i = 0; i < Apps.Count - 1; i++)
		{
			var bestIdx = i;
			for (var j = i + 1; j < Apps.Count; j++)
			{
				if (CompareApps(Apps[j], Apps[bestIdx]) < 0)
					bestIdx = j;
			}

			if (bestIdx != i)
				Apps.Move(bestIdx, i);
		}
	}

	/// <summary>
	/// Marks the top row as "most active." Drives the signature 2px
	/// purple bar on the left edge of that row.
	/// <para>
	/// Uses <see cref="AppUsageViewModel.IsRecentlyActive"/> (10-tick
	/// lookback) instead of the instantaneous rate so a bursty leader
	/// (e.g. a browser doing periodic polling) doesn't strobe the bar
	/// on and off every second. Once an app qualifies, it stays the
	/// leader until another app has higher total throughput AND is
	/// itself recently active.
	/// </para>
	/// </summary>
	private void MarkMostActive()
	{
		// Apps are already sorted by total bytes descending when this
		// runs. Pick the highest-ranked row that's had traffic in the
		// last 10 seconds; that smooths over per-tick gaps from apps
		// whose traffic is spiky at the 1Hz timescale.
		AppUsageViewModel? leader = null;
		foreach (var app in Apps)
		{
			if (app.IsRecentlyActive)
			{
				leader = app;
				break;
			}
		}

		foreach (var app in Apps)
			app.IsMostActive = ReferenceEquals(app, leader);
	}

	private static int CompareApps(AppUsageViewModel a, AppUsageViewModel b)
	{
		var byBytes = b.TotalBytes.CompareTo(a.TotalBytes);
		if (byBytes != 0) return byBytes;
		return string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
	}

	private void RefreshPeriodTotals(DateTime nowUtc)
	{
		var nowLocal = nowUtc.ToLocalTime();
		var todayStartLocal = nowLocal.Date;
		var cycleStartLocal = ComputeCycleStartLocal(nowLocal, _settings.BillingCycleResetDayClamped);
		var currentBucket = ToMinuteBucket(nowUtc);

		// Re-query the DB only when a minute boundary crosses OR when the
		// day rolls over OR when the cycle boundary shifts (new month).
		var needsRequery =
			currentBucket != _lastQueriedMinuteBucket ||
			todayStartLocal != _lastQueriedLocalDay ||
			cycleStartLocal != _lastQueriedCycleStartLocal;

		if (needsRequery)
		{
			try
			{
				var todayStartUtc = todayStartLocal.ToUniversalTime();
				var cycleStartUtc = cycleStartLocal.ToUniversalTime();

				var (todaySent, todayRecv) = _historyStore.GetTotalsAcrossApps(todayStartUtc, nowUtc);
				var (cycleSent, cycleRecv) = _historyStore.GetTotalsAcrossApps(cycleStartUtc, nowUtc);

				_todayDbBytes = todaySent + todayRecv;
				_cycleDbBytes = cycleSent + cycleRecv;
				_lastQueriedMinuteBucket = currentBucket;
				_lastQueriedLocalDay = todayStartLocal;
				_lastQueriedCycleStartLocal = cycleStartLocal;
				CycleStartDisplay = $"Since {cycleStartLocal:MMM d}";

				RefreshRollingRate(nowUtc, cycleStartUtc);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[desuwatch] period totals query failed: {ex}");
			}
		}

		// Layer the live session counters on top so the display keeps
		// moving between flushes. Session counters reset at app start,
		// which is why the DB portion carries the pre-session history.
		TodayTotalBytes = _todayDbBytes + TotalBytesSent + TotalBytesReceived;
		CycleTotalBytes = _cycleDbBytes + TotalBytesSent + TotalBytesReceived;
		RefreshBudgetProjection(nowUtc, cycleStartLocal);
		RefreshCycleCapSubtitle();
		ApplyBannerMessage();
	}

	/// <summary>
	/// Recomputes the bytes-per-second rate used to project cycle-end
	/// usage. Prefers a rolling 24-hour window; falls back to the
	/// cycle-so-far average when less than 24h of history is available
	/// (i.e. during the first day of a new cycle or on a fresh install).
	/// Called only on minute-bucket rollover, so the two DB reads here
	/// cost at most ~60/hour.
	/// </summary>
	private void RefreshRollingRate(DateTime nowUtc, DateTime cycleStartUtc)
	{
		var cycleSpan = nowUtc - cycleStartUtc;
		if (cycleSpan >= TimeSpan.FromHours(24))
		{
			var windowStart = nowUtc - TimeSpan.FromHours(24);
			var (sent, recv) = _historyStore.GetTotalsAcrossApps(windowStart, nowUtc);
			_rollingRateBytesPerSecond = (sent + recv) / (long)TimeSpan.FromHours(24).TotalSeconds;
		}
		else
		{
			var seconds = Math.Max(1, (long)cycleSpan.TotalSeconds);
			_rollingRateBytesPerSecond = _cycleDbBytes / seconds;
		}
	}

	/// <summary>
	/// Runs the pure budget projector against the current cycle state.
	/// Cycle end is computed as one month past the cycle start so DST
	/// and variable month lengths stay consistent with the same math
	/// used in <see cref="ComputeCycleStartLocal"/>.
	/// </summary>
	private void RefreshBudgetProjection(DateTime nowUtc, DateTime cycleStartLocal)
	{
		if (!_settings.HasDataCap)
		{
			_lastProjection = default;
			return;
		}

		var cycleEndLocal = cycleStartLocal.AddMonths(1);
		_lastProjection = BudgetProjector.Project(
			bytesUsedThisCycle: CycleTotalBytes,
			monthlyCapBytes: _settings.MonthlyDataCapBytes,
			cycleStartLocal: cycleStartLocal,
			cycleEndLocal: cycleEndLocal,
			nowLocal: nowUtc.ToLocalTime(),
			bytesPerSecondRate: _rollingRateBytesPerSecond);
	}

	private void RefreshCycleCapSubtitle()
	{
		// Shown to the right of the "cycle · since Apr 1" label when the
		// user has configured a data cap. Reads "12.3% of 20 GB" — keeps
		// the cap visible at a glance without needing a separate widget.
		// When the projection shows an ETA inside this cycle, we append
		// "· ETA Thu" for quiet-but-present predictive context.
		if (!_settings.HasDataCap)
		{
			CycleCapSubtitle = "";
			return;
		}

		var pct = (double)CycleTotalBytes / _settings.MonthlyDataCapBytes * 100;
		var baseText = $"{pct:0.#}% of {_settings.MonthlyDataCapGb:0.#} GB";

		if (_lastProjection.EtaLocal is { } eta)
		{
			var etaText = BudgetProjector.FormatEta(eta, DateTime.Now);
			CycleCapSubtitle = $"{baseText} · ETA {etaText}";
		}
		else
		{
			CycleCapSubtitle = baseText;
		}
	}

	private static DateTime ComputeCycleStartLocal(DateTime nowLocal, int resetDay)
	{
		// If today is on or after the reset day, cycle started this month.
		// Otherwise it started on the reset day of the previous month.
		var thisMonthReset = new DateTime(nowLocal.Year, nowLocal.Month, resetDay, 0, 0, 0, DateTimeKind.Local);
		if (nowLocal >= thisMonthReset) return thisMonthReset;
		return thisMonthReset.AddMonths(-1);
	}

	private static long ToMinuteBucket(DateTime utc)
	{
		var rounded = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
		return new DateTimeOffset(rounded).ToUnixTimeSeconds();
	}

	private void OnEventObserved(object? sender, NetworkEvent e)
	{
		// Hot path: runs on ETW processing thread. Marshal to UI thread for
		// any observable-collection mutation.
		Dispatcher.UIThread.Post(() => ApplyEvent(e));
	}

	private void OnCostChanged(object? sender, ConnectionCostInfo info)
	{
		Dispatcher.UIThread.Post(() => ApplyCost(info));
	}

	private void ApplyCost(ConnectionCostInfo info)
	{
		var wasMetered = ConnectionCost.IsMetered ||
		                 ConnectionCost.IsRoaming ||
		                 ConnectionCost.IsOverDataLimit;
		ConnectionCost = info;

		// Auto-resume: if we have paused services and the connection is
		// no longer metered/roaming/over-limit, give the user their
		// services back. They went to the trouble of leaving the hotspot;
		// they don't want to remember to manually un-pause.
		var stillConstrained = info.IsMetered || info.IsRoaming || info.IsOverDataLimit;
		if (wasMetered && !stillConstrained && _protection.IsProtectionActive)
		{
			LiftProtection(reason: "cost state cleared");
		}

		ApplyBannerMessage();
	}

	/// <summary>
	/// Merges the current connection-cost state with the latest budget
	/// projection and the active protection state to produce a single
	/// banner message.
	/// <para>
	/// Priority (highest first):
	/// <list type="number">
	///   <item>Protection currently active → confirmation banner with Resume button.</item>
	///   <item>Over-limit / roaming → critical cost banner (suppresses budget).</item>
	///   <item>Metered + overage projection → merged metered/budget banner.</item>
	///   <item>Metered alone → plain metered banner.</item>
	///   <item>Budget-only (non-metered, Warning/OverBudget) → budget banner.</item>
	///   <item>Otherwise → hidden.</item>
	/// </list>
	/// Also triggers auto-mode protection when the trigger fires, and
	/// maintains <see cref="CanOfferProtection"/> for the action button.
	/// </summary>
	private void ApplyBannerMessage()
	{
		var cost = ConnectionCost;
		var projection = _lastProjection;

		// Protection confirmation state takes priority — the user needs
		// to see and be able to reverse what we did.
		if (_protection.IsProtectionActive)
		{
			var services = _protection.ProtectedServices;
			var phrase = services.Count == 1
				? ServiceProtectionController.GetDisplayName(services[0])
				: string.Join(" + ", services.Select(ServiceProtectionController.GetDisplayName));

			HotspotBannerText = $"Protected — {phrase} paused";
			IsBannerCritical = false;
			IsProtectionBannerState = true;
			ShowHotspotBanner = true;
			CanOfferProtection = false;
			return;
		}

		IsProtectionBannerState = false;

		// Short-circuit on the two most severe cost states. Over-limit
		// means the carrier has already flagged you; roaming means bytes
		// are expensive regardless of projected monthly totals.
		if (cost.IsOverDataLimit)
		{
			HotspotBannerText = "Over data limit — every byte counts";
			IsBannerCritical = true;
			ShowHotspotBanner = true;
			CanOfferProtection = ShouldOfferProtection();
			TryAutoProtect();
			return;
		}

		if (cost.IsRoaming)
		{
			HotspotBannerText = "Roaming — data may be expensive";
			IsBannerCritical = true;
			ShowHotspotBanner = true;
			CanOfferProtection = ShouldOfferProtection();
			TryAutoProtect();
			return;
		}

		var budgetPhrase = BuildBudgetPhrase(projection);
		var budgetIsCritical = projection.Severity is BudgetSeverity.Warning or BudgetSeverity.OverBudget;

		if (cost.IsMetered)
		{
			var name = string.IsNullOrEmpty(cost.ProfileName) ? "this network" : cost.ProfileName;
			HotspotBannerText = budgetPhrase is null
				? $"Metered connection ({name})"
				: $"Metered ({name}) — {budgetPhrase}";
			IsBannerCritical = cost.ApproachingDataLimit || budgetIsCritical;
			ShowHotspotBanner = true;
			CanOfferProtection = ShouldOfferProtection();
			TryAutoProtect();
			return;
		}

		// Not metered. Only surface the banner if the budget projection
		// itself warrants it — OnTrack/Watch stay quiet and live in the
		// cycle card subtitle only. Protection is hotspot-specific so
		// the action button stays hidden here.
		if (budgetPhrase is not null && budgetIsCritical)
		{
			HotspotBannerText = char.ToUpperInvariant(budgetPhrase[0]) + budgetPhrase.Substring(1);
			IsBannerCritical = projection.Severity == BudgetSeverity.OverBudget;
			ShowHotspotBanner = true;
			CanOfferProtection = false;
			return;
		}

		ShowHotspotBanner = false;
		IsBannerCritical = false;
		CanOfferProtection = false;
	}

	/// <summary>
	/// Fires <see cref="ApplyProtection"/> only when Auto mode is on AND
	/// the trigger condition currently holds. Idempotent — the controller
	/// no-ops on already-stopped services, so repeated calls during a
	/// sustained hotspot don't thrash.
	/// <para>
	/// Uses an inlined copy of the eligibility check rather than calling
	/// <see cref="ShouldOfferProtection"/> directly, because that method
	/// short-circuits on NotifyOnly mode — which is correct for the Ask
	/// button but irrelevant here since we've already gated on Auto.
	/// </para>
	/// </summary>
	private void TryAutoProtect()
	{
		if (_settings.HotspotProtection.Mode != ProtectionMode.Auto) return;
		if (!_settings.HotspotProtection.HasAnyServiceSelected) return;
		if (!IsProtectionTriggerActive()) return;
		if (_protection.IsProtectionActive) return;

		var severity = _lastProjection.Severity;
		if (severity is not (BudgetSeverity.Warning or BudgetSeverity.OverBudget)) return;

		ApplyProtection(manual: false);
	}
	
	/// <summary>
	/// Runs the protection allowlist through the controller and updates
	/// the VM state to reflect what actually got paused. Safe to call
	/// repeatedly — the controller tracks its own state and no-ops on
	/// services that are already stopped.
	/// </summary>
	private void ApplyProtection(bool manual)
	{
		if (!_settings.HotspotProtection.HasAnyServiceSelected) return;

		var stopped = _protection.PauseProtected(_settings.HotspotProtection.EnabledServices());
		RefreshProtectionState();

		// Only reshape the banner when we actually paused something,
		// otherwise the user gets a "protection active" confirmation
		// for a no-op — confusing.
		if (stopped.Count > 0 || _protection.IsProtectionActive)
			ApplyBannerMessage();

		if (manual)
		{
			System.Diagnostics.Debug.WriteLine(
				$"[desuwatch] manual protection: paused {stopped.Count} service(s)");
		}
	}

	/// <summary>
	/// Restart anything we paused. Called on cost transitions back to
	/// unrestricted (auto-resume) or via the banner's "Resume" button.
	/// </summary>
	private void LiftProtection(string reason)
	{
		if (!_protection.IsProtectionActive) return;

		var resumed = _protection.ResumeProtected();
		RefreshProtectionState();
		ApplyBannerMessage();

		System.Diagnostics.Debug.WriteLine(
			$"[desuwatch] protection lifted ({reason}): resumed {resumed.Count} service(s)");
	}

	private void RefreshProtectionState()
	{
		var active = _protection.IsProtectionActive;
		IsProtectionActive = active;

		if (!active)
		{
			ProtectionStatusText = "";
			return;
		}

		var services = _protection.ProtectedServices;
		ProtectionStatusText = services.Count == 1
			? $"{ServiceProtectionController.GetDisplayName(services[0])} paused"
			: $"{services.Count} services paused";
	}

	/// <summary>
	/// True when the cost + budget state warrants offering protection
	/// to the user. Exposed as a property so <see cref="ApplyBannerMessage"/>
	/// and the banner's button visibility stay in sync.
	/// <para>
	/// Returns false in NotifyOnly mode regardless of cost/budget state —
	/// the user explicitly opted out of action, and continuing to show
	/// the button would ignore that choice. Auto mode is handled
	/// separately in <see cref="TryAutoProtect"/>; this method controls
	/// the Ask-mode button and the eligibility gate shared by both.
	/// </para>
	/// </summary>
	private bool ShouldOfferProtection()
	{
		if (_settings.HotspotProtection.Mode == ProtectionMode.NotifyOnly) return false;
		if (!_settings.HotspotProtection.HasAnyServiceSelected) return false;
		if (!IsProtectionTriggerActive()) return false;

		return _lastProjection.Severity is BudgetSeverity.Warning or BudgetSeverity.OverBudget;
	}

	/// <summary>
	/// True when the connection is in any state that could warrant
	/// proactive intervention: metered, roaming, or already over the
	/// carrier-reported limit. Broader than <c>cost.IsMetered</c> alone —
	/// roaming and over-limit both mean bytes are expensive even when
	/// the base cost level is Unrestricted.
	/// </summary>
	private bool IsProtectionTriggerActive()
	{
		var cost = ConnectionCost;
		return cost.IsMetered || cost.IsRoaming || cost.IsOverDataLimit;
	}

	/// <summary>
	/// Returns a lowercase phrase for the banner body, or null when the
	/// projection is too quiet to deserve banner real estate. Lowercase
	/// so it can glue cleanly after "Metered (AT&amp;T) — ".
	/// </summary>
	private string? BuildBudgetPhrase(BudgetProjection projection)
	{
		if (projection.Severity is BudgetSeverity.Unknown or BudgetSeverity.OnTrack)
			return null;

		var capGb = _settings.MonthlyDataCapGb;

		if (projection.Severity == BudgetSeverity.OverBudget && projection.EtaLocal is null)
		{
			// Already past the cap, no future ETA — just state the fact.
			return $"over {capGb:0.#} GB cap";
		}

		if (projection.EtaLocal is { } eta)
		{
			var etaText = BudgetProjector.FormatEta(eta, DateTime.Now);
			return $"projected to exceed {capGb:0.#} GB cap {etaText}";
		}

		// Watch / Warning without a crossing ETA inside the cycle —
		// typically means the rate is high enough to be concerning but
		// not quite high enough to blow through the cap. Quiet by
		// returning null (caller will hide the banner if not metered).
		return null;
	}

private void ApplyEvent(NetworkEvent e)
	{
		if (!_byPid.TryGetValue(e.ProcessId, out var app))
		{
			app = new AppUsageViewModel(e.ProcessId, e.ProcessName, _settings.Anomaly);
			_byPid[e.ProcessId] = app;
			Apps.Add(app);
		}

		app.Record(e);

		if (e.Direction == TransferDirection.Sent)
			TotalBytesSent += e.Bytes;
		else
			TotalBytesReceived += e.Bytes;

		// Opportunistic reverse DNS — cached, rate-limited internally.
		_dnsResolver.RequestLookup(e.RemoteAddress);
	}

	private void OnHostResolved(object? sender, HostResolvedEventArgs e)
	{
		Dispatcher.UIThread.Post(() =>
		{
			foreach (var app in _byPid.Values)
				app.ApplyHostnameResolution(e.IpAddress, e.Hostname);
		});
	}

	[RelayCommand]
	private void PauseBackgroundServices()
	{
		// User-initiated from the banner action button (Ask mode) or
		// manually triggered via settings in the future.
		ApplyProtection(manual: true);
	}

	[RelayCommand]
	private void ResumeBackgroundServices()
	{
		LiftProtection(reason: "user requested");
	}

	[RelayCommand]
	private void Pause()
	{
		if (!IsCapturing) return;
		try
		{
			_engine.Stop();
			IsCapturing = false;
			StatusText = "Paused";
			StatusPillText = "paused";
		}
		catch (Exception ex)
		{
			StatusText = "Pause failed: " + ex.Message;
		}
	}

	[RelayCommand]
	private void Resume()
	{
		if (IsCapturing) return;
		try
		{
			_engine.Start();
			IsCapturing = true;
			StatusText = "Capturing (kernel ETW)";
			StatusPillText = "capturing · kernel ETW";
		}
		catch (Exception ex)
		{
			StatusText = "Resume failed: " + ex.Message;
			StatusPillText = "resume failed";
		}
	}

	[RelayCommand]
	private void ClearSession()
	{
		// Reset session-scoped counters without touching the DB. Today /
		// Cycle totals re-query on the next minute bucket and stay
		// accurate because they're DB-backed plus a live session overlay.
		TotalBytesSent = 0;
		TotalBytesReceived = 0;

		foreach (var app in Apps)
			app.ResetSessionCounters();

		Apps.Clear();
		_byPid.Clear();

		_heartbeatBuffer.Clear();
		HeartbeatPoints = _heartbeatBuffer.BuildPoints(HeartbeatWidth, HeartbeatHeight);

		CurrentBytesPerSecondSent = 0;
		CurrentBytesPerSecondReceived = 0;
		ActiveAppCount = 0;
	}

	[RelayCommand]
	private void OpenSettings() => IsSettingsOpen = true;

	[RelayCommand]
	private void CloseSettings()
	{
		_settings.Save();
		IsSettingsOpen = false;
		// Settings that apply live (cap display, cycle reset day) surface
		// on the next sparkline tick via RefreshPeriodTotals; no need to
		// nudge anything here.
	}

	[RelayCommand]
	private void OpenHistory()
	{
		// Re-query on each open so the panel reflects anything flushed
		// since the last time it was viewed. Cheap — indexed range
		// reads on modest data volumes.
		History.RefreshForCurrentRange();
		IsHistoryOpen = true;
	}

	[RelayCommand]
	private void CloseHistory() => IsHistoryOpen = false;

	public void Dispose()
	{
		// Best-effort resume on exit so we don't leave the user's
		// machine with silently-disabled services after desuwatch closes.
		try { _protection.ResumeProtected(); }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[desuwatch] resume-on-dispose failed: {ex}");
		}

		_sparklineTimer.Stop();
		_sparklineTimer.Tick -= OnSparklineTick;
		_engine.EventObserved -= OnEventObserved;
		_costMonitor.CostChanged -= OnCostChanged;
		_dnsResolver.HostResolved -= OnHostResolved;
		_engine.Dispose();
		_costMonitor.Dispose();
		_dnsResolver.Dispose();
		_historyStore.Dispose();
		_dnsCacheStore.Dispose();
	}
}