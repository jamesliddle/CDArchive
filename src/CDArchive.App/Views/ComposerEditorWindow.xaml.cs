using System.Windows;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ComposerEditorWindow : Window
{
    private readonly CanonComposer _composer;

    /// <summary>
    /// The composer being edited (or newly created).
    /// </summary>
    public CanonComposer Composer => _composer;

    public ComposerEditorWindow(CanonComposer? composer = null)
    {
        InitializeComponent();

        var isNew = composer == null;
        _composer = composer ?? new CanonComposer();

        Title = isNew ? "New Composer" : "Edit Composer";
        LoadFromComposer();
    }

    private void LoadFromComposer()
    {
        NameBox.Text = _composer.Name;
        SortNameBox.Text = _composer.SortName;
        BirthDateBox.Text = _composer.BirthDate ?? "";
        BirthPlaceBox.Text = _composer.BirthPlace ?? "";
        BirthStateBox.Text = _composer.BirthState ?? "";
        BirthCountryBox.Text = _composer.BirthCountry ?? "";
        DeathDateBox.Text = _composer.DeathDate ?? "";
        DeathPlaceBox.Text = _composer.DeathPlace ?? "";
        DeathStateBox.Text = _composer.DeathState ?? "";
        DeathCountryBox.Text = _composer.DeathCountry ?? "";
    }

    private void SaveToComposer()
    {
        _composer.Name = NameBox.Text.Trim();
        _composer.SortName = SortNameBox.Text.Trim();
        _composer.BirthDate = NullIfEmpty(BirthDateBox.Text);
        _composer.BirthPlace = NullIfEmpty(BirthPlaceBox.Text);
        _composer.BirthState = NullIfEmpty(BirthStateBox.Text);
        _composer.BirthCountry = NullIfEmpty(BirthCountryBox.Text);
        _composer.DeathDate = NullIfEmpty(DeathDateBox.Text);
        _composer.DeathPlace = NullIfEmpty(DeathPlaceBox.Text);
        _composer.DeathState = NullIfEmpty(DeathStateBox.Text);
        _composer.DeathCountry = NullIfEmpty(DeathCountryBox.Text);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveToComposer();
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
