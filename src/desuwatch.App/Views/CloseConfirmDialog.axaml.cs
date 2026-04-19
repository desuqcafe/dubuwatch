using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Desuwatch.App.Views;

public enum CloseChoice
{
	Cancel,
	MinimizeToTray,
	Quit
}

public sealed record CloseDialogResult(CloseChoice Choice, bool RememberChoice);

public partial class CloseConfirmDialog : Window
{
	public CloseConfirmDialog()
	{
		InitializeComponent();
	}

	private void OnCancelClicked(object? sender, RoutedEventArgs e) =>
		Close(new CloseDialogResult(CloseChoice.Cancel, false));

	private void OnMinimizeClicked(object? sender, RoutedEventArgs e) =>
		Close(new CloseDialogResult(
			CloseChoice.MinimizeToTray,
			RememberChoiceCheckBox.IsChecked == true));

	private void OnQuitClicked(object? sender, RoutedEventArgs e) =>
		Close(new CloseDialogResult(
			CloseChoice.Quit,
			RememberChoiceCheckBox.IsChecked == true));
}