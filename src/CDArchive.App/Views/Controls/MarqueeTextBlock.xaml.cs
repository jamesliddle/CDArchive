using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CDArchive.App.Views.Controls;

/// <summary>
/// A single-line text display that scrolls its content left, looping, when the
/// text is too wide for the available width; when it fits, the text is shown
/// statically and centred (no animation).
///
/// <para>
/// Used by the player bar for the track caption (title line + composer /
/// performers / album line) so long captions remain fully readable on a
/// narrow window. Font size / weight / foreground / family inherit from the
/// control instance via WPF's <c>TextElement</c> property inheritance — the
/// caller styles the <c>MarqueeTextBlock</c> and the inner text follows.
/// </para>
///
/// <para>
/// Wrap-around loop: while scrolling, two copies of the text are positioned on a
/// Canvas (copy A at x=0, copy B a small gap behind), moved left by one shared
/// transform. The transform animates left by one copy-plus-gap and repeats, so
/// copy B's head follows close behind copy A's tail — the head re-enters from the
/// right before the tail has fully exited the left, and at the wrap point copy B
/// sits exactly where copy A started (the restart is invisible).
/// </para>
/// </summary>
public partial class MarqueeTextBlock : UserControl
{
    /// <summary>Scroll speed in device-independent pixels per second.</summary>
    private const double SpeedPxPerSec = 40.0;

    /// <summary>Gap between the tail of one copy and the head of the next, px.</summary>
    private const double Gap = 24.0;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(MarqueeTextBlock),
            new PropertyMetadata(null, OnTextChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public MarqueeTextBlock()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateMarquee();
        SizeChanged += (_, _) => UpdateMarquee();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarqueeTextBlock)d).OnTextChanged();

    private void OnTextChanged()
    {
        // Defer to Loaded priority so the new text has been measured + arranged
        // before we compare its width against the viewport.
        Dispatcher.BeginInvoke(new Action(UpdateMarquee), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-evaluates whether the text overflows and starts / stops the scroll
    /// animation accordingly. Idempotent — safe to call on Loaded, SizeChanged,
    /// and Text change.
    /// </summary>
    private void UpdateMarquee()
    {
        if (!IsLoaded) return;

        // Stop any running animation and reset before re-deciding. Passing a
        // null animation releases the animation clock so the local X value
        // below takes effect.
        Translate.BeginAnimation(TranslateTransform.XProperty, null);
        Translate.X = 0;

        UpdateLayout();

        double viewport = Viewport.ActualWidth;
        double textWidth = PrimaryText.ActualWidth;

        // Fits (or nothing to measure yet) → static + centred, no scroll layer.
        if (textWidth <= 0 || viewport <= 0 || textWidth <= viewport)
        {
            ScrollCanvas.Visibility = Visibility.Collapsed;
            PrimaryText.Visibility = Visibility.Visible;
            return;
        }

        // Overflow → wrap-around left-scroll with two Canvas copies. Hide the
        // sizer (kept for layout height); copy B trails copy A by the gap.
        PrimaryText.Visibility = Visibility.Hidden;
        ScrollCanvas.Visibility = Visibility.Visible;
        Canvas.SetLeft(ScrollB, textWidth + Gap);

        double advance = textWidth + Gap;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = -advance,
            Duration = TimeSpan.FromSeconds(advance / SpeedPxPerSec),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Translate.BeginAnimation(TranslateTransform.XProperty, anim);
    }
}
