using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Desuwatch.App.ViewModels;
using Desuwatch.App.Views;

namespace Desuwatch.App;

public partial class App : Application
{
	private TrayIconService? _trayIcon;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			var vm = new MainWindowViewModel();
			var mainWindow = new MainWindow { DataContext = vm };

			// Start hidden if the user opted in; otherwise show normally.
			// Either way, the window gets assigned to desktop.MainWindow so
			// Avalonia's lifetime semantics stay intact.
			desktop.MainWindow = mainWindow;
			desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

			_trayIcon = new TrayIconService(
				vm,
				() => mainWindow,
				() =>
				{
					mainWindow.RequestQuit();
					desktop.Shutdown();
				});

			if (!vm.Settings.StartMinimizedToTray)
			{
				mainWindow.Show();
			}

			desktop.Exit += (_, _) =>
			{
				_trayIcon?.Dispose();
				vm.Dispose();
			};
		}

		base.OnFrameworkInitializationCompleted();
	}
}