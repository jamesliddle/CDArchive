using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CDArchive.App.ViewModels;

namespace CDArchive.App.Views;

/// <summary>
/// Progress-slider interaction:
/// <list type="bullet">
///   <item><b>Click on the track</b> (<see cref="ProgressSlider_PreviewMouseLeftButtonDown"/>)
///         seeks straight to the clicked position. Handled explicitly from the
///         click X rather than relying on the slider's move-to-point + thumb-drag
///         machinery, which doesn't reliably raise a matching seek on a bare click.</item>
///   <item><b>Drag the thumb</b> uses the standard
///         <c>Thumb.DragStarted</c> / <c>DragCompleted</c> pair: BeginScrub stops
///         playback-driven updates clobbering the thumb; EndScrub seeks to where
///         it ended up. A click that lands on the thumb is left to the drag path.</item>
/// </list>
/// </summary>
public partial class PlayerBar : UserControl
{
    public PlayerBar() => InitializeComponent();

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    private void ProgressSlider_DragStarted(object sender, DragStartedEventArgs e) =>
        Vm?.BeginScrub();

    private void ProgressSlider_DragCompleted(object sender, DragCompletedEventArgs e) =>
        Vm?.EndScrub(ProgressSlider.Value);

    private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || !Vm.IsTrackLoaded) return;

        // A press on the thumb starts a drag — let the Thumb handle it.
        if (IsOnThumb(e.OriginalSource)) return;

        // Track click: jump straight to the clicked position.
        var width = ProgressSlider.ActualWidth;
        if (width <= 0) return;
        var fraction = Math.Clamp(e.GetPosition(ProgressSlider).X / width, 0, 1);
        var value = ProgressSlider.Minimum + fraction * (ProgressSlider.Maximum - ProgressSlider.Minimum);

        Vm.SliderValue = value; // move the thumb + time labels immediately
        Vm.EndScrub(value);     // seek
        e.Handled = true;       // suppress the default track handling
    }

    private static bool IsOnThumb(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is Thumb) return true;
        return false;
    }
}
