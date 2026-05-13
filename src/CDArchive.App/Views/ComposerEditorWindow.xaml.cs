using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ComposerEditorWindow : Window
{
    private readonly CanonComposer _composer;
    private readonly List<string> _aliases;
    private readonly List<string> _catalogPrefixes;

    /// <summary>
    /// The composer being edited (or newly created).
    /// </summary>
    public CanonComposer Composer => _composer;

    public ComposerEditorWindow(CanonPickLists pickLists, CanonComposer? composer = null)
    {
        InitializeComponent();

        var isNew = composer == null;
        _composer = composer ?? new CanonComposer();
        _aliases         = _composer.Aliases?.ToList() ?? [];
        _catalogPrefixes = _composer.CatalogPrefixes?.ToList() ?? [];

        Title = isNew ? "New Composer" : "Edit Composer";

        CatalogPrefixCombo.ItemsSource = pickLists.CatalogPrefixes;

        LoadFromComposer();
        RefreshAliasList();
        RefreshCatalogList();
    }

    private void LoadFromComposer()
    {
        NameBox.Text         = _composer.Name;
        SortNameBox.Text     = _composer.SortName;
        BirthDateBox.Text    = _composer.BirthDate ?? "";
        BirthPlaceBox.Text   = _composer.BirthPlace ?? "";
        BirthStateBox.Text   = _composer.BirthState ?? "";
        BirthCountryBox.Text = _composer.BirthCountry ?? "";
        DeathDateBox.Text    = _composer.DeathDate ?? "";
        DeathPlaceBox.Text   = _composer.DeathPlace ?? "";
        DeathStateBox.Text   = _composer.DeathState ?? "";
        DeathCountryBox.Text = _composer.DeathCountry ?? "";
        NotesBox.Text        = _composer.Notes ?? "";
    }

    private void SaveToComposer()
    {
        _composer.Name         = NameBox.Text.Trim();
        _composer.SortName     = SortNameBox.Text.Trim();
        _composer.BirthDate    = NullIfEmpty(BirthDateBox.Text);
        _composer.BirthPlace   = NullIfEmpty(BirthPlaceBox.Text);
        _composer.BirthState   = NullIfEmpty(BirthStateBox.Text);
        _composer.BirthCountry = NullIfEmpty(BirthCountryBox.Text);
        _composer.DeathDate    = NullIfEmpty(DeathDateBox.Text);
        _composer.DeathPlace   = NullIfEmpty(DeathPlaceBox.Text);
        _composer.DeathState   = NullIfEmpty(DeathStateBox.Text);
        _composer.DeathCountry = NullIfEmpty(DeathCountryBox.Text);
        _composer.Notes        = NullIfEmpty(NotesBox.Text);
        _composer.Aliases        = _aliases.Count > 0 ? _aliases.ToList() : null;
        _composer.CatalogPrefixes = _catalogPrefixes.Count > 0 ? _catalogPrefixes.ToList() : null;
    }

    // ── Aliases list ─────────────────────────────────────────────────────────

    private void RefreshAliasList()
    {
        var selected = AliasList.SelectedItem as string;
        AliasList.Items.Clear();
        foreach (var alias in _aliases)
            AliasList.Items.Add(alias);
        if (selected != null && AliasList.Items.Contains(selected))
            AliasList.SelectedItem = selected;
    }

    private void OnAddAliasClick(object sender, RoutedEventArgs e)
    {
        var alias = AliasEntryBox.Text.Trim();
        if (string.IsNullOrEmpty(alias)) return;

        if (_aliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)))
        {
            AliasEntryBox.Text = "";
            return;
        }

        _aliases.Add(alias);
        RefreshAliasList();
        AliasList.SelectedItem = alias;
        AliasEntryBox.Text = "";
        AliasEntryBox.Focus();
    }

    private void OnRemoveAliasClick(object sender, RoutedEventArgs e)
    {
        if (AliasList.SelectedItem is not string alias) return;
        _aliases.Remove(alias);
        RefreshAliasList();
    }

    // ── Catalogue prefix list ────────────────────────────────────────────────

    private void RefreshCatalogList()
    {
        var selected = CatalogList.SelectedItem as string;
        CatalogList.Items.Clear();
        foreach (var prefix in _catalogPrefixes)
            CatalogList.Items.Add(prefix);
        if (selected != null && CatalogList.Items.Contains(selected))
            CatalogList.SelectedItem = selected;
    }

    private void OnAddCatalogClick(object sender, RoutedEventArgs e)
    {
        var prefix = CatalogPrefixCombo.Text.Trim();
        if (string.IsNullOrEmpty(prefix)) return;

        // Prevent duplicates (case-insensitive)
        if (_catalogPrefixes.Any(p => string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)))
        {
            CatalogPrefixCombo.Text = "";
            return;
        }

        _catalogPrefixes.Add(prefix);
        RefreshCatalogList();
        CatalogList.SelectedItem = prefix;
        CatalogPrefixCombo.Text = "";
    }

    private void OnRemoveCatalogClick(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not string prefix) return;
        _catalogPrefixes.Remove(prefix);
        RefreshCatalogList();
    }

    private void OnMoveCatalogUpClick(object sender, RoutedEventArgs e)
    {
        var idx = CatalogList.SelectedIndex;
        if (idx <= 0) return;
        (_catalogPrefixes[idx - 1], _catalogPrefixes[idx]) = (_catalogPrefixes[idx], _catalogPrefixes[idx - 1]);
        RefreshCatalogList();
        CatalogList.SelectedIndex = idx - 1;
    }

    private void OnMoveCatalogDownClick(object sender, RoutedEventArgs e)
    {
        var idx = CatalogList.SelectedIndex;
        if (idx < 0 || idx >= _catalogPrefixes.Count - 1) return;
        (_catalogPrefixes[idx + 1], _catalogPrefixes[idx]) = (_catalogPrefixes[idx], _catalogPrefixes[idx + 1]);
        RefreshCatalogList();
        CatalogList.SelectedIndex = idx + 1;
    }

    // ── OK / Cancel ─────────────────────────────────────────────────────────

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveToComposer();
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
