using System.Collections.ObjectModel;
using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>EnsembleEntryEditorWindow</c> (H13 small-editors slice 4).
///
/// <para>Unlike the other small-editor VMs, this one owns ONLY the Members
/// list — the parent <see cref="InstrumentEntry"/>'s Instrument name and
/// IsEnsemble flag are deliberately NOT exposed for editing. The editor's
/// scope is "add / remove / reorder members of this ensemble", nothing else.
/// Pre-fix H31 + H33 retirements established this contract via the
/// mutate-in-place pattern; the VM enforces it by not even surfacing those
/// fields.</para>
///
/// <para><b>EnsembleName</b> is exposed as a read-only string so the XAML's
/// header label can bind to it without a separate code-behind assignment.</para>
/// </summary>
public partial class EnsembleEntryEditorViewModel : ObservableObject
{
    /// <summary>Read-only display of the parent ensemble's instrument name.
    /// Set by <see cref="LoadFromEntry"/>; never mutated by this editor.</summary>
    [ObservableProperty] private string _ensembleName = "";

    /// <summary>The ensemble's members. Each is an <see cref="InstrumentEntry"/>
    /// in its own right (with Instrument + optional Key / PartNumber / Alternate).
    /// Mutated via <c>Add</c> / <c>Remove</c> / indexer-swap by the editor's
    /// code-behind handlers; <see cref="ObservableCollection{T}.CollectionChanged"/>
    /// drives the ListBox re-render.</summary>
    public ObservableCollection<InstrumentEntry> Members { get; } = [];

    /// <summary>
    /// Populate from the parent entry. Re-running clears the list first to
    /// support theoretical re-loading.
    /// </summary>
    public void LoadFromEntry(InstrumentEntry entry)
    {
        EnsembleName = entry.Instrument ?? "";
        Members.Clear();
        foreach (var m in entry.Members ?? []) Members.Add(m);
    }

    /// <summary>
    /// Write the Members list back to the entry. Empty collection persists
    /// as null (keeps JSON snapshots clean). Critically does NOT touch
    /// <see cref="InstrumentEntry.Instrument"/> or
    /// <see cref="InstrumentEntry.IsEnsemble"/> — H31 + H33 contract.
    /// </summary>
    public void SaveToEntry(InstrumentEntry entry)
    {
        entry.Members = Members.Count > 0 ? Members.ToList() : null;
    }
}
