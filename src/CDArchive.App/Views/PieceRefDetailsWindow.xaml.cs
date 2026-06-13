using System.Windows;
using System.Windows.Controls;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.App.Views;

/// <summary>
/// Refines an existing <see cref="TrackPieceRef"/> with optional range and
/// marker anchors:
/// <list type="bullet">
///   <item><see cref="TrackPieceRef.EndSubpiecePath"/> for tracks that span
///         several adjacent sibling subpieces.</item>
///   <item><see cref="TrackPieceRef.StartMarker"/> / <see cref="TrackPieceRef.EndMarker"/>
///         to pin the track inside the start (and end) subpiece.</item>
/// </list>
/// <para>
/// The window resolves the ref's target inside <c>allPieces</c> at construction
/// time so its combos can offer the right list of sibling subpieces and markers.
/// On OK it mutates the ref in place — the caller should re-render the row to
/// pick up the updated <see cref="TrackPieceRef.DisplaySummary"/>.
/// </para>
/// </summary>
public partial class PieceRefDetailsWindow : Window
{
    private readonly TrackPieceRef _ref;

    /// <summary>Top-level piece the ref resolves to (composer + piece title).</summary>
    private CanonPiece? _root;

    /// <summary>
    /// Subpieces of the version (when versioned) or piece (otherwise) — i.e.
    /// the parent of the ref's start subpiece. <c>null</c> when the ref points
    /// at the top-level piece itself, in which case range and per-subpiece
    /// markers are not meaningful (markers come from the top piece directly).
    /// </summary>
    private List<CanonPiece>? _siblingSet;

    /// <summary>The actual subpiece the ref points at (last entry on SubpiecePath).</summary>
    private CanonPiece? _startSubpiece;

    /// <summary>True if user clicked OK; the ref has been mutated.</summary>
    public bool Saved { get; private set; }

    public PieceRefDetailsWindow(TrackPieceRef pieceRef, IReadOnlyList<CanonPiece> allPieces)
    {
        InitializeComponent();
        _ref = pieceRef;

        SummaryText.Text = pieceRef.DisplaySummary;

        ResolveTarget(allPieces);
        InitRangeUI();
        InitMarkerCombos();
        InitVariantChecklist(allPieces);
    }

    // ── Variants ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Populates the variant checklist from the variants available along the
    /// ref's resolved path (leaf + ancestors + version), reusing the same
    /// collector the save path uses so the picker offers exactly what will
    /// persist. The whole section stays collapsed when no variants apply.
    /// Current selections are pre-ticked, matched by id then description.
    /// </summary>
    private void InitVariantChecklist(IReadOnlyList<CanonPiece> allPieces)
    {
        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(allPieces);
        var available = resolver.CollectAvailableVariants(_ref);
        if (available.Count == 0) return;   // section stays Collapsed

        VariantSection.Visibility = Visibility.Visible;

        foreach (var variant in available)
        {
            bool isSelected = _ref.Variants is { Count: > 0 } && _ref.Variants.Any(vr =>
                (vr.Id != 0 && vr.Id == variant.Id) ||
                (vr.Id == 0 && !string.IsNullOrWhiteSpace(vr.Description) &&
                 string.Equals(vr.Description, variant.Description, StringComparison.OrdinalIgnoreCase)));

            VariantList.Children.Add(new CheckBox
            {
                Content   = variant.Description,
                Tag       = variant,
                IsChecked = isSelected,
                FontSize  = 11,
                Margin    = new Thickness(0, 2, 0, 2),
                ToolTip   = string.IsNullOrWhiteSpace(variant.LongDescription)
                                ? null : variant.LongDescription,
            });
        }
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    /// <summary>
    /// Walks <paramref name="allPieces"/> using the ref's identity
    /// (composer / piece title / version / subpiece path) and remembers the
    /// resulting top-level piece, the start subpiece, and the parent's
    /// subpiece collection (for the range UI).
    /// </summary>
    private void ResolveTarget(IReadOnlyList<CanonPiece> allPieces)
    {
        _root = allPieces.FirstOrDefault(p =>
            string.Equals(p.Composer, _ref.Composer, StringComparison.OrdinalIgnoreCase) &&
            TitleMatches(p, _ref.PieceTitle));
        if (_root is null) return;

        // Choose the subpiece root (version or main).
        List<CanonPiece>? subs;
        if (!string.IsNullOrWhiteSpace(_ref.VersionDescription) && _root.Versions is not null)
        {
            var v = _root.Versions.FirstOrDefault(x =>
                string.Equals(x.Description, _ref.VersionDescription, StringComparison.OrdinalIgnoreCase));
            subs = v?.Subpieces;
        }
        else
        {
            subs = _root.Subpieces;
        }

        // Walk the SubpiecePath segments. _siblingSet ends up holding the
        // collection that contains the *start* subpiece (i.e. its siblings),
        // so the range combo offers the right list.
        if (_ref.SubpiecePath is { Count: > 0 } path && subs is not null)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var match = subs.FirstOrDefault(s => SegmentMatches(s, path[i]));
                if (match is null) { _siblingSet = null; _startSubpiece = null; return; }

                if (i == path.Count - 1)
                {
                    _siblingSet   = subs;
                    _startSubpiece = match;
                }
                else
                {
                    subs = match.Subpieces;
                    if (subs is null) return;
                }
            }
        }
    }

    private static bool TitleMatches(CanonPiece p, string title)
    {
        if (string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(p.DisplayTitle, title, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(p.DisplayTitleShort, title, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool SegmentMatches(CanonPiece sp, string segment)
    {
        if (string.Equals(sp.Title, segment, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(sp.DisplayTitle, segment, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(sp.DisplayTitleShort, segment, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(sp.SubpieceDisplayTitle, segment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ── Range UI init ────────────────────────────────────────────────────────

    private void InitRangeUI()
    {
        // Range only makes sense when we have an identifiable start subpiece
        // and the ref carries a SubpiecePath (otherwise it's already "the
        // whole piece" and there's nothing to range across).
        bool rangeEnabled = _startSubpiece is not null && _siblingSet is not null;
        RangeCheck.IsEnabled = rangeEnabled;
        RangePanel.IsEnabled = false;

        if (!rangeEnabled)
        {
            RangeNote.Text = "Range refs require a subpiece path. Pick a subpiece in the picker first.";
            return;
        }

        // Populate the end-subpiece combo with the start subpiece's siblings —
        // including the start itself, since selecting "(no range)" is encoded
        // by leaving the checkbox off.
        EndSubpieceCombo.Items.Clear();
        foreach (var sp in _siblingSet!)
        {
            EndSubpieceCombo.Items.Add(new ComboBoxItem
            {
                Content = sp.SubpieceDisplayTitle,
                Tag = sp,
            });
        }

        // Pre-select if the ref already has an end path.
        if (_ref.EndSubpiecePath is { Count: > 0 } endPath)
        {
            // Match by the last segment against the sibling set's display titles.
            var lastSeg = endPath[^1];
            var match = _siblingSet.FirstOrDefault(s => SegmentMatches(s, lastSeg));
            if (match is not null)
            {
                RangeCheck.IsChecked = true;
                RangePanel.IsEnabled = true;
                foreach (ComboBoxItem item in EndSubpieceCombo.Items)
                    if (ReferenceEquals(item.Tag, match))
                    { EndSubpieceCombo.SelectedItem = item; break; }
            }
        }
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        RangePanel.IsEnabled = RangeCheck.IsChecked == true;
        // When toggling the range on for the first time, default the end
        // combo to the start subpiece — a "single-segment range" — so the
        // user only has to pick the actual end if it differs.
        if (RangeCheck.IsChecked == true && EndSubpieceCombo.SelectedItem == null)
        {
            foreach (ComboBoxItem item in EndSubpieceCombo.Items)
                if (ReferenceEquals(item.Tag, _startSubpiece))
                { EndSubpieceCombo.SelectedItem = item; break; }
        }
        // Refresh the end-marker combo too, since which subpiece's markers we
        // offer depends on whether range is on.
        RefreshEndMarkerCombo();
    }

    private void OnEndSubpieceChanged(object sender, SelectionChangedEventArgs e)
    {
        // When the user picks a different end subpiece, rebuild the end-marker
        // combo from that subpiece's markers (start marker stays bound to start).
        RefreshEndMarkerCombo();
    }

    // ── Marker combos ────────────────────────────────────────────────────────

    private void InitMarkerCombos()
    {
        // Source for the start marker: the start subpiece's markers (or, if
        // the ref is whole-piece, the top piece's own markers).
        var startMarkerSource = _startSubpiece?.Markers ?? _root?.Markers;
        PopulateMarkerCombo(StartMarkerCombo, startMarkerSource, _ref.StartMarker, out var startNote);
        StartMarkerHint.Text = startNote;

        RefreshEndMarkerCombo();
    }

    private void RefreshEndMarkerCombo()
    {
        // End marker source: end subpiece's markers when range is on, else the
        // start subpiece's. (An end marker without a range is interpreted as
        // "in the same subpiece as the start", per TrackPieceRef's docs.)
        List<MusicalMarker>? source;
        if (RangeCheck.IsChecked == true &&
            EndSubpieceCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is CanonPiece endSub)
        {
            source = endSub.Markers;
        }
        else
        {
            source = _startSubpiece?.Markers ?? _root?.Markers;
        }

        PopulateMarkerCombo(EndMarkerCombo, source, _ref.EndMarker, out var endNote);
        EndMarkerHint.Text = endNote;
    }

    private static void PopulateMarkerCombo(
        ComboBox combo, List<MusicalMarker>? markers, MarkerReference? selected, out string note)
    {
        combo.Items.Clear();
        // Always offer "(no marker)" as the first item — selecting it clears
        // the anchor on OK.
        combo.Items.Add(new ComboBoxItem { Content = "(no marker)", Tag = null });

        if (markers is null or { Count: 0 })
        {
            note = "No markers defined on the target subpiece. Add markers via the piece editor first.";
            combo.SelectedIndex = 0;
            return;
        }

        note = $"{markers.Count} marker(s) available on the target subpiece.";
        foreach (var m in markers)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = FormatMarker(m),
                Tag = m,
            });
        }

        // Preselect by Id when present, else by kind+value as a best-effort fallback.
        if (selected is not null)
        {
            ComboBoxItem? match = null;
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag is not MusicalMarker m) continue;
                if (selected.Id != 0 && m.Id == selected.Id) { match = item; break; }
                if (selected.Id == 0 && m.Kind == selected.Kind &&
                    string.Equals(m.Value, selected.Value, StringComparison.OrdinalIgnoreCase))
                { match = item; }
            }
            combo.SelectedItem = match ?? combo.Items[0];
        }
        else
        {
            combo.SelectedIndex = 0;
        }
    }

    private static string FormatMarker(MusicalMarker m)
    {
        var kind = m.Kind switch
        {
            MarkerKind.Tempo         => "Tempo",
            MarkerKind.RehearsalMark => "Rehearsal",
            MarkerKind.BarNumber     => "Bar",
            MarkerKind.Section       => "Section",
            _                        => m.Kind.ToString(),
        };
        var label = !string.IsNullOrEmpty(m.Value)
            ? m.Value
            : (m.BarNumber is { } bn ? $"bar {bn}" : "(no value)");
        return $"{kind}: {label}";
    }

    // ── OK ───────────────────────────────────────────────────────────────────

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // End subpiece path: only set when the checkbox is on AND the chosen
        // end is a different subpiece than the start (single-subpiece "range"
        // is normalised back to no-range so DisplaySummary stays clean).
        if (RangeCheck.IsChecked == true &&
            EndSubpieceCombo.SelectedItem is ComboBoxItem endItem &&
            endItem.Tag is CanonPiece endSub &&
            !ReferenceEquals(endSub, _startSubpiece))
        {
            // Build the end path by replacing the last segment of the start
            // path with the chosen end subpiece's display title. Both
            // siblings live under the same parent so the prefix is identical.
            var startPath = _ref.SubpiecePath ?? new List<string>();
            var newEnd = new List<string>(startPath);
            if (newEnd.Count > 0)
                newEnd[^1] = endSub.SubpieceDisplayTitle;
            else
                newEnd.Add(endSub.SubpieceDisplayTitle);
            _ref.EndSubpiecePath = newEnd;
        }
        else
        {
            _ref.EndSubpiecePath = null;
        }

        _ref.StartMarker = ToMarkerReference(StartMarkerCombo.SelectedItem);
        _ref.EndMarker   = ToMarkerReference(EndMarkerCombo.SelectedItem);

        // Variants: collect the ticked entries (in checklist order). When none
        // are ticked, store null — "no variant identified" is a valid state.
        var chosen = new List<VariantReference>();
        foreach (var child in VariantList.Children)
        {
            if (child is not CheckBox { IsChecked: true, Tag: VariantInfo v }) continue;
            chosen.Add(new VariantReference { Id = v.Id, Description = v.Description });
        }
        _ref.Variants = chosen.Count > 0 ? chosen : null;

        Saved = true;
        DialogResult = true;
    }

    private static MarkerReference? ToMarkerReference(object? selected)
    {
        if (selected is not ComboBoxItem item) return null;
        if (item.Tag is not MusicalMarker m) return null;
        return new MarkerReference
        {
            Id        = m.Id,
            Kind      = m.Kind,
            Value     = m.Value,
            BarNumber = m.BarNumber,
        };
    }
}
