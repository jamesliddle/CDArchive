using System.Windows;
using CDArchive.App.ViewModels;

namespace CDArchive.App.Views;

/// <summary>
/// Code-behind for the MB review pane (slice 5).
///
/// <para>Most behaviour lives on <see cref="MusicBrainzReviewViewModel"/>;
/// the window only owns:</para>
/// <list type="bullet">
///   <item>The "Jump to" toolbar's scroll-to-anchor handlers (visual-tree
///         walk for the ScrollViewer offset).</item>
///   <item>The DialogResult bridge — the VM raises
///         <see cref="MusicBrainzReviewViewModel.DialogResult"/> when Apply /
///         Cancel / Apply none fires; the window mirrors it back via
///         <see cref="Window.DialogResult"/> so <c>ShowDialog</c> returns
///         cleanly.</item>
/// </list>
/// </summary>
public partial class MusicBrainzReviewWindow : Window
{
    public MusicBrainzReviewViewModel ViewModel { get; }

    public MusicBrainzReviewWindow(MusicBrainzReviewViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;

        // Bridge VM.DialogResult → Window.DialogResult. ShowDialog only
        // returns once the latter is set, so we mirror the VM's signal.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MusicBrainzReviewViewModel.DialogResult)
                && vm.DialogResult is { } result)
            {
                DialogResult = result;
            }
        };
    }

    // ── "Jump to" handlers — scroll the named anchor into view ───────────────

    private void OnJumpToAlbums(object sender, RoutedEventArgs e)    => ScrollAnchorIntoView(AlbumsSectionAnchor);

    /// <summary>
    /// Scroll the supplied anchor element into the viewport's top region.
    /// FrameworkElement.BringIntoView() is the simplest way; relies on the
    /// containing ScrollViewer responding to RequestBringIntoView (the
    /// default behaviour).
    /// </summary>
    private void ScrollAnchorIntoView(FrameworkElement anchor)
    {
        if (anchor is null) return;
        anchor.BringIntoView();
    }
}
