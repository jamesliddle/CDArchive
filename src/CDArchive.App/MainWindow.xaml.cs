using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CDArchive.App.ViewModels;

namespace CDArchive.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    /// <summary>
    /// Space-bar = play/pause when no text-input control has focus. We use
    /// PreviewKeyDown so the shortcut fires at the window level regardless
    /// of which view is active, but bail out if a TextBox / ComboBox /
    /// PasswordBox owns the keystroke (so the user can still type spaces
    /// into the album editor, etc.).
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        if (e.OriginalSource is TextBoxBase or PasswordBox or ComboBox) return;
        if (DataContext is not MainViewModel mvm) return;

        var player = mvm.PlayerViewModel;
        if (!player.IsTrackLoaded) return; // greyed bar — nothing to toggle

        player.PlayPauseCommand.Execute(null);
        e.Handled = true;
    }
}
