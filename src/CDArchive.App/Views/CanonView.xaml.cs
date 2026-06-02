using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.Helpers;
using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CDArchive.App.Views;

public partial class CanonView : UserControl
{
    // ── Composer sort state ──────────────────────────────────────────────────
    // Migrated to CanonViewModel.ComposerSortColumn (H2 slice 2). The XAML
    // SelectedValue-binds it to the Sort Composers dropdown; the View reacts
    // via OnViewModelPropertyChanged to call ApplySortedFilter on change.

    // ── Piece sort state ─────────────────────────────────────────────────────
    // Migrated to CanonViewModel.PieceSortColumn (H2 slice 3). The XAML
    // SelectedValue-binds it to the Sort Pieces dropdown; the View reacts
    // via OnViewModelPropertyChanged to call ApplySortedFilter on change.

    // ── Current selection ────────────────────────────────────────────────────

    private CanonComposer? _activeComposer;
    private CanonPiece?    _activePiece;

    // ── Context-menu target (set on right-click, independent of selection) ────
    // Tracking this separately avoids calling tvi.IsSelected = true inside any
    // mouse-button handler.  Any selection change during mouse-button routing
    // triggers a SelectedItemChanged→layout cascade that corrupts expander state
    // on unrelated rows, so we never touch IsSelected from a mouse handler.

    private object?       _ctxTarget;   // data item that was right-clicked
    private TreeViewItem? _ctxTvi;      // its container

    // ── Auto-refresh suppression ─────────────────────────────────────────────
    // VM commands fire DataMutated synchronously before their save's await;
    // the View's OnVmDataMutated runs UpdatePieceCounts + ApplySortedFilter
    // and sets _suppressAutoRefresh = true so the post-save IsLoading=false
    // transition doesn't trigger a redundant second rebuild via
    // OnViewModelPropertyChanged.

    private bool _suppressAutoRefresh;

    // ── Expansion state (all four hashsets + walks) ──────────────────────────
    // Encapsulated in CanonTreeExpansionState (H2 slice 1). The View calls
    // _expansionState.Save(ComposerTree) before any rebuild and
    // _expansionState.Restore(ComposerTree, nodes) after.

    private readonly CanonTreeExpansionState _expansionState = new();

    // ── Constructor ──────────────────────────────────────────────────────────

    public CanonView()
    {
        InitializeComponent();
    }

    // ── Initial data load ────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;

        // CanonView is now a permanent element (never recreated), so Loaded fires exactly once.
        // Subscribe for all future reloads (Refresh button, NavigateToCanon, etc.).
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        // H36 retirement: the new Delete/Approve/Reject RelayCommands raise
        // DataMutated after the in-memory mutation but before the save's
        // await. We subscribe here to refresh badge counts + rebuild the
        // tree synchronously — preserving the pre-fix mutate → rebuild →
        // suppress → save order (which avoids a WPF rendering glitch where
        // TreeViewItem expander triangles end up partially-stale when a
        // rebuild runs after the save's await).
        vm.DataMutated -= OnVmDataMutated;
        vm.DataMutated += OnVmDataMutated;

        // When the album↔piece cross-reference is rebuilt (e.g. after saving an album),
        // our hit-count badges are stale until the tree re-renders. Force a refresh.
        if (PieceReferenceIndex.Current is { } idx)
        {
            idx.Indexed -= OnIndexRebuilt;
            idx.Indexed += OnIndexRebuilt;
        }

        // Initial data load.
        await vm.LoadDataCommand.ExecuteAsync(null);
        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
    }

    /// <summary>
    /// Subscribed to <see cref="CanonViewModel.DataMutated"/> — fires
    /// synchronously between the command's mutation and its save's await.
    /// Refreshes badge counts + rebuilds the tree, then sets the suppress
    /// flag so the post-save reload's tree-rebuild is a no-op.
    /// </summary>
    private void OnVmDataMutated()
    {
        if (DataContext is not CanonViewModel vm) return;
        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
        _suppressAutoRefresh = true;
    }

    private void OnIndexRebuilt(object? sender, EventArgs e)
    {
        // Converters don't re-fire when a static index changes; nudge the tree.
        // Items.Refresh() regenerates every TreeViewItem container, which wipes
        // expansion state — so save and restore it around the refresh. Without
        // this, opening the Albums screen (which triggers a rebuild) would
        // collapse the Canon tree and lose the user's current context.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _expansionState.Save(ComposerTree);
            ComposerTree.Items.Refresh();
            if (ComposerTree.ItemsSource is IEnumerable<ComposerTreeNode> nodes)
                _expansionState.Restore(ComposerTree, nodes);
        }));
    }

    /// <summary>
    /// Rebuilds the tree when a reload triggered externally (e.g. Refresh button) finishes.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CanonViewModel vm) return;

        // Sort + filter + Show changes all trigger a tree rebuild. H2 slices
        // 2 and 3 migrated every toolbar control into VM observable properties
        // — the View no longer carries event handlers for them; WPF property
        // change notifications bubble through here instead.
        if (e.PropertyName is nameof(CanonViewModel.ComposerProvisionalFilter)
                           or nameof(CanonViewModel.PieceProvisionalFilter)
                           or nameof(CanonViewModel.ComposerSortColumn)
                           or nameof(CanonViewModel.PieceSortColumn)
                           or nameof(CanonViewModel.ComposerFilter))
        {
            ApplySortedFilter(vm);
            return;
        }

        if (e.PropertyName != nameof(CanonViewModel.IsLoading)) return;
        if (vm.IsLoading) return;   // only act on the transition to false

        // VM commands raise DataMutated before their save's await; the
        // OnVmDataMutated handler ran ApplySortedFilter synchronously and set
        // _suppressAutoRefresh, so the post-save IsLoading=false transition
        // here is a redundant second rebuild to skip.
        if (_suppressAutoRefresh) { _suppressAutoRefresh = false; return; }

        UpdatePieceCounts(vm);
        ApplySortedFilter(vm);
    }

    // ── Toolbar handlers ─────────────────────────────────────────────────────
    // Every toolbar control (Composer Sort / Composer Filter / Composer Show /
    // Piece Sort / Piece Show) now drives directly off VM observable
    // properties (H2 slices 2 + 3). XAML bindings push user changes onto the
    // VM; the View reacts through OnViewModelPropertyChanged above to rebuild
    // the tree. No more SelectionChanged / TextChanged handlers here.

    // ── Tree: build / refresh ─────────────────────────────────────────────────

    /// <summary>
    /// Saves all expansion state, rebuilds the composer tree with current filter
    /// and sort order, then restores expansion state.
    /// </summary>
    private void ApplySortedFilter(CanonViewModel vm)
    {
        _expansionState.Save(ComposerTree);

        var filter = vm.ComposerFilter.Trim();
        IEnumerable<CanonComposer> filtered = string.IsNullOrEmpty(filter)
            ? vm.Composers
            : vm.Composers.Where(c =>
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.SortName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        filtered = vm.ComposerProvisionalFilter switch
        {
            ProvisionalFilter.Provisional => filtered.Where(c => c.IsProvisional),
            ProvisionalFilter.Accepted    => filtered.Where(c => !c.IsProvisional),
            _                             => filtered,
        };

        filtered = vm.ApplyComposerSort(filtered);

        // Cross-composer detection runs once over the full piece tree per
        // tree-rebuild, then we hand each composer's slice to their node.
        // The walk is O(pieces) and the dictionary lookup is O(1), so this is
        // cheaper than redoing the scan inside GetSortedPieces per composer.
        var crossComposerByName = CrossComposerSubpieceFinder.Find(vm.Pieces);
        var sortField           = PieceSorting.ParseField(vm.PieceSortColumn);
        var idx                 = PieceReferenceIndex.Current;
        // Adapter that lets the UI-free PieceSorting helper query album-hit
        // counts without depending on PieceReferenceIndex directly.
        Func<object, int>? recordingCount = idx is null ? null : o => o switch
        {
            CanonPiece p                  => idx.CountForPiece(p),
            CrossComposerSubpieceNode ccn => idx.CountForPiece(ccn.Subpiece),
            _                             => 0,
        };

        var nodes = filtered
            .Select(c =>
            {
                var node = new ComposerTreeNode(c, GetSortedPieces(vm, c.Name));
                node.ContributedGroups = ContributedWorksFinder.FindContributedGroups(vm.Pieces, c.Name);
                if (crossComposerByName.TryGetValue(c.Name, out var ccn))
                    node.CrossComposerNodes = ccn;
                // Apply the user's selected sort to the merged list. The
                // node's constructor pre-built a catalogue-sorted view; this
                // call replaces it with one matching the combo selection.
                node.RebuildAllItems(sortField, recordingCount);
                return node;
            })
            .ToList();

        ComposerTree.ItemsSource = nodes;
        _expansionState.Restore(ComposerTree, nodes);
    }

    /// <summary>
    /// Returns the pieces owned by the given composer. The actual sort
    /// (Catalogue / Title / Category / Year) is applied uniformly via
    /// <see cref="PieceSorting.Sort"/> in <see cref="ApplySortedFilter"/>,
    /// where it's combined with cross-composer-credit nodes — so this method
    /// is a plain composer filter with no per-list sort.
    /// </summary>
    private static List<CanonPiece> GetSortedPieces(CanonViewModel vm, string composerName)
    {
        IEnumerable<CanonPiece> pieces = vm.Pieces.Where(p =>
            string.Equals(p.Composer, composerName, StringComparison.OrdinalIgnoreCase));

        pieces = vm.PieceProvisionalFilter switch
        {
            ProvisionalFilter.Provisional => pieces.Where(p => p.IsProvisional),
            ProvisionalFilter.Accepted    => pieces.Where(p => !p.IsProvisional),
            _                             => pieces,
        };

        return pieces.ToList();
    }

    // Expansion-state save/restore + the recursive WPF walks (composer /
    // piece / cross-composer / contributed-group / subpiece-version levels)
    // moved to CanonTreeExpansionState (H2 slice 1). The View calls Save()
    // before any rebuild and Restore(nodes) after — see _expansionState above.

    // ── Tree: selection ───────────────────────────────────────────────────────

    private void OnComposerTreeSelectionChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not CanonViewModel vm) return;

        if (e.NewValue is ComposerTreeNode node)
        {
            _activeComposer = node.Composer;
            _activePiece    = null;
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = false;
        }
        else if (e.NewValue is CanonPiece piece)
        {
            _activeComposer = vm.Composers.FirstOrDefault(c =>
                string.Equals(c.Name, piece.Composer, StringComparison.OrdinalIgnoreCase));
            _activePiece    = piece;
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = true;
        }
        else if (e.NewValue is PieceOriginalNode origNode)
        {
            _activeComposer = vm.Composers.FirstOrDefault(c =>
                string.Equals(c.Name, origNode.Piece.Composer, StringComparison.OrdinalIgnoreCase));
            _activePiece    = origNode.Piece;
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = true;
        }
        else if (e.NewValue is VersionDisplayNode versionNode)
        {
            // Track the parent piece so New/Delete Piece still work sensibly.
            if (versionNode.ParentPiece != null)
            {
                _activePiece = versionNode.ParentPiece;
                _activeComposer = vm.Composers.FirstOrDefault(c =>
                    string.Equals(c.Name, versionNode.ParentPiece.Composer, StringComparison.OrdinalIgnoreCase));
            }
            NewPieceButton.IsEnabled    = true;
            DeletePieceButton.IsEnabled = true;
        }
        else if (e.NewValue is ContributedRoleGroupNode)
        {
            // Group header — no piece/composer change, disable New/Delete
            NewPieceButton.IsEnabled    = false;
            DeletePieceButton.IsEnabled = false;
        }
        else if (e.NewValue is ContributedPieceNode contribNode)
        {
            _activePiece = contribNode.Piece;
            _activeComposer = vm.Composers.FirstOrDefault(c =>
                string.Equals(c.Name, contribNode.Piece.Composer, StringComparison.OrdinalIgnoreCase));
            NewPieceButton.IsEnabled    = false;
            DeletePieceButton.IsEnabled = false;
        }
        else if (e.NewValue is CrossComposerSubpieceNode ccn)
        {
            // In-place preview: the right-hand pane (which binds to
            // SelectedPiece) shows the cross-credited subpiece's details. We
            // also lift _activeComposer to the subpiece's composer, matching
            // the New/Delete button behaviour for the contributing composer.
            _activePiece = ccn.Subpiece;
            _activeComposer = vm.Composers.FirstOrDefault(c =>
                string.Equals(c.Name, ccn.Subpiece.Composer, StringComparison.OrdinalIgnoreCase));
            // New/Delete are scoped to a top-level piece. The cross-credited
            // subpiece lives under (Various)'s tree, not this composer's,
            // so we disable them here to avoid surprises — the user can still
            // edit the subpiece via double-click or the Edit context menu.
            NewPieceButton.IsEnabled    = false;
            DeletePieceButton.IsEnabled = false;
        }
        else if (e.NewValue is SubpieceDisplayNode)
        {
            // Keep _activeComposer / _activePiece and button state from the
            // most recently selected piece — no change needed.
        }
        else
        {
            _activeComposer = null;
            _activePiece    = null;
            NewPieceButton.IsEnabled    = false;
            DeletePieceButton.IsEnabled = false;
        }
    }

    // ── Expander arrow click ──────────────────────────────────────────────────
    // The expand arrows in the DataTemplates are plain Path elements with a
    // one-way DataTrigger (no TwoWay binding).  Clicking the arrow's hit area
    // (the Border / Grid it sits in) calls this handler to toggle IsExpanded.
    // e.Handled is NOT set so the click also propagates to the TreeViewItem's
    // normal selection machinery.

    private void OnExpanderBorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;   // ignore the second tap of a double-click
        var hit = sender as DependencyObject;
        while (hit != null && hit is not TreeViewItem)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        if (hit is TreeViewItem tvi && tvi.HasItems)
        {
            // Pin horizontal scroll across the expand: child realisation +
            // implicit select-on-click both trigger BringIntoView, which can
            // scroll right to fit wide children.  Capture now, restore after
            // layout settles.  See PinHorizontalScrollAcross.
            var scrollViewer = FindTreeScrollViewer();
            double offset = scrollViewer?.HorizontalOffset ?? 0;
            tvi.IsExpanded = !tvi.IsExpanded;
            PinHorizontalScrollAcross(scrollViewer, offset);
        }
        // Do NOT set e.Handled — let the click also select the item normally.
    }

    private void PinHorizontalScrollAcross(ScrollViewer? scrollViewer, double offset)
    {
        if (scrollViewer == null) return;
        // BringIntoView and child realisation both queue scroll work that
        // runs after the current dispatcher pass.  Pin immediately AND at
        // Loaded priority to defeat whichever path actually moves the offset.
        if (scrollViewer.HorizontalOffset != offset)
            scrollViewer.ScrollToHorizontalOffset(offset);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (scrollViewer.HorizontalOffset != offset)
                scrollViewer.ScrollToHorizontalOffset(offset);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Suppress horizontal auto-scroll ──────────────────────────────────────
    // WPF raises RequestBringIntoView when a TreeViewItem is selected/focused
    // (including the implicit select-on-click that fires when the user clicks
    // an expander arrow).  Default behaviour scrolls the ScrollViewer to fit
    // the item's full bounding rect, which pushes the composer's expander off
    // the left of the viewport when a piece with wide content is expanded.
    // We re-raise with a degenerate (Width=0) vertical-only rect, then pin
    // the horizontal offset back to where the user had it — so vertical
    // bring-into-view (keyboard nav, expand) still works, but horizontal
    // scroll position is preserved.

    private bool _suppressBringIntoView;   // prevents the re-raised call from looping

    private void OnTreeRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_suppressBringIntoView) return;
        if (e.TargetObject is not FrameworkElement target) return;

        e.Handled = true;   // cancel the default horizontal+vertical scroll

        var rect = e.TargetRect.IsEmpty ? new Rect(target.RenderSize) : e.TargetRect;
        var scrollViewer = FindTreeScrollViewer();
        double horizontalOffset = scrollViewer?.HorizontalOffset ?? 0;

        _suppressBringIntoView = true;
        try   { target.BringIntoView(new Rect(0, rect.Y, 0, rect.Height)); }
        finally { _suppressBringIntoView = false; }

        PinHorizontalScrollAcross(scrollViewer, horizontalOffset);
    }

    private ScrollViewer? FindTreeScrollViewer()
    {
        if (ComposerTree.Template?.FindName("PART_ContentHost", ComposerTree) is ScrollViewer sv)
            return sv;
        // Template has no x:Name; walk the visual tree.
        DependencyObject? node = ComposerTree;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(node);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(current, i);
                if (child is ScrollViewer found) return found;
                queue.Enqueue(child);
            }
        }
        return null;
    }

    // ── Double-click dispatcher ───────────────────────────────────────────────

    private async void OnTreeItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || !item.IsSelected) return;
        e.Handled = true;
        await EditSelectedItemAsync(item.DataContext);
    }

    // ── Enter key ─────────────────────────────────────────────────────────────

    private async void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var selected = ComposerTree.SelectedItem;
        if (selected == null) return;
        e.Handled = true;
        await EditSelectedItemAsync(selected);
    }

    // ── Shared edit dispatcher ────────────────────────────────────────────────

    private async Task EditSelectedItemAsync(object? item)
    {
        switch (item)
        {
            case ComposerTreeNode node:
                await EditComposerAsync(node.Composer);
                break;
            case ContributedPieceNode contribNode:
                await EditPieceAsync(contribNode.Piece);
                break;
            case ContributedRoleGroupNode:
                break;   // group header — no edit
            case CrossComposerSubpieceNode ccn:
                // Edit the cross-credited subpiece in place, scoped to its
                // immediate parent (the last entry in AncestorPath).
                await EditSubpieceAsync(ccn.Subpiece, ccn.AncestorPath[^1]);
                break;
            case CanonPiece piece:
                await EditPieceAsync(piece);
                break;
            case PieceOriginalNode origNode:
                await EditPieceAsync(origNode.Piece);
                break;
            case VersionDisplayNode versionNode:
                await EditVersionAsync(versionNode);
                break;
            case SubpieceDisplayNode subNode:
                await EditSubpieceAsync(subNode.Piece, subNode.ParentPiece);
                break;
        }
    }

    // ── Context menu: record right-clicked item (never touch IsSelected) ────────

    private void OnTreeMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not TreeViewItem)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);

        if (hit is TreeViewItem tvi)
        {
            _ctxTarget = tvi.DataContext;
            _ctxTvi    = tvi;
        }
        else
        {
            _ctxTarget = null;
            _ctxTvi    = null;
        }
    }

    // ── Context menu: enable/disable items before showing ────────────────────

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Suppress menu if right-click landed on empty space
        if (_ctxTarget == null)
        {
            e.Handled = true;
            return;
        }

        bool canEdit = _ctxTarget is not ContributedRoleGroupNode;
        bool hasChildren = _ctxTarget switch
        {
            ComposerTreeNode n              => n.AllItems.Count > 0,
            CanonPiece p                    => p.HasTreeChildren,
            SubpieceDisplayNode s           => s.HasChildren,
            PieceOriginalNode o             => o.HasChildren,
            VersionDisplayNode v            => v.HasSubpieces,
            ContributedRoleGroupNode g      => g.Pieces.Count > 0,
            ContributedPieceNode cp         => cp.HasChildren,
            CrossComposerSubpieceNode ccn   => ccn.Subpiece.HasTreeChildren,
            _                               => false,
        };

        bool isProvisional = _ctxTarget switch
        {
            ComposerTreeNode n => n.Composer.IsProvisional,
            CanonPiece p       => p.IsProvisional,
            _                  => false,
        };

        CtxEdit.IsEnabled        = canEdit;
        CtxExpandAll.IsEnabled   = hasChildren;
        CtxCollapseAll.IsEnabled = hasChildren;
        CtxShowAlbums.IsEnabled  = HitCountForTarget(_ctxTarget) > 0;
        CtxApprove.IsEnabled     = isProvisional;
        CtxReject.IsEnabled      = isProvisional;
    }

    private static int HitCountForTarget(object? target)
    {
        var idx = PieceReferenceIndex.Current;
        if (idx is null || target is null) return 0;
        return target switch
        {
            ComposerTreeNode n              => idx.CountForComposer(n.Composer.Name),
            CanonComposer c                 => idx.CountForComposer(c.Name),
            PieceOriginalNode pon           => idx.CountForOriginal(pon.Piece),
            VersionDisplayNode vdn          => idx.CountForVersion(vdn.Version),
            SubpieceDisplayNode sdn         => idx.CountForPiece(sdn.Piece),
            ContributedPieceNode cpn        => idx.CountForPiece(cpn.Piece),
            ContributedRoleGroupNode g      => idx.CountForPieces(g.Pieces.Select(p => p.Piece)),
            CrossComposerSubpieceNode ccn   => idx.CountForPiece(ccn.Subpiece),
            CanonPiece p                    => idx.CountForPiece(p),
            _ => 0
        };
    }

    private static (string Header, IReadOnlyList<PieceAlbumHit> Hits) ResolveHits(object target)
    {
        var idx = PieceReferenceIndex.Current!;
        return target switch
        {
            ComposerTreeNode n              => ($"Albums referencing works by {n.Composer.Name}",                      idx.HitsForComposer(n.Composer.Name)),
            CanonComposer c                 => ($"Albums referencing works by {c.Name}",                               idx.HitsForComposer(c.Name)),
            PieceOriginalNode pon           => ($"Albums referencing “{pon.Piece.DisplayTitleShort}” (Original)",      idx.HitsForOriginal(pon.Piece)),
            VersionDisplayNode vdn          => ($"Albums referencing {vdn.DisplayTitle}",                              idx.HitsForVersion(vdn.Version)),
            SubpieceDisplayNode sdn         => ($"Albums referencing “{sdn.DisplayTitle}”",                            idx.HitsForPiece(sdn.Piece)),
            ContributedPieceNode cpn        => ($"Albums referencing “{cpn.Piece.DisplayTitleShort}”",                 idx.HitsForPiece(cpn.Piece)),
            ContributedRoleGroupNode g      => ($"Albums referencing {g.DisplayTitle}",                                idx.HitsForPieces(g.Pieces.Select(p => p.Piece))),
            CrossComposerSubpieceNode ccn   => ($"Albums referencing “{ccn.DisplayTitle}”",                            idx.HitsForPiece(ccn.Subpiece)),
            CanonPiece p                    => ($"Albums referencing “{p.DisplayTitleShort}”",                         idx.HitsForPiece(p)),
            _                               => ("", Array.Empty<PieceAlbumHit>()),
        };
    }

    private async void OnContextShowAlbums(object sender, RoutedEventArgs e)
    {
        if (_ctxTarget is null) return;
        var (header, hits) = ResolveHits(_ctxTarget);
        if (hits.Count == 0) return;

        var albumsVm = App.ServiceProvider.GetRequiredService<AlbumsViewModel>();
        var dlg = new PieceAlbumsWindow(header, hits, albumsVm.Player)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true) return;

        // Play branch handled playback itself — only open the editor for Open Album.
        if (dlg.PlayRequested) return;
        if (dlg.SelectedAlbum is not CanonAlbum album) return;

        await OpenAlbumEditorAsync(album);
    }

    /// <summary>
    /// Opens the standard album editor for <paramref name="album"/>, mirroring the
    /// flow used by <see cref="AlbumsView"/> so saves persist back to storage and
    /// the Canon-side cross-reference index is refreshed.
    /// </summary>
    private async Task OpenAlbumEditorAsync(CanonAlbum album)
    {
        var albumsVm = App.ServiceProvider.GetRequiredService<AlbumsViewModel>();
        // Ensure we're editing the live in-memory instance (not a stale copy from the index).
        if (albumsVm.AllAlbums.Count == 0) await albumsVm.LoadDataCommand.ExecuteAsync(null);
        var liveAlbum = albumsVm.AllAlbums.FirstOrDefault(a => ReferenceEquals(a, album)) ?? album;

        var (pieces, pickLists) = await albumsVm.LoadEditorDataAsync();
        var dlg = new AlbumEditorWindow(pickLists, pieces, albumsVm.Player, liveAlbum)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true || dlg.Result is not CanonAlbum result) return;

        var idx = albumsVm.AllAlbums.IndexOf(liveAlbum);
        if (idx >= 0) albumsVm.AllAlbums[idx] = result;
        else          albumsVm.AllAlbums.Add(result);
        albumsVm.ApplyFilter();
        await albumsVm.SaveAsync(pickLists);
    }

    // ── Context menu: handlers ────────────────────────────────────────────────

    private async void OnContextEdit(object sender, RoutedEventArgs e) =>
        await EditSelectedItemAsync(_ctxTarget);

    private void OnContextExpandAll(object sender, RoutedEventArgs e)
    {
        if (_ctxTvi == null) return;
        _ctxTvi.IsExpanded = true;
        SetExpandedRecursive(_ctxTvi, expand: true);
    }

    private void OnContextCollapseAll(object sender, RoutedEventArgs e)
    {
        if (_ctxTvi == null) return;
        SetExpandedRecursive(_ctxTvi, expand: false);
        _ctxTvi.IsExpanded = false;
    }

    // H36 retirement: data work + confirmation + cascade + status lives on
    // CanonViewModel.ApproveCanonItemCommand / RejectCanonItemCommand. The
    // View's click handlers are thin shims that forward _ctxTarget — the
    // right-clicked tree item the context-menu logic stored. Tree rebuild
    // runs via the DataMutated event subscription (OnVmDataMutated).
    private async void OnContextApprove(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        await vm.ApproveCanonItemCommand.ExecuteAsync(_ctxTarget);
    }

    private async void OnContextReject(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        await vm.RejectCanonItemCommand.ExecuteAsync(_ctxTarget);
    }

    // ── Expand / collapse helpers ─────────────────────────────────────────────

    private static void SetExpandedRecursive(TreeViewItem parent, bool expand)
    {
        parent.UpdateLayout();   // ensure child containers are generated
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                    is not TreeViewItem child) continue;
            child.IsExpanded = expand;
            SetExpandedRecursive(child, expand);
        }
    }

    // ── Edit: composer ───────────────────────────────────────────────────────

    // H2 slice 5: post-dialog orchestration for EditComposer / EditPiece /
    // EditVersion / EditSubpiece migrated to CanonViewModel.Complete*EditAsync.
    // The View handlers now only own dialog construction (which needs Owner =
    // Window.GetWindow(this), composerNames/composerCatalogs/ancestorRoles
    // computed from VM state, and the expansion-state guard around ShowDialog).
    // The post-OK orchestration (rename propagation + catalog reorder + save +
    // album-ref rename) lives on the VM.

    private async Task EditComposerAsync(CanonComposer composer)
    {
        if (DataContext is not CanonViewModel vm) return;

        // Snapshot captured before the dialog mutates the composer — the VM
        // uses it post-dialog to detect rename + prefix changes.
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        var window = new ComposerEditorWindow(vm.PickLists, composer)
        {
            Owner = Window.GetWindow(this)
        };

        if (ShowDialogWithExpansionGuard(window) != true) return;
        await vm.CompleteEditComposerAsync(composer, snapshot);
    }

    // ── Edit: piece ──────────────────────────────────────────────────────────

    private async Task EditPieceAsync(CanonPiece piece)
    {
        if (DataContext is not CanonViewModel vm) return;

        // Snapshot the piece's path structure before editing so the VM can
        // diff against the post-dialog state to detect title renames.
        var snapshot = PieceRefPathDiffer.Snapshot(piece);

        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var window = new PieceEditorWindow(vm.PickLists, piece.Composer ?? "", piece, composerNames,
            composerCatalogs: composerCatalogs)
        {
            Owner = Window.GetWindow(this)
        };

        if (ShowDialogWithExpansionGuard(window) == true)
            await vm.CompleteEditPieceAsync(piece, snapshot);
    }

    // ── Edit: version (direct from tree) ────────────────────────────────

    private async Task EditVersionAsync(VersionDisplayNode versionNode)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (versionNode.ParentPiece is not { } parentPiece) return;

        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var window = new PieceEditorWindow(
            vm.PickLists,
            versionNode.Version,
            showSubpieceNumbers: parentPiece.EffectiveSubpiecesNumbered,
            composerNames: composerNames,
            inheritedComposer: parentPiece.Composer,
            inheritedComposers: parentPiece.Composers,
            composerCatalogs: composerCatalogs)
        {
            Owner = Window.GetWindow(this)
        };

        if (ShowDialogWithExpansionGuard(window) == true)
            await vm.CompleteEditVersionAsync(versionNode);
    }

    // ── Edit: subpiece ───────────────────────────────────────────────────────

    private async Task EditSubpieceAsync(CanonPiece subpiece, CanonPiece? parentPiece = null)
    {
        if (DataContext is not CanonViewModel vm) return;

        // Prefer the authoritative parent reference from the node; fall back to _activePiece.
        var parent = parentPiece ?? _activePiece;

        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var ancestorRoles = ParseAncestorRoles(parent, subpiece);
        var window = new PieceEditorWindow(
            vm.PickLists, subpiece.Composer ?? "", subpiece, composerNames, PieceEditorMode.Subpiece,
            inheritedComposer: parent?.Composer,
            inheritedComposers: parent?.Composers,
            composerCatalogs: composerCatalogs,
            ancestorRoles: ancestorRoles)
        {
            Owner = Window.GetWindow(this)
        };

        if (ShowDialogWithExpansionGuard(window) == true)
            await vm.CompleteEditSubpieceAsync(subpiece);
    }

    // ── Toolbar: New Composer ────────────────────────────────────────────────
    // H2 slice 4: post-dialog orchestration lives on
    // CanonViewModel.NewComposerCommand. The View opens the modal and forwards
    // the dialog-built composer; tree-rebuild + status messages happen via
    // the DataMutated event subscription (OnVmDataMutated).
    private async void OnNewComposerClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;

        var window = new ComposerEditorWindow(vm.PickLists)
        {
            Owner = Window.GetWindow(this)
        };

        if (ShowDialogWithExpansionGuard(window) == true)
            await vm.NewComposerCommand.ExecuteAsync(window.Composer);
    }

    // ── Toolbar: Delete Composer ─────────────────────────────────────────────
    // H36 retirement: data work + confirmation + save lives on
    // CanonViewModel.DeleteComposerCommand. The View's click handler is a
    // thin shim that forwards the active selection — view-side state that the
    // VM can't see directly. UpdatePieceCounts + ApplySortedFilter run via
    // the DataMutated event subscription (OnVmDataMutated).
    private async void OnDeleteComposerClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (_activeComposer == null) return;
        await vm.DeleteComposerCommand.ExecuteAsync(_activeComposer);
        _activeComposer = null;
    }

    // ── Toolbar: New Piece ───────────────────────────────────────────────────
    // H2 slice 4: see New Composer above.
    private async void OnNewPieceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;

        var composerName = _activeComposer?.Name ?? _activePiece?.Composer ?? "";
        var composerNames = vm.Composers.Select(c => c.Name).ToList();
        var composerCatalogs = BuildComposerCatalogDict(vm);
        var window = new PieceEditorWindow(vm.PickLists, composerName, null, composerNames,
            composerCatalogs: composerCatalogs)
        {
            Owner = Window.GetWindow(this)
        };

        if (ShowDialogWithExpansionGuard(window) == true)
            await vm.NewPieceCommand.ExecuteAsync(window.Piece);
    }

    // ── Toolbar: Delete Piece ────────────────────────────────────────────────
    // H36 retirement: see DeleteComposer above.
    private async void OnDeletePieceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CanonViewModel vm) return;
        if (_activePiece == null) return;
        await vm.DeletePieceCommand.ExecuteAsync(_activePiece);
        _activePiece = null;
        DeletePieceButton.IsEnabled = false;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a case-insensitive dictionary from composer name to their permitted catalogue
    /// prefixes. Composers with no restrictions are omitted (callers treat a missing key as
    /// "no restriction — show all prefixes").
    /// </summary>
    /// <summary>
    /// Parses the roles from a parent piece into a flat list suitable for passing
    /// as <c>ancestorRoles</c> to a subpiece editor. Returns null if the piece has
    /// no roles defined.
    /// </summary>
    /// <summary>
    /// Returns the ancestor roles visible to <paramref name="subpieceContext"/>.
    /// Checks the top-level piece's roles first; if absent, walks the piece's versions
    /// to find whichever version contains the subpiece (by reference) and returns
    /// that version's roles instead.
    /// </summary>
    private static IReadOnlyList<RoleEntry>? ParseAncestorRoles(
        CanonPiece? piece, CanonPiece? subpieceContext = null)
    {
        if (piece == null) return null;

        if (piece.Roles is { } roles)
        {
            var parsed = RoleEntry.ParseRoles(roles);
            if (parsed.Count > 0) return parsed;
        }

        // Fall back to whichever version contains the subpiece
        if (subpieceContext != null && piece.Versions != null)
        {
            foreach (var v in piece.Versions)
            {
                if (v.Roles != null && SubpieceExistsInTree(v.Subpieces, subpieceContext))
                {
                    var parsed = RoleEntry.ParseRoles(v.Roles.Value);
                    if (parsed.Count > 0) return parsed;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="target"/> exists anywhere in the subpiece tree
    /// rooted at <paramref name="subpieces"/> (reference equality, recursive).
    /// </summary>
    private static bool SubpieceExistsInTree(List<CanonPiece>? subpieces, CanonPiece target)
    {
        if (subpieces == null) return false;
        foreach (var sp in subpieces)
        {
            if (ReferenceEquals(sp, target)) return true;
            if (SubpieceExistsInTree(sp.Subpieces, target)) return true;
        }
        return false;
    }

    /// <summary>
    /// Shows a dialog window while preserving the tree's expansion state.
    /// <para>
    /// <c>ShowDialog()</c> calls Win32 <c>EnableWindow(ownerHandle, false/true)</c>,
    /// which propagates <c>IsEnabled = false → true</c> through the entire visual tree.
    /// WPF's coercion during that cycle can clear the local value of
    /// <c>TreeViewItem.IsExpanded</c>, letting the Style's default <c>Value="False"</c>
    /// setter win — collapsing every node silently.  Saving/restoring expansion state
    /// around the dialog call prevents this.
    /// </para>
    /// Expansion is restored unconditionally (cancel <i>and</i> save paths) so the tree
    /// never flickers even when the user dismisses the dialog.
    /// </summary>
    private bool? ShowDialogWithExpansionGuard(Window dialog)
    {
        _expansionState.Save(ComposerTree);
        var result = dialog.ShowDialog();

        // Restore into the current tree (pre-rebuild).  If the caller then calls
        // ApplySortedFilter it will save this state again, rebuild, and restore once more.
        if (ComposerTree.ItemsSource is IEnumerable<ComposerTreeNode> nodes)
            _expansionState.Restore(ComposerTree, nodes);

        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildComposerCatalogDict(CanonViewModel vm)
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var composer in vm.Composers)
        {
            if (composer.CatalogPrefixes is { Count: > 0 })
                dict[composer.Name] = composer.CatalogPrefixes;
        }
        return dict;
    }

    private static void UpdatePieceCounts(CanonViewModel vm)
    {
        var ownCounts = vm.Pieces
            .Where(p => !string.IsNullOrEmpty(p.Composer))
            .GroupBy(p => p.Composer!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // For each piece, collect all Other Contributor names from the full hierarchy,
        // then credit each contributor with +1 (excluding the piece's primary composer).
        var contributedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in vm.Pieces)
        {
            var contributors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectAllContributorNames(piece, contributors);
            contributors.Remove(piece.Composer ?? "");   // don't double-count own pieces

            foreach (var name in contributors)
            {
                contributedCounts.TryGetValue(name, out var c);
                contributedCounts[name] = c + 1;
            }
        }

        // Cross-composer subpieces (collaborative-work parts, e.g. Ravel's
        // Fanfare from L'éventail de Jeanne) — count one per cross-credit
        // node, matching what appears under each composer's tree entry.
        // Reusing CrossComposerSubpieceFinder keeps "what's counted" and
        // "what's surfaced in the tree" in lockstep.
        var crossComposerCounts = CrossComposerSubpieceFinder.Find(vm.Pieces)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var composer in vm.Composers)
        {
            ownCounts.TryGetValue(composer.Name, out var own);
            contributedCounts.TryGetValue(composer.Name, out var contrib);
            crossComposerCounts.TryGetValue(composer.Name, out var crossCredit);
            composer.PieceCount = own + contrib + crossCredit;
        }
    }

    /// <summary>
    /// Recursively collects all Other Contributor names (non-null role) from a piece,
    /// its versions, and all subpieces at every depth.
    /// </summary>
    private static void CollectAllContributorNames(CanonPiece piece, HashSet<string> names)
    {
        if (piece.Composers != null)
            foreach (var c in piece.Composers)
                if (!string.IsNullOrEmpty(c.Role)) names.Add(c.Name);

        if (piece.Versions != null)
            foreach (var v in piece.Versions)
            {
                if (v.Composers != null)
                    foreach (var c in v.Composers)
                        if (!string.IsNullOrEmpty(c.Role)) names.Add(c.Name);
                if (v.Subpieces != null)
                    foreach (var sp in v.Subpieces)
                        CollectAllContributorNames(sp, names);
            }

        if (piece.Subpieces != null)
            foreach (var sp in piece.Subpieces)
                CollectAllContributorNames(sp, names);
    }

}
