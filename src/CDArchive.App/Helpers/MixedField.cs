using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.Helpers;

/// <summary>
/// Tri-state wrapper for an editor field that can be in one of:
/// <list type="bullet">
///   <item><b>Unanimous</b> — all selected items share the same value; show + edit.</item>
///   <item><b>Mixed</b> — selected items have different values; show a placeholder
///     (typically <c>"Mixed"</c>); the user must touch the field to commit.</item>
///   <item><b>UserEdited</b> — user actively changed the field; commit on save.</item>
/// </list>
///
/// <para>VM-side complement to <see cref="MixedPlaceholder"/> (which handles the
/// gray-italic UI chrome + first-edit-clear keystroke wiring). This class owns the
/// canonical tri-state — bind <c>TextBox.Text</c> to <see cref="Value"/> TwoWay and
/// the rest follows: when the user types, the binding sets <see cref="Value"/>,
/// which trips <see cref="WasEdited"/> via the partial-method hook and auto-clears
/// <see cref="IsMixed"/>. On save, multi-edit code checks <see cref="WasEdited"/>
/// to decide whether to apply <see cref="Value"/> to every selected item.</para>
///
/// <para>Pre-H13 the equivalent state lived as a <c>HashSet&lt;string&gt; _mixedFields</c>
/// on the editor window plus per-TextBox <c>MixedPlaceholder.Apply()</c> wiring, with
/// commit logic that read directly from XAML element <c>.Text</c>. This wrapper
/// centralises the contract so VMs can be unit-tested headlessly.</para>
/// </summary>
public partial class MixedField<T> : ObservableObject
{
    [ObservableProperty]
    private T? _value;

    /// <summary>True while the field is showing the "Mixed" placeholder.</summary>
    [ObservableProperty]
    private bool _isMixed;

    /// <summary>
    /// True iff the user has actively changed <see cref="Value"/> since
    /// <see cref="InitUnanimous"/> / <see cref="InitMixed"/> was last called.
    /// Multi-edit save semantics: write the new value to every selected item
    /// only when this is true. Single-edit always writes regardless.
    /// </summary>
    public bool WasEdited { get; private set; }

    /// <summary>
    /// True iff the field was last initialised via <see cref="InitMixed"/>
    /// (i.e. the multi-edit selection's values differed at load time).
    /// Stays true even after the user edits the field — distinguishing
    /// "this field started Unanimous so an empty user value is intentional"
    /// from "this field started Mixed so an empty user value is the result
    /// of clearing the placeholder without typing a replacement and we
    /// should NOT wipe every selected item to empty as a side effect".
    /// <para>Used by <c>AlbumEditorViewModel.SaveMulti</c> (H13 slice 4) to
    /// replace the editor's per-window <c>_mixedFields</c> HashSet.</para>
    /// </summary>
    public bool StartedMixed { get; private set; }

    /// <summary>
    /// Suppresses <see cref="WasEdited"/> tracking while the Init* methods set
    /// <see cref="Value"/> and <see cref="IsMixed"/>. Without this the
    /// programmatic load itself would trip <see cref="WasEdited"/>.
    /// </summary>
    private bool _initializing;

    /// <summary>
    /// Initialise to a unanimous value (single-edit, or multi-edit where every
    /// selected item shares this value). <see cref="WasEdited"/> resets to false.
    /// </summary>
    public void InitUnanimous(T value)
    {
        _initializing = true;
        try
        {
            IsMixed      = false;
            StartedMixed = false;
            Value        = value;
        }
        finally
        {
            _initializing = false;
            WasEdited     = false;
        }
    }

    /// <summary>
    /// Initialise to Mixed (multi-edit, values differ across selection). The
    /// <paramref name="mixedPlaceholder"/> goes into <see cref="Value"/> so any
    /// XAML binding renders the placeholder text. <see cref="IsMixed"/> = true
    /// and <see cref="WasEdited"/> resets to false.
    /// </summary>
    public void InitMixed(T mixedPlaceholder)
    {
        _initializing = true;
        try
        {
            Value        = mixedPlaceholder;
            IsMixed      = true;
            StartedMixed = true;
        }
        finally
        {
            _initializing = false;
            WasEdited     = false;
        }
    }

    /// <summary>
    /// Hook for the <c>[ObservableProperty]</c> setter. Fires on every value
    /// change (programmatic or via XAML binding). <see cref="_initializing"/>
    /// suppresses the WasEdited side-effect during Init*; otherwise a value
    /// change is by definition a user edit.
    /// </summary>
    partial void OnValueChanged(T? value)
    {
        if (_initializing) return;
        WasEdited = true;
        // Once the user mutates the value, the "Mixed" placeholder state is no
        // longer relevant — clear it so any IsMixed-conditioned UI (e.g. the
        // gray-italic styling MixedPlaceholder applies) re-renders to normal.
        if (IsMixed) IsMixed = false;
    }
}
