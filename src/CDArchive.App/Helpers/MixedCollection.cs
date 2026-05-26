using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.Helpers;

/// <summary>
/// Tri-state wrapper for an editor list field that can be in one of:
/// <list type="bullet">
///   <item><b>Unanimous</b> — all selected items share the same list; load it,
///     allow Add/Remove. Save writes the (possibly user-edited) list to every
///     item.</item>
///   <item><b>Mixed</b> — selected items have different lists; load empty.
///     Save skips when the user hasn't touched the list (preserving each
///     item's existing list); save writes when the user has Added/Removed
///     anything (the new list REPLACES each item's existing list — the
///     "first edit means I'm authoring a new shared list" contract).</item>
/// </list>
///
/// <para>Collection-level complement to <see cref="MixedField{T}"/>. Same
/// State / WasEdited semantics, but for an <see cref="ObservableCollection{T}"/>
/// rather than a scalar. The <see cref="Items"/> property exposes the
/// collection for ListView <c>ItemsSource</c> bindings and direct
/// Add/Remove operations from the code-behind.</para>
///
/// <para>Pre-H13 the TrackEditor tracked this state via two parallel
/// booleans per list (<c>_pieceRefsUntouched</c> / <c>_performersUntouched</c>)
/// plus an entry in <c>HashSet&lt;string&gt; _mixedFields</c>; the View
/// subscribed to <c>CollectionChanged</c> to flip the flag on first edit.
/// This wrapper centralises the contract.</para>
/// </summary>
public partial class MixedCollection<T> : ObservableObject
{
    /// <summary>
    /// The underlying observable collection. Bind <c>ListView.ItemsSource</c>
    /// here, and mutate via <c>Add</c> / <c>Remove</c> / indexer for in-place
    /// updates. The <see cref="WasEdited"/> flag flips true on any
    /// user-driven mutation (Init* methods suppress the flip via the
    /// <c>_initializing</c> guard).
    /// </summary>
    public ObservableCollection<T> Items { get; } = new();

    /// <summary>
    /// True iff the list was last initialised via <see cref="InitMixed"/>
    /// (i.e. the multi-edit selection's lists differed at load time). Stays
    /// true even after the user mutates the list — distinguishing the
    /// unanimous-list case (always write on save) from the was-Mixed case
    /// (write only when WasEdited is also true).
    /// </summary>
    public bool StartedMixed { get; private set; }

    /// <summary>
    /// True iff the user has Added/Removed/Replaced any item since
    /// <see cref="InitUnanimous"/> / <see cref="InitMixed"/> was last called.
    /// Multi-edit save semantics: when <see cref="StartedMixed"/> is true,
    /// write the new list to every selected item only when this is true.
    /// </summary>
    [ObservableProperty]
    private bool _wasEdited;

    /// <summary>
    /// Suppresses <see cref="WasEdited"/> tracking while the Init* methods
    /// populate <see cref="Items"/>. Without this the programmatic load
    /// itself would trip <see cref="WasEdited"/>.
    /// </summary>
    private bool _initializing;

    public MixedCollection()
    {
        Items.CollectionChanged += (_, _) =>
        {
            if (_initializing) return;
            if (!WasEdited) WasEdited = true;
        };
    }

    /// <summary>
    /// Initialise to a unanimous list (single-edit, or multi-edit where every
    /// selected item shares this list). Replaces any existing items.
    /// <see cref="WasEdited"/> resets to false; <see cref="StartedMixed"/>
    /// resets to false.
    /// </summary>
    public void InitUnanimous(IEnumerable<T> items)
    {
        _initializing = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
            StartedMixed = false;
        }
        finally
        {
            _initializing = false;
            WasEdited     = false;
        }
    }

    /// <summary>
    /// Initialise to Mixed (multi-edit, lists differ across selection).
    /// Clears <see cref="Items"/> so the UI shows an empty list (typically
    /// with a "Mixed" banner alongside). <see cref="StartedMixed"/> = true;
    /// <see cref="WasEdited"/> resets to false; the user's first Add/Remove
    /// flips WasEdited via the CollectionChanged subscription.
    /// </summary>
    public void InitMixed()
    {
        _initializing = true;
        try
        {
            Items.Clear();
            StartedMixed = true;
        }
        finally
        {
            _initializing = false;
            WasEdited     = false;
        }
    }

    /// <summary>
    /// Returns true when a multi-edit save should write this list to every
    /// selected item: either the list started Unanimous (idempotent rewrite)
    /// or the user touched the Mixed list (authored a new shared list).
    /// Mirrors <see cref="MixedField{T}"/>'s combo-field skip semantics.
    /// </summary>
    public bool ShouldWriteOnSave => !StartedMixed || WasEdited;
}
