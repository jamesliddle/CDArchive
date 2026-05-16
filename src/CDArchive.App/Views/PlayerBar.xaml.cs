using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CDArchive.App.ViewModels;

namespace CDArchive.App.Views;

/// <summary>
/// Slider scrub coordination — the VM is the single source of truth for whether
/// the user is actively dragging the thumb. <see cref="PlayerViewModel.BeginScrub"/>
/// stops playback-driven updates clobbering the slider's value; the matching
/// <see cref="PlayerViewModel.EndScrub"/> seeks to wherever the slider ended up.
/// </summary>
public partial class PlayerBar : UserControl
{
    public PlayerBar() => InitializeComponent();

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    private void ProgressSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        Vm?.BeginScrub();

    private void ProgressSlider_DragCompleted(object sender, DragCompletedEventArgs e) =>
        Vm?.EndScrub(ProgressSlider.Value);

    private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // For a pure click on the track (IsMoveToPointEnabled=True), DragStarted
        // never fires — IsScrubbing is false and we seek here. For a real drag,
        // DragCompleted already handled the seek and reset IsScrubbing.
        if (Vm is null || Vm.IsScrubbing) return;
        Vm.EndScrub(ProgressSlider.Value);
    }
}
