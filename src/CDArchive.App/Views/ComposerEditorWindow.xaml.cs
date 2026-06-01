using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ComposerEditorWindow : Window
{
    // H13 small-editors slice 1: all field state moved onto
    // ComposerEditorViewModel. TwoWay XAML bindings keep the TextBoxes in
    // sync; the two ListBoxes ItemsSource-bind to ObservableCollections so
    // Add/Remove operations re-render automatically without the pre-fix
    // Refresh{Alias|Catalog}List methods.
    private readonly ComposerEditorViewModel _vm = new();
    private readonly CanonComposer _composer;

    /// <summary>The composer being edited (or newly created).</summary>
    public CanonComposer Composer => _composer;

    public ComposerEditorWindow(CanonPickLists pickLists, CanonComposer? composer = null)
    {
        InitializeComponent();
        DataContext = _vm;

        var isNew = composer == null;
        _composer = composer ?? new CanonComposer();

        Title = isNew ? "New Composer" : "Edit Composer";

        CatalogPrefixCombo.ItemsSource = pickLists.CatalogPrefixes;

        _vm.LoadFromComposer(_composer);
    }

    // ── Name → Sort Name auto-fill ───────────────────────────────────────────
    // When focus leaves the Name field and Sort Name is still empty, default
    // it to the Name. The convention for composer Name in this project is
    // already "Lastname, Firstname" (e.g. "Beethoven, Ludwig van"), so a
    // verbatim copy is correct for the common case. The user can refine it
    // freely afterwards — we only fire when SortName is currently blank, so
    // we never overwrite their input.

    private void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.SortName))
            _vm.SortName = _vm.Name?.Trim() ?? string.Empty;
    }

    // ── Aliases list ─────────────────────────────────────────────────────────

    private void OnAddAliasClick(object sender, RoutedEventArgs e)
    {
        var alias = AliasEntryBox.Text.Trim();
        if (string.IsNullOrEmpty(alias)) return;

        if (_vm.Aliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)))
        {
            AliasEntryBox.Text = "";
            return;
        }

        _vm.Aliases.Add(alias);
        AliasList.SelectedItem = alias;
        AliasEntryBox.Text = "";
        AliasEntryBox.Focus();
    }

    private void OnRemoveAliasClick(object sender, RoutedEventArgs e)
    {
        if (AliasList.SelectedItem is not string alias) return;
        _vm.Aliases.Remove(alias);
    }

    // ── Catalogue prefix list ────────────────────────────────────────────────

    private void OnAddCatalogClick(object sender, RoutedEventArgs e)
    {
        var prefix = CatalogPrefixCombo.Text.Trim();
        if (string.IsNullOrEmpty(prefix)) return;

        // Prevent duplicates (case-insensitive)
        if (_vm.CatalogPrefixes.Any(p => string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)))
        {
            CatalogPrefixCombo.Text = "";
            return;
        }

        _vm.CatalogPrefixes.Add(prefix);
        CatalogList.SelectedItem = prefix;
        CatalogPrefixCombo.Text = "";
    }

    private void OnRemoveCatalogClick(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not string prefix) return;
        _vm.CatalogPrefixes.Remove(prefix);
    }

    private void OnMoveCatalogUpClick(object sender, RoutedEventArgs e)
    {
        var idx = CatalogList.SelectedIndex;
        if (idx <= 0) return;
        var item = _vm.CatalogPrefixes[idx];
        _vm.CatalogPrefixes.RemoveAt(idx);
        _vm.CatalogPrefixes.Insert(idx - 1, item);
        CatalogList.SelectedIndex = idx - 1;
    }

    private void OnMoveCatalogDownClick(object sender, RoutedEventArgs e)
    {
        var idx = CatalogList.SelectedIndex;
        if (idx < 0 || idx >= _vm.CatalogPrefixes.Count - 1) return;
        var item = _vm.CatalogPrefixes[idx];
        _vm.CatalogPrefixes.RemoveAt(idx);
        _vm.CatalogPrefixes.Insert(idx + 1, item);
        CatalogList.SelectedIndex = idx + 1;
    }

    // ── OK / Cancel ─────────────────────────────────────────────────────────

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // H13 small-editors slice 1: validation lives on the VM. Code-behind
        // retains the MessageBox + Focus chrome on the relevant TextBox for
        // each validation case.
        var error = _vm.SaveToComposer(_composer);
        switch (error)
        {
            case ComposerEditorViewModel.SaveValidationError.MissingName:
                MessageBox.Show("Name is required.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;

            case ComposerEditorViewModel.SaveValidationError.MissingSortName:
                MessageBox.Show("Sort Name is required.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SortNameBox.Focus();
                return;

            case ComposerEditorViewModel.SaveValidationError.None:
            default:
                DialogResult = true;
                return;
        }
    }
}
