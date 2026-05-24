using System.Windows.Controls;

namespace CDArchive.App.Helpers;

/// <summary>
/// Generic save / restore for WPF <see cref="TreeView"/> expansion state
/// keyed by stable data-object identity (so the tree's items can be
/// rebuilt while the user's expand-state survives). Extracted from
/// <c>PiecesWindow</c>'s code-behind (H47) so the same recursive walk
/// isn't duplicated across views.
///
/// <para>The save walk records whichever items are currently expanded,
/// keyed via the caller-supplied <c>keyOf</c> delegate. The restore walk
/// re-expands matching items, calling <c>UpdateLayout()</c> at each
/// level so child containers exist before recursion (WPF's
/// <see cref="ItemContainerGenerator"/> is virtualised; containers are
/// lazily realised).</para>
///
/// <para><c>CanonView</c>'s expansion-state machinery is more complex
/// (4 separate hashsets keyed by node type) and stays in place — a
/// future PR can layer a multi-set facade on top of this helper to
/// consolidate that too.</para>
/// </summary>
public static class TreeExpansionState
{
    /// <summary>
    /// Walks the tree and records expanded items into <paramref name="into"/>
    /// keyed via <paramref name="keyOf"/>. Items for which <paramref name="keyOf"/>
    /// returns null are skipped (use this to filter out item types you
    /// don't care about). Recurses through realised <see cref="TreeViewItem"/>
    /// containers only.
    /// </summary>
    public static void Save(
        ItemsControl root,
        Func<object, object?> keyOf,
        HashSet<object> into)
    {
        into.Clear();
        Collect(root, root.Items, keyOf, into);
    }

    /// <summary>
    /// Walks the tree and re-expands items whose key is present in
    /// <paramref name="from"/>. Calls <see cref="UIElement.UpdateLayout"/>
    /// at each level so child containers are realised before recursion.
    /// </summary>
    public static void Restore(
        ItemsControl root,
        Func<object, object?> keyOf,
        HashSet<object> from)
    {
        root.UpdateLayout();          // ensure top-level containers exist
        Apply(root, root.Items, keyOf, from);
    }

    /// <summary>
    /// Append-only variant of <see cref="Save"/>: walks a subtree under
    /// <paramref name="parent"/> and records expanded items into
    /// <paramref name="into"/> without clearing first. Used by orchestrators
    /// (e.g. CanonView) that walk a heterogeneous top-level structure and
    /// recurse into multiple subtrees, accumulating expansion state into a
    /// single set as they go.
    /// </summary>
    public static void CollectExpanded(
        ItemsControl parent,
        ItemCollection items,
        Func<object, object?> keyOf,
        HashSet<object> into)
    {
        foreach (var item in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                    is not TreeViewItem container) continue;
            if (!container.IsExpanded) continue;
            var key = keyOf(item);
            if (key != null) into.Add(key);
            if (container.HasItems)
                CollectExpanded(container, container.Items, keyOf, into);
        }
    }

    /// <summary>
    /// Re-expand variant of <see cref="Restore"/> for a subtree rooted at
    /// <paramref name="parent"/>. Callers that walk a heterogeneous
    /// top-level structure (e.g. CanonView's composer / cross-composer /
    /// contributed-group nodes) recurse into each subtree by passing the
    /// appropriate <c>TreeViewItem</c> as <paramref name="parent"/>.
    /// </summary>
    public static void ApplyExpanded(
        ItemsControl parent,
        ItemCollection items,
        Func<object, object?> keyOf,
        HashSet<object> from)
    {
        foreach (var item in items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                    is not TreeViewItem container) continue;
            var key = keyOf(item);
            if (key == null || !from.Contains(key)) continue;
            container.IsExpanded = true;
            container.UpdateLayout();   // ensure child containers exist before recursing
            if (container.HasItems)
                ApplyExpanded(container, container.Items, keyOf, from);
        }
    }

    // Internal aliases for the Save/Restore wrappers above. They're identical
    // to CollectExpanded/ApplyExpanded but kept named separately so the call
    // sites (Save/Restore) stay readable.
    private static void Collect(ItemsControl parent, ItemCollection items,
                                Func<object, object?> keyOf, HashSet<object> into) =>
        CollectExpanded(parent, items, keyOf, into);

    private static void Apply(ItemsControl parent, ItemCollection items,
                              Func<object, object?> keyOf, HashSet<object> from) =>
        ApplyExpanded(parent, items, keyOf, from);
}
