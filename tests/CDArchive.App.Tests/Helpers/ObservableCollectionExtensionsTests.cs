using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CDArchive.App.Helpers;

namespace CDArchive.App.Tests.Helpers;

/// <summary>
/// Tests for the M7 in-place reset extension. The headline contract is
/// "the collection instance is preserved across the reset" — that's what
/// keeps the WPF ItemsControl from re-realising every container, which is
/// the actual cure for the per-keystroke flicker.
/// </summary>
public class ObservableCollectionExtensionsTests
{
    [Fact]
    public void Reset_PreservesCollectionInstance()
    {
        // The original symptom: replacing the collection (`X = new ObservableCollection(...)`)
        // wipes the WPF binding's internal state and forces re-virtualisation.
        // Reset() keeps the same instance, so the binding stays stable.
        var coll = new ObservableCollection<int> { 1, 2, 3 };
        var beforeRef = coll;

        coll.Reset(new[] { 10, 20, 30 });

        Assert.Same(beforeRef, coll);
        Assert.Equal(new[] { 10, 20, 30 }, coll);
    }

    [Fact]
    public void Reset_FiresCollectionChangedEvents()
    {
        // WPF needs change notifications. Verify Clear() + each Add() raises
        // CollectionChanged (the ObservableCollection contract — Reset doesn't
        // suppress it).
        var coll = new ObservableCollection<int> { 1, 2 };
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.Reset(new[] { 10, 20, 30 });

        Assert.Contains(NotifyCollectionChangedAction.Reset, events);  // from Clear()
        Assert.Equal(3, events.Count(a => a == NotifyCollectionChangedAction.Add));
    }

    [Fact]
    public void Reset_EmptySource_LeavesCollectionEmpty()
    {
        var coll = new ObservableCollection<string> { "a", "b" };
        coll.Reset(Array.Empty<string>());
        Assert.Empty(coll);
    }

    [Fact]
    public void Reset_FromEmpty_PopulatesNormally()
    {
        var coll = new ObservableCollection<string>();
        coll.Reset(new[] { "x", "y", "z" });
        Assert.Equal(new[] { "x", "y", "z" }, coll);
    }

    [Fact]
    public void Reset_NullCollection_Throws()
    {
        ObservableCollection<int>? coll = null;
        Assert.Throws<ArgumentNullException>(() => coll!.Reset(new[] { 1 }));
    }

    [Fact]
    public void Reset_NullItems_Throws()
    {
        var coll = new ObservableCollection<int>();
        Assert.Throws<ArgumentNullException>(() => coll.Reset(null!));
    }
}
