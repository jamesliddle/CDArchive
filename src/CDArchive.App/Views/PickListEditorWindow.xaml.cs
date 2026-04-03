using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class PickListEditorWindow : Window
{
    public ObservableCollection<string> Forms { get; }
    public ObservableCollection<string> Categories { get; }
    public ObservableCollection<string> CatalogPrefixes { get; }
    public ObservableCollection<string> KeyTonalities { get; }
    public ObservableCollection<string> Instruments { get; }

    /// <summary>Renames accumulated during editing: old value → new value.</summary>
    public Dictionary<string, string> FormRenames { get; } = new();
    public Dictionary<string, string> CategoryRenames { get; } = new();
    public Dictionary<string, string> CatalogRenames { get; } = new();
    public Dictionary<string, string> KeyRenames { get; } = new();
    public Dictionary<string, string> InstrumentRenames { get; } = new();

    public PickListEditorWindow(CanonPickLists pickLists)
    {
        InitializeComponent();

        // Clone lists so Cancel discards changes
        Forms = new ObservableCollection<string>(pickLists.Forms);
        Categories = new ObservableCollection<string>(pickLists.Categories);
        CatalogPrefixes = new ObservableCollection<string>(pickLists.CatalogPrefixes);
        KeyTonalities = new ObservableCollection<string>(pickLists.KeyTonalities);
        Instruments = new ObservableCollection<string>(pickLists.Instruments);

        FormsList.ItemsSource = Forms;
        CategoriesList.ItemsSource = Categories;
        CatalogsList.ItemsSource = CatalogPrefixes;
        KeysList.ItemsSource = KeyTonalities;
        InstrumentsList.ItemsSource = Instruments;
    }

    /// <summary>
    /// Applies the edited lists back to the given CanonPickLists instance.
    /// </summary>
    public void ApplyTo(CanonPickLists pickLists)
    {
        pickLists.Forms = Forms.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        pickLists.Categories = Categories.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        pickLists.CatalogPrefixes = CatalogPrefixes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        pickLists.KeyTonalities = KeyTonalities.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        pickLists.Instruments = Instruments.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    // --- Selection changed: populate text box ---
    private void OnFormsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormsList.SelectedItem is string s) FormTextBox.Text = s;
    }

    private void OnCategoriesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoriesList.SelectedItem is string s) CategoryTextBox.Text = s;
    }

    private void OnCatalogsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CatalogsList.SelectedItem is string s) CatalogTextBox.Text = s;
    }

    private void OnKeysSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeysList.SelectedItem is string s) KeyTextBox.Text = s;
    }

    private void OnInstrumentsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstrumentsList.SelectedItem is string s) InstrumentTextBox.Text = s;
    }

    // --- Add ---
    private void OnAddForm(object sender, RoutedEventArgs e) =>
        AddItem(FormTextBox, Forms);

    private void OnAddCategory(object sender, RoutedEventArgs e) =>
        AddItem(CategoryTextBox, Categories);

    private void OnAddCatalog(object sender, RoutedEventArgs e) =>
        AddItem(CatalogTextBox, CatalogPrefixes);

    private void OnAddKey(object sender, RoutedEventArgs e) =>
        AddItem(KeyTextBox, KeyTonalities);

    private void OnAddInstrument(object sender, RoutedEventArgs e) =>
        AddItem(InstrumentTextBox, Instruments);

    // --- Update (rename) ---
    private void OnUpdateForm(object sender, RoutedEventArgs e) =>
        UpdateItem(FormsList, FormTextBox, Forms, FormRenames);

    private void OnUpdateCategory(object sender, RoutedEventArgs e) =>
        UpdateItem(CategoriesList, CategoryTextBox, Categories, CategoryRenames);

    private void OnUpdateCatalog(object sender, RoutedEventArgs e) =>
        UpdateItem(CatalogsList, CatalogTextBox, CatalogPrefixes, CatalogRenames);

    private void OnUpdateKey(object sender, RoutedEventArgs e) =>
        UpdateItem(KeysList, KeyTextBox, KeyTonalities, KeyRenames);

    private void OnUpdateInstrument(object sender, RoutedEventArgs e) =>
        UpdateItem(InstrumentsList, InstrumentTextBox, Instruments, InstrumentRenames);

    // --- Remove ---
    private void OnRemoveForm(object sender, RoutedEventArgs e) =>
        RemoveSelected(FormsList, Forms);

    private void OnRemoveCategory(object sender, RoutedEventArgs e) =>
        RemoveSelected(CategoriesList, Categories);

    private void OnRemoveCatalog(object sender, RoutedEventArgs e) =>
        RemoveSelected(CatalogsList, CatalogPrefixes);

    private void OnRemoveKey(object sender, RoutedEventArgs e) =>
        RemoveSelected(KeysList, KeyTonalities);

    private void OnRemoveInstrument(object sender, RoutedEventArgs e) =>
        RemoveSelected(InstrumentsList, Instruments);

    // --- Helpers ---
    private static void AddItem(TextBox textBox, ObservableCollection<string> list)
    {
        var value = textBox.Text.Trim();
        if (string.IsNullOrEmpty(value)) return;
        if (!list.Contains(value))
        {
            list.Add(value);
            SortCollection(list);
        }
        textBox.Clear();
        textBox.Focus();
    }

    private static void UpdateItem(
        ListBox listBox, TextBox textBox,
        ObservableCollection<string> list,
        Dictionary<string, string> renames)
    {
        if (listBox.SelectedItem is not string oldValue) return;
        var newValue = textBox.Text.Trim();
        if (string.IsNullOrEmpty(newValue) || oldValue == newValue) return;

        // Record the rename (chain through any prior renames to the original value)
        var originalKey = renames.FirstOrDefault(r => r.Value == oldValue).Key;
        if (originalKey != null)
        {
            // Already renamed once: update the existing mapping
            renames[originalKey] = newValue;
        }
        else
        {
            renames[oldValue] = newValue;
        }

        var index = list.IndexOf(oldValue);
        if (index >= 0)
            list[index] = newValue;

        SortCollection(list);
        listBox.SelectedItem = newValue;
    }

    private static void RemoveSelected(ListBox listBox, ObservableCollection<string> list)
    {
        if (listBox.SelectedItem is string item)
            list.Remove(item);
    }

    private static void SortCollection(ObservableCollection<string> collection)
    {
        var sorted = collection.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        collection.Clear();
        foreach (var item in sorted)
            collection.Add(item);
    }
}
