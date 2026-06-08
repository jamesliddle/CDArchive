using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace CDArchive.App.Views.Controls;

/// <summary>
/// Read-only display of a MusicBrainz entity ID (artist / work / release).
/// Used by the Composer / Piece / Album editor windows to surface the MB
/// linkage stamped on a canon row during iTunes import.
///
/// <para>
/// Two dependency properties drive the surface:
/// <list type="bullet">
///   <item><c>Mbid</c> — the 36-char UUID (or null). When null/empty the
///         whole control collapses (the editor doesn't waste a row).</item>
///   <item><c>EntityKind</c> — "artist", "work", or "release"; selects the
///         MB browse URL segment.</item>
/// </list>
/// Truncation, copy-to-clipboard, and the open-in-browser button all live
/// here so the three editors don't duplicate the chrome.
/// </para>
/// </summary>
public partial class MusicBrainzIdDisplay : UserControl
{
    public static readonly DependencyProperty MbidProperty =
        DependencyProperty.Register(
            nameof(Mbid),
            typeof(string),
            typeof(MusicBrainzIdDisplay),
            new PropertyMetadata(null, OnMbidChanged));

    public static readonly DependencyProperty EntityKindProperty =
        DependencyProperty.Register(
            nameof(EntityKind),
            typeof(string),
            typeof(MusicBrainzIdDisplay),
            new PropertyMetadata("artist"));

    public string? Mbid
    {
        get => (string?)GetValue(MbidProperty);
        set => SetValue(MbidProperty, value);
    }

    /// <summary>"artist" / "work" / "release". Drives the MB browse URL.</summary>
    public string EntityKind
    {
        get => (string)GetValue(EntityKindProperty);
        set => SetValue(EntityKindProperty, value);
    }

    /// <summary>True when <see cref="Mbid"/> is a non-empty string. Drives the
    /// control's Visibility via XAML binding so an unset MBID collapses the row.</summary>
    public bool HasId => !string.IsNullOrWhiteSpace(Mbid);

    public MusicBrainzIdDisplay()
    {
        InitializeComponent();
        RefreshDisplay();
    }

    private static void OnMbidChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MusicBrainzIdDisplay self)
        {
            self.RefreshDisplay();
            // HasId is a derived property — forcing a binding refresh via the
            // standard ItemsSource pattern isn't available on a plain bool,
            // so we re-apply the Visibility binding's source. Cheapest path:
            // toggle visibility directly.
            self.Visibility = self.HasId ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RefreshDisplay()
    {
        // First 8 chars of an MB UUID gives the user enough to eyeball
        // distinct IDs without dragging the editor wide. The full ID is
        // surfaced in the TextBlock's ToolTip (bound directly in XAML).
        if (IdDisplayText is null) return;
        var id = Mbid;
        if (string.IsNullOrWhiteSpace(id))
        {
            IdDisplayText.Text = "";
            return;
        }
        IdDisplayText.Text = id.Length > 8 ? id[..8] + "…" : id;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Mbid)) return;
        try
        {
            Clipboard.SetText(Mbid);
        }
        catch
        {
            // Clipboard.SetText occasionally throws under tight focus
            // contention (another app holding the clipboard). Swallowing
            // is fine for a copy-button best-effort.
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Mbid)) return;
        var kind = EntityKind?.ToLowerInvariant() ?? "artist";
        // MB browse URLs: /artist/{id}, /work/{id}, /release/{id}. Validate
        // the kind defensively — a typo on the consumer side would otherwise
        // open a 404.
        if (kind != "artist" && kind != "work" && kind != "release")
            kind = "artist";
        var url = $"https://musicbrainz.org/{kind}/{Mbid}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // No browser registered / OS denied. Best-effort; no dialog
            // for a peripheral feature.
        }
    }
}
