namespace CDArchive.Core.Helpers;

/// <summary>
/// The ten pick-list categories the Pick Lists editor manages.
///
/// <para>
/// Rework H30 regression: pre-fix <c>PickListsViewModel</c> identified each
/// list by its index in a hand-curated display array — most painfully
/// <c>SelectedListIndex == 6</c> (= Ensembles) appeared as a literal in two
/// places, and the <c>CurrentStringList()</c> / <c>CurrentRenameDict()</c>
/// dispatch were positional <c>switch</c> expressions over the same display
/// order. Reordering or inserting a new pick list silently routed every
/// Add / Update / Remove command to the wrong list — a real correctness bug
/// masquerading as a constant.
/// </para>
///
/// <para>
/// This enum is the named replacement. The display order lives in
/// <see cref="OrderedKinds"/>; everything else (list lookup, rename
/// targeting, the ensemble special-case predicate) is keyed on the kind, so
/// reshuffling the display only touches the array — never the routing.
/// </para>
/// </summary>
public enum PickListKind
{
    Forms,
    Categories,
    Catalogues,
    Keys,
    Instruments,
    CreativeRoles,
    Ensembles,
    VoiceTypes,
    PerformerRoles,
    Labels,
}

/// <summary>
/// Static helpers around <see cref="PickListKind"/>: the display ordering,
/// the user-facing names, and predicates the VM uses to specialise
/// behaviour (string-list vs ensemble-definition list; renamable into
/// piece fields vs not).
/// </summary>
public static class PickListKinds
{
    /// <summary>
    /// The display order used by the Pick Lists screen's selector ComboBox.
    /// Reorder freely — the rest of the system addresses lists by
    /// <see cref="PickListKind"/>, not by index.
    /// </summary>
    public static IReadOnlyList<PickListKind> OrderedKinds { get; } = new[]
    {
        PickListKind.Forms,
        PickListKind.Categories,
        PickListKind.Catalogues,
        PickListKind.Keys,
        PickListKind.Instruments,
        PickListKind.CreativeRoles,
        PickListKind.Ensembles,
        PickListKind.VoiceTypes,
        PickListKind.PerformerRoles,
        PickListKind.Labels,
    };

    /// <summary>User-facing label per kind. Stable across display reorders.</summary>
    public static string DisplayName(PickListKind kind) => kind switch
    {
        PickListKind.Forms          => "Forms",
        PickListKind.Categories     => "Categories",
        PickListKind.Catalogues     => "Catalogues",
        PickListKind.Keys           => "Keys",
        PickListKind.Instruments    => "Instruments",
        PickListKind.CreativeRoles  => "Creative Roles",
        PickListKind.Ensembles      => "Ensembles",
        PickListKind.VoiceTypes     => "Voice Types",
        PickListKind.PerformerRoles => "Performer Roles",
        PickListKind.Labels         => "Labels",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The display names in <see cref="OrderedKinds"/> order — bound by the
    /// selector ComboBox's <c>ItemsSource</c>.
    /// </summary>
    public static IReadOnlyList<string> OrderedDisplayNames { get; } =
        OrderedKinds.Select(DisplayName).ToList();

    /// <summary>
    /// Returns the kind at the given display index. Throws on out-of-range —
    /// the selector ComboBox is bound to <see cref="OrderedKinds"/> so any
    /// out-of-range value is a bug, not user input.
    /// </summary>
    public static PickListKind KindAt(int index)
    {
        if (index < 0 || index >= OrderedKinds.Count)
            throw new ArgumentOutOfRangeException(
                nameof(index), index,
                $"No pick-list kind at index {index}; OrderedKinds has {OrderedKinds.Count} entries.");
        return OrderedKinds[index];
    }

    /// <summary>
    /// Returns the display index of the given kind. Used when the VM needs
    /// to map back from kind to <c>SelectedListIndex</c> (e.g. tests).
    /// </summary>
    public static int IndexOf(PickListKind kind)
    {
        for (int i = 0; i < OrderedKinds.Count; i++)
            if (OrderedKinds[i] == kind) return i;
        // Unreachable — every PickListKind value is in OrderedKinds.
        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Kind missing from OrderedKinds.");
    }

    /// <summary>
    /// True iff the kind's backing collection is <c>List&lt;string&gt;</c>.
    /// Only <see cref="PickListKind.Ensembles"/> returns false — it stores
    /// <c>EnsembleDefinition</c> objects and needs the dedicated UI affordance
    /// for editing members.
    /// </summary>
    public static bool IsStringList(PickListKind kind) =>
        kind != PickListKind.Ensembles;

    /// <summary>
    /// True iff renaming a value in this list should propagate to existing
    /// piece records via the editor's rename-tracking dictionary. Currently
    /// the four kinds whose values appear as scalar piece fields (Form,
    /// InstrumentationCategory, Catalog prefix, KeyTonality).
    /// </summary>
    public static bool IsRenamable(PickListKind kind) =>
        kind is PickListKind.Forms
             or PickListKind.Categories
             or PickListKind.Catalogues
             or PickListKind.Keys;
}
