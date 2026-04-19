using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desuwatch.App;

/// <summary>
/// User-tunable settings loaded from <c>%LOCALAPPDATA%\desuwatch\settings.json</c>.
/// Missing file or missing fields fall back to defaults; the file is
/// re-written on first successful load so users have a template to edit.
///
/// No in-app settings UI yet — edit the JSON file to customize. A settings
/// pane is planned for a future session.
/// </summary>
public sealed class DesuwatchSettings
{
    public string DataDirectory { get; set; } = DefaultDataDirectory();
    public int RetentionDays { get; set; } = 90;
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Day of month (1–28) on which the hotspot billing cycle resets.
    /// Capped at 28 to avoid February edge cases. If today is before this
    /// day, the cycle is considered to have started on this day of the
    /// previous month.
    /// </summary>
    public int BillingCycleResetDay { get; set; } = 1;

    /// <summary>
    /// How long to retain successfully-resolved DNS entries on disk.
    /// Negative cache entries use a fixed 15-minute TTL and are not
    /// user-configurable.
    /// </summary>
    public int DnsPositiveRetentionDays { get; set; } = 30;

    /// <summary>
    /// If true, the main window is hidden on launch and the user must
    /// open it from the tray. Tray icon still appears either way.
    /// </summary>
    public bool StartMinimizedToTray { get; set; } = false;

    /// <summary>
    /// What to do when the user clicks the main window's X. Defaults to
    /// <see cref="CloseActionMode.Ask"/>; the close dialog's
    /// "Don't ask again" checkbox updates this to skip the prompt on
    /// future closes.
    /// </summary>
    public CloseActionMode CloseAction { get; set; } = CloseActionMode.Ask;

    public AnomalySettings Anomaly { get; set; } = new();
    
    /// <summary>
    /// Behavior when a metered connection combines with an over-budget
    /// projection. See <see cref="HotspotProtectionSettings"/>.
    /// </summary>
    public HotspotProtectionSettings HotspotProtection { get; set; } = new();

    /// <summary>
    /// Monthly data cap in GB. 0 means "no cap set" and the cycle card
    /// simply shows raw usage without a percentage. Used by the cycle
    /// card subtitle and predictive-budget warnings.
    /// <para>
    /// Exposed as nullable so Avalonia's NumericUpDown — whose Value is
    /// decimal? — can round-trip an empty field without throwing. A null
    /// input is treated as 0 (no cap).
    /// </para>
    /// </summary>
    private double _monthlyDataCapGb;
    public double? MonthlyDataCapGb
    {
        get => _monthlyDataCapGb;
        set => _monthlyDataCapGb = value ?? 0;
    }

    /// <summary>
    /// User-facing preset for row-pulse sensitivity. On save, this is
    /// folded back into <see cref="AnomalySettings.StdDevThreshold"/>
    /// so existing consumers of <see cref="Anomaly"/> keep working.
    /// </summary>
    public AnomalySensitivity AnomalySensitivity { get; set; } = AnomalySensitivity.Normal;

    [JsonIgnore]
    public long MonthlyDataCapBytes => _monthlyDataCapGb > 0
        ? (long)(_monthlyDataCapGb * 1024 * 1024 * 1024)
        : 0;

    [JsonIgnore]
    public bool HasDataCap => _monthlyDataCapGb > 0;

    [JsonIgnore]
    public string HistoryDbPath => Path.Combine(DataDirectory, "history.db");

    [JsonIgnore]
    public TimeSpan FlushInterval => TimeSpan.FromSeconds(Math.Max(5, FlushIntervalSeconds));

    [JsonIgnore]
    public TimeSpan Retention => TimeSpan.FromDays(Math.Max(1, RetentionDays));

    [JsonIgnore]
    public int BillingCycleResetDayClamped => Math.Clamp(BillingCycleResetDay, 1, 28);

    [JsonIgnore]
    public string DnsCacheDbPath => Path.Combine(DataDirectory, "dns-cache.db");

    [JsonIgnore]
    public TimeSpan DnsPositiveRetention => TimeSpan.FromDays(Math.Max(1, DnsPositiveRetentionDays));

    public static string SettingsFilePath =>
        Path.Combine(DefaultDataDirectory(), "settings.json");

    public static DesuwatchSettings LoadOrCreate()
    {
        var path = SettingsFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<DesuwatchSettings>(json, SerializerOptions);
                if (loaded is not null) return loaded;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[desuwatch] settings load failed, using defaults: {ex.Message}");
            }
        }

        var fresh = new DesuwatchSettings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            // Sync the user-facing sensitivity preset into the dev-tunable
            // Anomaly knob so both surfaces agree on disk.
            Anomaly.StdDevThreshold = AnomalySensitivity switch
            {
                AnomalySensitivity.Low => 3.5,
                AnomalySensitivity.High => 1.8,
                _ => 2.5,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            var json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[desuwatch] settings save failed: {ex.Message}");
        }
    }

    private static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "desuwatch");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}

public sealed class AnomalySettings
{
    public int WarmupSamples { get; set; } = 20;
    public long AbsoluteFloorBytes { get; set; } = 50 * 1024;
    public double StdDevThreshold { get; set; } = 2.5;
}

public enum CloseActionMode
{
    Ask,
    MinimizeToTray,
    Quit
}

public enum AnomalySensitivity
{
    Low,
    Normal,
    High
}