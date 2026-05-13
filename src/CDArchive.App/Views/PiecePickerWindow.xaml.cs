using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

/// <summary>
/// Dialog that lets the user pick a piece, movement, or version and returns
/// a <see cref="TrackPieceRef"/> describing the selection.
///
/// The UI is a single hierarchical tree — Composer → Piece → (Subpiece |
/// Original | Version → Subpiece) — styled to match the Canon view's
/// composer tree. Selection rules:
///   * Composer node: not selectable.
///   * Piece node: whole-piece ref (no subpiece path, no version).
///   * Original node: same as piece (whole piece, no version).
///   * Version node: whole version ref (version description only).
///   * Subpiece node: movement ref with the accumulated subpiece path.
/// </summary>
public partial class PiecePickerWindow : Window
{
    private readonly IReadOnlyList<CanonPiece> _allPieces;

    // Current filter / sort state
    private string _composerFilter = "";          // "" = all composers
    private string _sortField      = "Catalogue"; // Catalogue | Title | Category | Year

    // Guard: event handlers that drive RebuildTree fire during InitializeComponent()
    // (the SortBox IsSelected="True" item triggers SelectionChanged before SearchBox exists).
    private bool _initialized;

    public TrackPieceRef? SelectedRef { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public PiecePickerWindow(IReadOnlyList<CanonPiece> allPieces)
    {
        _allPieces = allPieces;
        InitializeComponent();
        _initialized = true;

        PopulateComposerDropdown();
        RebuildTree();
    }

    // ── Toolbar handlers ──────────────────────────────────────────────────────

    private void PopulateComposerDropdown()
    {
        var composers = _allPieces
            .Select(p => p.Composer ?? "")
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ComposerBox.Items.Clear();
        ComposerBox.Items.Add("(all composers)");
        foreach (var c in composers)
            ComposerBox.Items.Add(c);

        ComposerBox.SelectedIndex = 0;
    }

    private void OnComposerFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _composerFilter = ComposerBox.SelectedIndex > 0
            ? ComposerBox.SelectedItem as string ?? ""
            : "";
        RebuildTree();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _sortField = (SortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Catalogue";
        RebuildTree();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        RebuildTree();
    }

    // ── Tree construction ─────────────────────────────────────────────────────

    private void RebuildTree()
    {
        var search = SearchBox.Text.Trim();

        // 1. Composer filter.
        IEnumerable<CanonPiece> source = string.IsNullOrEmpty(_composerFilter)
            ? _allPieces
            : _allPieces.Where(p =>
                string.Equals(p.Composer, _composerFilter, StringComparison.OrdinalIgnoreCase));

        // 2. Search filter — match on composer, title, display title, or any subpiece title.
        if (!string.IsNullOrEmpty(search))
        {
            source = source.Where(p => PieceMatchesSearch(p, search));
        }

        // 3. Sort within each composer group.
        var sortedPieces = ApplySort(source.ToList());

        // 4. Group into composer → pieces.
        var grouped = sortedPieces
            .GroupBy(p => p.Composer ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 5. Build tree nodes.
        var roots = new List<PickerComposerNode>();
        foreach (var g in grouped)
        {
            var composerNode = new PickerComposerNode
            {
                Name            = g.Key,
                DisplayTitle    = g.Key,   // used by AutoExpandMatches
                LifeSpan        = TryGetLifeSpan(g.First()),
                PieceCountLabel = FormatPieceCountLabel(g.Count()),
            };

            foreach (var piece in g)
            {
                var pieceNode = BuildPieceNode(piece);
                composerNode.Children.Add(pieceNode);
            }
            roots.Add(composerNode);
        }

        PieceTree.ItemsSource = roots;

        // 6. When a search is active, auto-expand composers and pieces whose
        // descendants matched, so the user can see the hits immediately.
        if (!string.IsNullOrEmpty(search))
        {
            foreach (var c in roots)
            {
                c.IsExpanded = true;
                foreach (var p in c.Children)
                    AutoExpandMatches(p, search);
            }
        }

        // Nothing is selected yet.
        SelectedRef = null;
        OkButton.IsEnabled = false;
    }

    private IEnumerable<CanonPiece> ApplySort(List<CanonPiece> pieces) => _sortField switch
    {
        "Title"    => pieces.OrderBy(p => p.Composer ?? "", StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.DisplayTitle,    StringComparer.OrdinalIgnoreCase),
        "Category" => pieces.OrderBy(p => p.Composer ?? "", StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.Category,          StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.CatalogSortNumber)
                            .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
        "Year"     => pieces.OrderBy(p => p.Composer ?? "", StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.PublicationYear ?? int.MaxValue)
                            .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.CatalogSortNumber)
                            .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase),
        _          => pieces.OrderBy(p => p.Composer ?? "", StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.CatalogSortPrefix, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.CatalogSortNumber)
                            .ThenBy(p => p.CatalogSortSuffix, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(p => p.DisplayTitle,       StringComparer.OrdinalIgnoreCase),
    };

    private static bool PieceMatchesSearch(CanonPiece p, string search)
    {
        if ((p.Composer    ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
        if ((p.Title       ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
        if (p.DisplayTitle.Contains(search, StringComparison.OrdinalIgnoreCase))        return true;
        if (SubpieceMatchesSearch(p.Subpieces, search)) return true;
        if (p.Versions is { Count: > 0 })
        {
            foreach (var v in p.Versions)
            {
                if ((v.Description ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
                if (SubpieceMatchesSearch(v.Subpieces, search)) return true;
            }
        }
        return false;
    }

    private static bool SubpieceMatchesSearch(List<CanonPiece>? subs, string search)
    {
        if (subs is null || subs.Count == 0) return false;
        foreach (var sp in subs)
        {
            if (sp.SubpieceDisplayTitle.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
            if (sp.Subpieces is { Count: > 0 } && SubpieceMatchesSearch(sp.Subpieces, search)) return true;
        }
        return false;
    }

    // ── Piece → node, with recursive subpiece/version children ───────────────

    private static PickerPieceNode BuildPieceNode(CanonPiece piece)
    {
        var composer = piece.Composer ?? "";
        // Store the fully qualified DisplayTitle (with key + catalog) but
        // strip any trailing nickname / subtitle suffixes — album refs
        // historically omit those ("Piano Sonata #17 in d, Op. 31 #2"
        // rather than 'Piano Sonata #17 in d, Op. 31 #2 "Tempest"'), and
        // PieceReferenceIndex.EnumerateTitleKeys registers the stripped
        // form explicitly. Going through this form closes the gap that
        // makes "Chopin – Scherzo #1" render unqualified while
        // "Beethoven – Piano Sonata #32 in c, Op. 111" renders fully.
        var title = StripNicknameAndSubtitle(piece.DisplayTitle, piece);

        var node = new PickerPieceNode
        {
            DisplayTitle = piece.DisplayTitleShort,
            Catalog      = piece.Catalog,
            ToolTipText  = piece.ToolTipText,
            Composer     = composer,
            PieceTitle   = title,
        };

        var showNums = piece.EffectiveSubpiecesNumbered;

        if (piece.Versions is { Count: > 0 })
        {
            // Piece has alternative versions — expose "Original" + one node per version.
            var original = new PickerOriginalNode
            {
                DisplayTitle = "Original",
                Composer     = composer,
                PieceTitle   = title,
            };
            AppendSubpieceChildren(original.Children, piece.Subpieces, composer, title,
                                   parentPath: new List<string>(),
                                   showNums: showNums,
                                   versionDescription: null);
            node.Children.Add(original);

            foreach (var v in piece.Versions)
            {
                var desc = v.Description ?? "(untitled version)";
                var versionNode = new PickerVersionNode
                {
                    DisplayTitle       = "Version: " + desc,
                    Composer           = composer,
                    PieceTitle         = title,
                    VersionDescription = desc,
                };
                AppendSubpieceChildren(versionNode.Children, v.Subpieces, composer, title,
                                       parentPath: new List<string>(),
                                       showNums: showNums,
                                       versionDescription: desc);
                node.Children.Add(versionNode);
            }
        }
        else if (piece.Subpieces is { Count: > 0 })
        {
            AppendSubpieceChildren(node.Children, piece.Subpieces, composer, title,
                                   parentPath: new List<string>(),
                                   showNums: showNums,
                                   versionDescription: null);
        }

        return node;
    }

    private static void AppendSubpieceChildren(
        IList<PickerNode>    sink,
        List<CanonPiece>?    subpieces,
        string               composer,
        string               pieceTitle,
        List<string>         parentPath,
        bool                 showNums,
        string?              versionDescription)
    {
        if (subpieces is null || subpieces.Count == 0) return;

        foreach (var sp in subpieces)
        {
            var label       = sp.BuildSubpieceTitle(showNums);
            var currentPath = parentPath.Append(label).ToList();

            var child = new PickerSubpieceNode
            {
                DisplayTitle       = label,
                Composer           = composer,
                PieceTitle         = pieceTitle,
                SubpiecePath       = currentPath,
                VersionDescription = versionDescription,
            };

            if (sp.Subpieces is { Count: > 0 })
            {
                AppendSubpieceChildren(child.Children, sp.Subpieces, composer, pieceTitle,
                                       currentPath, showNums, versionDescription);
            }
            sink.Add(child);
        }
    }

    // ── Auto-expand search hits ──────────────────────────────────────────────

    /// <summary>
    /// Expands every ancestor of a node whose label contains the search string.
    /// Returns true if this subtree contains any hit.
    /// </summary>
    private static bool AutoExpandMatches(PickerNode node, string search)
    {
        var selfHit = node.DisplayTitle.Contains(search, StringComparison.OrdinalIgnoreCase);
        var childHit = false;
        foreach (var child in node.Children)
        {
            if (AutoExpandMatches(child, search))
                childHit = true;
        }
        if (childHit) node.IsExpanded = true;
        return selfHit || childHit;
    }

    // ── Selection handling ──────────────────────────────────────────────────

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SelectedRef = TryBuildSelectedRef(PieceTree.SelectedItem);
        OkButton.IsEnabled = SelectedRef != null;
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only commit when the double-click lands on a leaf — for nodes with
        // children, double-click should expand/collapse (the default WPF
        // behaviour), not close the dialog.
        if (SelectedRef != null
            && PieceTree.SelectedItem is PickerNode n
            && !n.HasChildren)
        {
            CommitAndClose();
            e.Handled = true;
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedRef != null)
        {
            CommitAndClose();
            e.Handled = true;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => CommitAndClose();

    private void CommitAndClose()
    {
        if (SelectedRef == null) return;
        DialogResult = true;
    }

    private static TrackPieceRef? TryBuildSelectedRef(object? node)
    {
        return node switch
        {
            PickerPieceNode p    => new TrackPieceRef
            {
                Composer   = p.Composer   ?? "",
                PieceTitle = p.PieceTitle ?? "",
            },
            PickerOriginalNode o => new TrackPieceRef
            {
                Composer   = o.Composer   ?? "",
                PieceTitle = o.PieceTitle ?? "",
            },
            PickerVersionNode v  => new TrackPieceRef
            {
                Composer           = v.Composer   ?? "",
                PieceTitle         = v.PieceTitle ?? "",
                VersionDescription = v.VersionDescription,
            },
            PickerSubpieceNode s => new TrackPieceRef
            {
                Composer           = s.Composer   ?? "",
                PieceTitle         = s.PieceTitle ?? "",
                SubpiecePath       = s.SubpiecePath is { Count: > 0 } ? s.SubpiecePath : null,
                VersionDescription = s.VersionDescription,
            },
            _ => null,
        };
    }

    // ── Expander click: toggle IsExpanded without selecting the row ──────────

    private void OnExpanderBorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PickerNode node && node.HasChildren)
        {
            node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }

    // ── Misc ─────────────────────────────────────────────────────────────────

    private static string? TryGetLifeSpan(CanonPiece p)
    {
        // The picker gets a flat piece list — we don't have the composer
        // record here, so we can't show birth/death years. Return null and
        // the XAML binding just shows nothing.
        _ = p;
        return null;
    }

    private static string FormatPieceCountLabel(int count) =>
        count == 1 ? "(1 piece)" : $"({count} pieces)";

    /// <summary>
    /// Strips the trailing <c>, Subtitle</c> and/or <c> "Nickname"</c> suffixes
    /// that <see cref="CanonPiece"/> appends inside BuildDisplayTitle. Mirrors
    /// <c>PieceReferenceIndex.StripNicknameAndSubtitle</c> — we can't call it
    /// directly because it's private, and plumbing a public overload in just
    /// for this one caller would add more surface area than it's worth.
    /// </summary>
    private static string StripNicknameAndSubtitle(string? displayTitle, CanonPiece p)
    {
        if (string.IsNullOrWhiteSpace(displayTitle)) return displayTitle ?? "";
        var result = displayTitle;

        if (!string.IsNullOrEmpty(p.Nickname))
        {
            var nick = $" \"{p.Nickname}\"";
            if (result.EndsWith(nick, StringComparison.Ordinal))
                result = result[..^nick.Length];
        }
        if (!string.IsNullOrEmpty(p.Subtitle))
        {
            var sub = $", {p.Subtitle}";
            if (result.EndsWith(sub, StringComparison.Ordinal))
                result = result[..^sub.Length];
        }
        return result;
    }
}

// ── Tree node types (picker-local) ───────────────────────────────────────────

/// <summary>Base for every PiecePickerWindow tree node.</summary>
public abstract class PickerNode : INotifyPropertyChanged
{
    public List<PickerNode> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;

    /// <summary>
    /// Label used for search-hit detection and for the XAML bindings on the
    /// piece, version, and subpiece rows. (The composer row binds Name
    /// directly.) Each derived type sets this in its object initializer.
    /// </summary>
    public string DisplayTitle { get; set; } = "";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PickerComposerNode : PickerNode
{
    public string  Name            { get; init; } = "";
    public string? LifeSpan        { get; init; }
    public string  PieceCountLabel { get; init; } = "";
}

public sealed class PickerPieceNode : PickerNode
{
    public string? Catalog     { get; init; }
    public string? ToolTipText { get; init; }
    public string? Composer    { get; init; }
    public string? PieceTitle  { get; init; }
}

public sealed class PickerOriginalNode : PickerNode
{
    public string? Composer   { get; init; }
    public string? PieceTitle { get; init; }
}

public sealed class PickerVersionNode : PickerNode
{
    public string? Composer           { get; init; }
    public string? PieceTitle         { get; init; }
    public string? VersionDescription { get; init; }
}

public sealed class PickerSubpieceNode : PickerNode
{
    public string?       Composer           { get; init; }
    public string?       PieceTitle         { get; init; }
    public List<string>? SubpiecePath       { get; init; }
    public string?       VersionDescription { get; init; }
}
