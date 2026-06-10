using System.Windows;
using System.Windows.Controls;

namespace CDArchive.App.Views.Controls;

/// <summary>
/// The seek-button icon: a circular arrow with the seek step (<see cref="Seconds"/>)
/// centred inside, Overcast-style. <see cref="Mirror"/> flips it horizontally so
/// the same artwork serves both the back (counter-clockwise) and forward
/// (clockwise) seek buttons.
/// </summary>
public partial class SeekGlyph : UserControl
{
    public static readonly DependencyProperty SecondsProperty =
        DependencyProperty.Register(
            nameof(Seconds), typeof(int), typeof(SeekGlyph), new PropertyMetadata(10));

    public static readonly DependencyProperty MirrorProperty =
        DependencyProperty.Register(
            nameof(Mirror), typeof(bool), typeof(SeekGlyph),
            new PropertyMetadata(false, OnMirrorChanged));

    public int Seconds
    {
        get => (int)GetValue(SecondsProperty);
        set => SetValue(SecondsProperty, value);
    }

    /// <summary>When true, the arrow is mirrored horizontally (forward seek).</summary>
    public bool Mirror
    {
        get => (bool)GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    public SeekGlyph() => InitializeComponent();

    private static void OnMirrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var glyph = (SeekGlyph)d;
        if (glyph.MirrorScale is not null)
            glyph.MirrorScale.ScaleX = (bool)e.NewValue ? -1 : 1;
    }
}
