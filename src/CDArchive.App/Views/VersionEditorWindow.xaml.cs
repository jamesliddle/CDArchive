using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class VersionEditorWindow : Window
{
    private readonly CanonPickLists _pickLists;
    private readonly CanonPieceVersion _version;
    private readonly List<CanonPiece> _subpieces;

    public CanonPieceVersion Version => _version;

    public VersionEditorWindow(CanonPickLists pickLists, CanonPieceVersion? version = null)
    {
        InitializeComponent();

        _pickLists = pickLists;
        _version = version ?? new CanonPieceVersion();
        _subpieces = _version.Subpieces?.ToList() ?? [];

        Title = version == null ? "New Version" : "Edit Version";

        CatalogPrefixCombo.ItemsSource = _pickLists.CatalogPrefixes;
        CategoryCombo.ItemsSource = _pickLists.Categories;

        LoadFromVersion();
        RefreshSubpieceList();
    }

    private void LoadFromVersion()
    {
        DescriptionBox.Text = _version.Description ?? "";
        CategoryCombo.Text = _version.InstrumentationCategory ?? "";
        PubYearBox.Text = _version.PublicationYear?.ToString() ?? "";

        var cat = _version.CatalogInfo?.FirstOrDefault();
        CatalogPrefixCombo.Text = cat?.Catalog ?? "";
        CatalogNumberBox.Text = cat?.CatalogNumber ?? "";
    }

    private void SaveToVersion()
    {
        _version.Description = NullIfEmpty(DescriptionBox.Text);
        _version.InstrumentationCategory = NullIfEmpty(CategoryCombo.Text);
        _version.PublicationYear = int.TryParse(PubYearBox.Text.Trim(), out var y) ? y : null;

        var prefix = CatalogPrefixCombo.Text.Trim();
        var number = CatalogNumberBox.Text.Trim();
        if (!string.IsNullOrEmpty(prefix) || !string.IsNullOrEmpty(number))
        {
            _version.CatalogInfo = [new CatalogInfo
            {
                Catalog = prefix,
                CatalogNumber = string.IsNullOrEmpty(number) ? null : number
            }];
        }
        else
        {
            _version.CatalogInfo = null;
        }

        _version.Subpieces = _subpieces.Count > 0 ? _subpieces.ToList() : null;
    }

    // --- Subpiece management (same pattern as PieceEditorWindow) ---

    private void RefreshSubpieceList()
    {
        var selectedTag = (SubpieceList.SelectedItem as ListBoxItem)?.Tag;
        SubpieceList.Items.Clear();
        foreach (var sp in _subpieces)
        {
            var item = new ListBoxItem { Content = FormatSubpieceLabel(sp), Tag = sp };
            SubpieceList.Items.Add(item);
            if (sp == selectedTag) SubpieceList.SelectedItem = item;
        }
    }

    private static string FormatSubpieceLabel(CanonPiece sp)
    {
        var prefix = sp.Number.HasValue ? $"{sp.Number}. " : "";
        var tempoDesc = sp.TempoDescription;
        var form = sp.Form != null ? char.ToUpper(sp.Form[0]) + sp.Form[1..] : "";

        if (!string.IsNullOrEmpty(sp.Title))  return $"{prefix}{sp.Title}";
        if (!string.IsNullOrEmpty(form) && !string.IsNullOrEmpty(tempoDesc)) return $"{prefix}{form}. {tempoDesc}";
        if (!string.IsNullOrEmpty(tempoDesc)) return $"{prefix}{tempoDesc}";
        if (!string.IsNullOrEmpty(form))      return $"{prefix}{form}";
        return $"{prefix}(untitled)";
    }

    private CanonPiece? SelectedSubpiece =>
        (SubpieceList.SelectedItem as ListBoxItem)?.Tag as CanonPiece;

    private void OnAddSubpieceClick(object sender, RoutedEventArgs e)
    {
        var nextNumber = _subpieces.Count > 0
            ? _subpieces.Where(s => s.Number.HasValue).Select(s => s.Number!.Value).DefaultIfEmpty(0).Max() + 1
            : 1;
        var newMovement = new CanonPiece { Number = nextNumber };
        var editor = new MovementEditorWindow(_pickLists, newMovement) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _subpieces.Add(editor.Movement);
            RefreshSubpieceList();
        }
    }

    private void OnEditSubpieceClick(object sender, RoutedEventArgs e) => EditSelectedSubpiece();

    private void OnSubpieceDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedSubpiece();

    private void EditSelectedSubpiece()
    {
        if (SelectedSubpiece is not { } sp) return;
        var editor = new MovementEditorWindow(_pickLists, sp) { Owner = this };
        if (editor.ShowDialog() == true) RefreshSubpieceList();
    }

    private void OnRemoveSubpieceClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        _subpieces.Remove(sp);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _subpieces.IndexOf(sp);
        if (idx <= 0) return;
        (_subpieces[idx], _subpieces[idx - 1]) = (_subpieces[idx - 1], _subpieces[idx]);
        (sp.Number, _subpieces[idx].Number) = (_subpieces[idx].Number, sp.Number);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _subpieces.IndexOf(sp);
        if (idx < 0 || idx >= _subpieces.Count - 1) return;
        (_subpieces[idx], _subpieces[idx + 1]) = (_subpieces[idx + 1], _subpieces[idx]);
        (sp.Number, _subpieces[idx].Number) = (_subpieces[idx].Number, sp.Number);
        RefreshSubpieceList();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveToVersion();
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
