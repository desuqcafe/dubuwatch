using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Desuwatch.App.ViewModels;

namespace Desuwatch.App.Views;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();
		Closing += OnWindowClosing;
	}

	private bool _forceQuit;

	/// <summary>
	/// Called by the tray menu's Quit action. Bypasses the close-behavior
	/// dialog/setting check so the window can close cleanly on real exit.
	/// </summary>
	public void RequestQuit()
	{
		_forceQuit = true;
		Close();
	}

	private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
	{
		if (_forceQuit) return;
		if (DataContext is not MainWindowViewModel vm) return;

		var settings = vm.Settings;

		switch (settings.CloseAction)
		{
			case CloseActionMode.Quit:
				// App is in OnExplicitShutdown mode for tray support, so
				// letting the window close here would just hide it. Trigger
				// a full app shutdown via the same path the tray's Quit
				// menu uses.
				e.Cancel = true;
				_forceQuit = true;
				ShutdownApp();
				return;

			case CloseActionMode.MinimizeToTray:
				e.Cancel = true;
				Hide();
				return;

			case CloseActionMode.Ask:
			default:
				e.Cancel = true;

				var dialog = new CloseConfirmDialog();
				var result = await dialog.ShowDialog<CloseDialogResult?>(this);
				if (result is null || result.Choice == CloseChoice.Cancel) return;

				if (result.RememberChoice)
				{
					settings.CloseAction = result.Choice == CloseChoice.Quit
						? CloseActionMode.Quit
						: CloseActionMode.MinimizeToTray;
					settings.Save();
				}

				if (result.Choice == CloseChoice.MinimizeToTray)
				{
					Hide();
				}
				else
				{
					_forceQuit = true;
					ShutdownApp();
				}
				return;
		}
	}
	
	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);

		if (DataContext is ViewModels.MainWindowViewModel vm)
		{
			// Bridge the history VM's save-file needs to Avalonia's storage
			// provider. Keeps the VM free of view-layer types while still
			// using the platform-native file picker.
			vm.History.ConfigureSavePicker(async (suggestedName, extension) =>
			{
				var sp = StorageProvider;
				var file = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
				{
					SuggestedFileName = suggestedName,
					DefaultExtension = extension,
					FileTypeChoices = new[]
					{
						new Avalonia.Platform.Storage.FilePickerFileType(
							extension.ToUpperInvariant() + " file")
						{
							Patterns = new[] { "*." + extension }
						}
					}
				});
				return file?.TryGetLocalPath();
			});
		}
	}
	
	private void OnHistoryChartSizeChanged(object? sender, Avalonia.Controls.SizeChangedEventArgs e)
	{
		if (DataContext is ViewModels.MainWindowViewModel vm)
		{
			vm.History.ResizeChart(e.NewSize.Width, e.NewSize.Height);
		}
	}
	
	private void OnHistoryChartPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
	{
		if (sender is Avalonia.Controls.Canvas canvas &&
		    DataContext is ViewModels.MainWindowViewModel vm)
		{
			var pos = e.GetPosition(canvas);
			vm.History.UpdateHover(pos.X, canvas.Bounds.Width);
		}
	}

	private void OnHistoryChartPointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
	{
		if (DataContext is ViewModels.MainWindowViewModel vm)
			vm.History.HideHover();
	}

	private static void ShutdownApp()
	{
		if (Avalonia.Application.Current?.ApplicationLifetime
		    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.Shutdown();
		}
	}
}