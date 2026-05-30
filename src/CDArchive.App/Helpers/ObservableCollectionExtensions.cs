using System.Collections.ObjectModel;

namespace CDArchive.App.Helpers;

/// <summary>
/// Convenience helpers for <see cref="ObservableCollection{T}"/> mutations
/// that need to preserve the collection instance.
///
/// <para>
/// Rework M7: pre-fix many filter/sort paths reassigned the bound property
/// — <c>FilteredComposers = new ObservableCollection&lt;...&gt;(sorted)</c>
/// — which forces the WPF ItemsControl to drop every realised container and
/// re-virtualise from scratch. At 3,000+ items that's perceivable as a
/// scroll-reset flicker on every keystroke in the filter textbox.
/// </para>
///
/// <para>
/// The fix: keep the same collection instance bound to the View; clear it
/// and re-add the new items. WPF's <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>
/// path handles the resulting Reset + Add events without dropping container
/// realisation, eliminating the flicker.
/// </para>
/// </summary>
public static class ObservableCollectionExtensions
{
    /// <summary>
    /// Clears <paramref name="collection"/> in place, then appends each item
    /// from <paramref name="items"/>. Preserves the collection instance so
    /// bound WPF controls don't re-virtualise.
    /// </summary>
    public static void Reset<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(items);

        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }
}
