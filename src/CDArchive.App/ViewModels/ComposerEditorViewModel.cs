using System.Collections.ObjectModel;
using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>ComposerEditorWindow</c> (H13 small-editors slice 1).
///
/// <para>The first of the H13 small-editor extractions. Owns all 11 text
/// fields (Name + SortName plus the four-part Birth and Death blocks plus
/// Notes) and the two string lists (Aliases, CatalogPrefixes). Plain
/// <c>[ObservableProperty]</c> string fields — no multi-edit, so no
/// <see cref="Helpers.MixedField{T}"/> wrapper.</para>
///
/// <para>Mirrors the established pattern (AlbumEditor, TrackEditor,
/// PieceEditor): VM-owned state + Load/Save methods + a SaveValidationError
/// enum for validation feedback. Code-behind retains the MessageBox + Focus
/// chrome for validation failures, plus the ListBox custom-rendering Refresh
/// helpers and the Add/Edit/Remove modal-dialog wiring.</para>
/// </summary>
public partial class ComposerEditorViewModel : ObservableObject
{
    // ── Identity (required) ──────────────────────────────────────────────────

    /// <summary>Display name (e.g. "Beethoven, Ludwig van"). Required.</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>Sort name. Required.</summary>
    [ObservableProperty] private string _sortName = "";

    // ── Birth ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _birthDate    = "";
    [ObservableProperty] private string _birthPlace   = "";
    [ObservableProperty] private string _birthState   = "";
    [ObservableProperty] private string _birthCountry = "";

    // ── Death ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _deathDate    = "";
    [ObservableProperty] private string _deathPlace   = "";
    [ObservableProperty] private string _deathState   = "";
    [ObservableProperty] private string _deathCountry = "";

    // ── Notes ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _notes = "";

    // ── List-shaped fields ───────────────────────────────────────────────────
    // Both are simple string lists. Add/Edit/Remove handlers in code-behind
    // mutate these directly via VM.Aliases.Add() / .Remove() / etc.

    /// <summary>Alternative names / spellings ("J. S. Bach", "Bach, J. S.").</summary>
    public ObservableCollection<string> Aliases         { get; } = [];

    /// <summary>Catalogue prefixes this composer uses (Op. / BWV / D. / K.).</summary>
    public ObservableCollection<string> CatalogPrefixes { get; } = [];

    /// <summary>
    /// Validation result returned by <see cref="SaveToComposer"/>. Code-behind
    /// surfaces the user-visible MessageBox + Focus on failure.
    /// </summary>
    public enum SaveValidationError
    {
        None,
        MissingName,
        MissingSortName,
    }

    /// <summary>
    /// Populate from an existing composer (or a fresh one for the "new"
    /// path). Re-running clears the lists first.
    /// </summary>
    public void LoadFromComposer(CanonComposer composer)
    {
        Name         = composer.Name         ?? "";
        SortName     = composer.SortName     ?? "";
        BirthDate    = composer.BirthDate    ?? "";
        BirthPlace   = composer.BirthPlace   ?? "";
        BirthState   = composer.BirthState   ?? "";
        BirthCountry = composer.BirthCountry ?? "";
        DeathDate    = composer.DeathDate    ?? "";
        DeathPlace   = composer.DeathPlace   ?? "";
        DeathState   = composer.DeathState   ?? "";
        DeathCountry = composer.DeathCountry ?? "";
        Notes        = composer.Notes        ?? "";

        Aliases.Clear();
        foreach (var a in composer.Aliases ?? []) Aliases.Add(a);

        CatalogPrefixes.Clear();
        foreach (var p in composer.CatalogPrefixes ?? []) CatalogPrefixes.Add(p);
    }

    /// <summary>
    /// Validate required fields + write VM state back to <paramref name="composer"/>.
    /// Returns <see cref="SaveValidationError.None"/> on success; the caller
    /// surfaces a validation message on failure (composer is left unmutated).
    /// </summary>
    public SaveValidationError SaveToComposer(CanonComposer composer)
    {
        if (string.IsNullOrWhiteSpace(Name))     return SaveValidationError.MissingName;
        if (string.IsNullOrWhiteSpace(SortName)) return SaveValidationError.MissingSortName;

        composer.Name         = Name.Trim();
        composer.SortName     = SortName.Trim();
        composer.BirthDate    = NullIfEmpty(BirthDate);
        composer.BirthPlace   = NullIfEmpty(BirthPlace);
        composer.BirthState   = NullIfEmpty(BirthState);
        composer.BirthCountry = NullIfEmpty(BirthCountry);
        composer.DeathDate    = NullIfEmpty(DeathDate);
        composer.DeathPlace   = NullIfEmpty(DeathPlace);
        composer.DeathState   = NullIfEmpty(DeathState);
        composer.DeathCountry = NullIfEmpty(DeathCountry);
        composer.Notes        = NullIfEmpty(Notes);

        composer.Aliases         = Aliases.Count         > 0 ? Aliases.ToList()         : null;
        composer.CatalogPrefixes = CatalogPrefixes.Count > 0 ? CatalogPrefixes.ToList() : null;

        return SaveValidationError.None;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
