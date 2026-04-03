using System.Windows;
using System.Windows.Controls;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class MovementEditorWindow : Window
{
    private readonly CanonPiece _movement;
    private readonly CanonPickLists _pickLists;
    private readonly List<TempoInfo> _tempos;

    public CanonPiece Movement => _movement;

    public MovementEditorWindow(CanonPickLists pickLists, CanonPiece? movement = null)
    {
        InitializeComponent();

        _pickLists = pickLists;
        _movement = movement ?? new CanonPiece();
        _tempos = _movement.Tempos != null
            ? _movement.Tempos.Select(CloneTempo).ToList()
            : [];

        Title = movement == null ? "New Movement" : "Edit Movement";

        FormCombo.ItemsSource = _pickLists.Forms;
        KeyTonalityCombo.ItemsSource = _pickLists.KeyTonalities;

        LoadFromMovement();
        RefreshTempoList();
    }

    private void LoadFromMovement()
    {
        NumberBox.Text   = _movement.Number?.ToString() ?? "";
        FormCombo.Text   = _movement.Form ?? "";
        TitleBox.Text    = _movement.Title ?? "";
        NicknameBox.Text = _movement.Nickname ?? "";
        NameBox.Text     = _movement.Name ?? "";
        FirstLineBox.Text = _movement.FirstLine ?? "";
        KeyTonalityCombo.Text = _movement.KeyTonality ?? "";

        // Roles — JSON string array, show comma-separated
        RolesBox.Text = _movement.Roles?.ValueKind == System.Text.Json.JsonValueKind.Array
            ? string.Join(", ", _movement.Roles.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0))
            : "";

        var mode = (_movement.KeyMode ?? "").ToLowerInvariant();
        foreach (ComboBoxItem item in KeyModeCombo.Items)
        {
            if ((item.Content as string ?? "") == mode)
            {
                KeyModeCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void SaveToMovement()
    {
        _movement.Number    = int.TryParse(NumberBox.Text.Trim(), out var n) ? n : null;
        _movement.Form      = NullIfEmpty(FormCombo.Text);
        _movement.Title     = NullIfEmpty(TitleBox.Text);
        _movement.Nickname  = NullIfEmpty(NicknameBox.Text);
        _movement.Name      = NullIfEmpty(NameBox.Text);
        _movement.FirstLine = NullIfEmpty(FirstLineBox.Text);
        _movement.KeyTonality = NullIfEmpty(KeyTonalityCombo.Text);

        var selectedMode = (KeyModeCombo.SelectedItem as ComboBoxItem)?.Content as string;
        _movement.KeyMode = string.IsNullOrEmpty(selectedMode) ? null : selectedMode;

        // Roles — split on commas, store as JSON string array
        var rolesText = NullIfEmpty(RolesBox.Text);
        _movement.Roles = rolesText != null
            ? System.Text.Json.JsonSerializer.SerializeToElement(
                rolesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : null;

        _movement.Tempos = _tempos.Count > 0 ? _tempos.ToList() : null;
    }

    private void RefreshTempoList()
    {
        TempoList.Items.Clear();
        foreach (var t in _tempos.OrderBy(t => t.Number))
        {
            var desc = !string.IsNullOrEmpty(t.Description) ? t.Description : "(no description)";
            TempoList.Items.Add(new ListBoxItem
            {
                Content = $"{t.Number}. {desc}",
                Tag = t
            });
        }
    }

    private TempoInfo? SelectedTempo =>
        (TempoList.SelectedItem as ListBoxItem)?.Tag as TempoInfo;

    private void OnAddTempoClick(object sender, RoutedEventArgs e)
    {
        var nextNumber = _tempos.Count > 0 ? _tempos.Max(t => t.Number) + 1 : 1;
        var editor = new TempoEditorWindow(nextNumber) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _tempos.Add(editor.Tempo);
            RefreshTempoList();
        }
    }

    private void OnEditTempoClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        var editor = new TempoEditorWindow(tempo) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            RefreshTempoList();
        }
    }

    private void OnRemoveTempoClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        _tempos.Remove(tempo);
        RefreshTempoList();
    }

    private void OnMoveTempoUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        var ordered = _tempos.OrderBy(t => t.Number).ToList();
        var idx = ordered.IndexOf(tempo);
        if (idx <= 0) return;

        // Swap numbers with the previous item
        (ordered[idx].Number, ordered[idx - 1].Number) =
            (ordered[idx - 1].Number, ordered[idx].Number);
        RefreshTempoList();
        SelectTempoByRef(tempo);
    }

    private void OnMoveTempoDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTempo is not { } tempo) return;
        var ordered = _tempos.OrderBy(t => t.Number).ToList();
        var idx = ordered.IndexOf(tempo);
        if (idx < 0 || idx >= ordered.Count - 1) return;

        (ordered[idx].Number, ordered[idx + 1].Number) =
            (ordered[idx + 1].Number, ordered[idx].Number);
        RefreshTempoList();
        SelectTempoByRef(tempo);
    }

    private void SelectTempoByRef(TempoInfo tempo)
    {
        foreach (ListBoxItem item in TempoList.Items)
        {
            if (item.Tag == tempo)
            {
                TempoList.SelectedItem = item;
                break;
            }
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SaveToMovement();
        DialogResult = true;
    }

    private static TempoInfo CloneTempo(TempoInfo t) => new()
    {
        Number = t.Number,
        Description = t.Description,
        SubTempos = t.SubTempos?.Select(CloneTempo).ToList()
    };

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
