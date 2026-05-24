using System.Windows;

namespace CDArchive.App.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/>. The only class in
/// the app that's allowed to call <see cref="MessageBox.Show(string, string,
/// MessageBoxButton, MessageBoxImage)"/> outside of editor windows; VMs go
/// through <see cref="IDialogService"/> so they stay headless-testable.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK;

    public void ShowInfo(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
