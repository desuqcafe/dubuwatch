using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Desuwatch.App.ViewModels;
using Desuwatch.App.Views;

namespace Desuwatch.App;

/// <summary>
/// Owns the notification-area (tray) icon for desuwatch. The icon is a
/// tiny sparkline glyph rendered once at startup; live throughput is
/// surfaced via the tooltip, refreshed each second in sync with the
/// main view-model's sparkline tick.
///
/// Menu actions: Open (show/focus main window), Quit (dispose the
/// entire app).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Func<Window?> _mainWindowAccessor;
    private readonly Action _quitAction;
    private readonly TrayIcon _trayIcon;
    private readonly DispatcherTimer _tooltipTimer;

    public TrayIconService(
        MainWindowViewModel viewModel,
        Func<Window?> mainWindowAccessor,
        Action quitAction)
    {
        _viewModel = viewModel;
        _mainWindowAccessor = mainWindowAccessor;
        _quitAction = quitAction;

        _trayIcon = new TrayIcon
        {
            Icon = BuildIcon(),
            ToolTipText = "desuwatch",
            IsVisible = true,
            Menu = BuildMenu()
        };
        _trayIcon.Clicked += OnTrayClicked;

        // Attach to the application so Avalonia manages its lifetime.
        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);

        _tooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tooltipTimer.Tick += (_, _) => RefreshTooltip();
        _tooltipTimer.Start();

        RefreshTooltip();
    }

    private void RefreshTooltip()
    {
        var sent = HumanizeRate(_viewModel.CurrentBytesPerSecondSent);
        var recv = HumanizeRate(_viewModel.CurrentBytesPerSecondReceived);
        var active = _viewModel.ActiveAppCount;
        var appsLabel = active == 1 ? "1 app active" : $"{active} apps active";
        _trayIcon.ToolTipText = $"desuwatch\n↑ {sent}   ↓ {recv}\n{appsLabel}";
    }

    private static string HumanizeRate(long bytesPerSecond)
    {
        // Render in bits/sec to match common "Mbps" expectation.
        var bitsPerSec = bytesPerSecond * 8.0;
        if (bitsPerSec < 1_000) return $"{bitsPerSec:0} bps";
        if (bitsPerSec < 1_000_000) return $"{bitsPerSec / 1_000:0.#} Kbps";
        if (bitsPerSec < 1_000_000_000) return $"{bitsPerSec / 1_000_000:0.##} Mbps";
        return $"{bitsPerSec / 1_000_000_000:0.##} Gbps";
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Open desuwatch");
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Add(openItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => _quitAction();
        menu.Add(quitItem);

        return menu;
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void ShowMainWindow()
    {
        var window = _mainWindowAccessor();
        if (window is null) return;

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Renders a tiny sparkline glyph as the tray icon. Two peaks in
    /// accent purple on transparent background — visually consistent
    /// with the app's sparkline-first aesthetic.
    /// </summary>
    private static WindowIcon BuildIcon()
    {
        const int size = 32;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));

        using (var ctx = bitmap.CreateDrawingContext())
        {
            var stroke = new Pen(new SolidColorBrush(Color.Parse("#9EA1E8")), 2.5,
                lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

            // Two-peak sparkline spanning the full icon width.
            var geometry = new StreamGeometry();
            using (var gctx = geometry.Open())
            {
                gctx.BeginFigure(new Point(3, 22), isFilled: false);
                gctx.LineTo(new Point(9, 14));
                gctx.LineTo(new Point(14, 19));
                gctx.LineTo(new Point(20, 8));
                gctx.LineTo(new Point(25, 16));
                gctx.LineTo(new Point(29, 12));
                gctx.EndFigure(false);
            }
            ctx.DrawGeometry(null, stroke, geometry);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        _tooltipTimer.Stop();
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}