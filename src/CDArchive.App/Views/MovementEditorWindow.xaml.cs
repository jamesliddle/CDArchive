using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class MovementEditorWindow : Window
{
    private readonly CanonPiece _movement;
    private readonly CanonPickLists _pickLists;
    private readonly List<TempoInfo> _tempos;
    private readonly List<CanonPiece> _subpieces;
    private readonly List<string> _roles = [];

    public CanonPiece Movement => _movement;

    public MovementEditorWindow(CanonPickLists pickLists, CanonPiece? movement = null)
    {
        InitializeComponent();

        _pickLists = pickLists;
        _movement = movement ?? new CanonPiece();
        _tempos = _movement.Tempos != null
            ? _movement.Tempos.Select(CloneTempo).ToList()
            : [];
        _subpieces = _movement.Subpieces?.ToList() ?? [];

        Title = movement == null ? "New Movement" : "Edit Movement";

        FormCombo.ItemsSource = _pickLists.Forms;
        KeyTonalityCombo.ItemsSource = _pickLists.KeyTonalities;

        LoadFromMovement();
        RefreshTempoList();
        RefreshRoleList();
        RefreshSubpieceList();
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

        // Roles — JSON string array
        if (_movement.Roles?.ValueKind == System.Text.Json.JsonValueKind.Array)
            _roles.AddRange(_movement.Roles.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0));

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

        // Roles — store as JSON string array
        _movement.Roles = _roles.Count > 0
            ? System.Text.Json.JsonSerializer.SerializeToElement(_roles.ToArray())
            : null;

        _movement.Tempos = _tempos.Count > 0 ? _tempos.ToList() : null;
        _movement.Subpieces = _subpieces.Count > 0 ? _subpieces.ToList() : null;
    }

    // --- Role list ---

    private void RefreshRoleList()
    {
        var selected = RoleList.SelectedItem as string;
        RoleList.Items.Clear();
        foreach (var name in _roles)
            RoleList.Items.Add(name);
        if (selected != null && RoleList.Items.Contains(selected))
            RoleList.SelectedItem = selected;
    }

    private void OnAddRoleClick(object sender, RoutedEventArgs e)
    {
        var name = RoleEntryBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _roles.Add(name);
        RefreshRoleList();
        RoleList.SelectedItem = name;
        RoleEntryBox.Text = "";
        RoleEntryBox.Focus();
    }

    private void OnRemoveRoleClick(object sender, RoutedEventArgs e)
    {
        if (RoleList.SelectedItem is not string name) return;
        _roles.Remove(name);
        RefreshRoleList();
    }

    // --- Tempo list ---

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

    private void OnEditTempoClick(object sender, RoutedEventArgs e) => EditSelectedTempo();

    private void OnTempoDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedTempo();

    private void EditSelectedTempo()
    {
        if (SelectedTempo is not { } tempo) return;
        var editor = new TempoEditorWindow(tempo) { Owner = this };
        if (editor.ShowDialog() == true)
            RefreshTempoList();
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

    // --- Sub-movement management ---

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

    private static string FormatSubpieceLabel(CanonPiece sp) => sp.SubpieceDisplayTitle;

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
        if (editor.ShowDialog() == true)
            RefreshSubpieceList();
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
        (sp.Number, _subpieces[idx].Number)     = (_subpieces[idx].Number, sp.Number);
        RefreshSubpieceList();
    }

    private void OnMoveSubpieceDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubpiece is not { } sp) return;
        var idx = _subpieces.IndexOf(sp);
        if (idx < 0 || idx >= _subpieces.Count - 1) return;
        (_subpieces[idx], _subpieces[idx + 1]) = (_subpieces[idx + 1], _subpieces[idx]);
        (sp.Number, _subpieces[idx].Number)     = (_subpieces[idx].Number, sp.Number);
        RefreshSubpieceList();
    }

    // --- OK ---

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
