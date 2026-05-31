using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class SessionEditorWindow : Window
{
    // Rework H31: see PerformerEditorWindow for the mutate-in-place rationale.
    private readonly SessionEditorViewModel _vm = new();
    private readonly RecordingSession _working;

    public RecordingSession? Result { get; private set; }

    public SessionEditorWindow(RecordingSession? existing)
    {
        InitializeComponent();
        DataContext = _vm;

        _working = existing ?? new RecordingSession();
        _vm.LoadFromSession(_working);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // No required-field validation for sessions; commit whatever's there.
        _vm.SaveToSession(_working);
        Result = _working;
        DialogResult = true;
    }

    // ── Engineers list handlers ──────────────────────────────────────────────

    private void OnEngineerSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateListButtonState(EngineerList, _vm.Engineers,
            EditEngineerButton, RemoveEngineerButton,
            EngineerUpButton, EngineerDownButton);

    private void OnAddEngineer(object sender, RoutedEventArgs e) =>
        AddNameTo(_vm.Engineers, EngineerList, "Add Engineer", "Engineer name:");

    private void OnEditEngineer(object sender, RoutedEventArgs e) =>
        EditSelectedNameIn(_vm.Engineers, EngineerList, "Edit Engineer", "Engineer name:");

    private void OnRemoveEngineer(object sender, RoutedEventArgs e) =>
        RemoveSelectedNameIn(_vm.Engineers, EngineerList);

    private void OnEngineerMoveUp(object sender, RoutedEventArgs e) =>
        MoveSelectedIn(_vm.Engineers, EngineerList, delta: -1);

    private void OnEngineerMoveDown(object sender, RoutedEventArgs e) =>
        MoveSelectedIn(_vm.Engineers, EngineerList, delta: +1);

    private void OnEngineerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EngineerList.SelectedIndex < 0) return;
        OnEditEngineer(sender, e);
    }

    // ── Producers list handlers ──────────────────────────────────────────────

    private void OnProducerSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateListButtonState(ProducerList, _vm.Producers,
            EditProducerButton, RemoveProducerButton,
            ProducerUpButton, ProducerDownButton);

    private void OnAddProducer(object sender, RoutedEventArgs e) =>
        AddNameTo(_vm.Producers, ProducerList, "Add Producer", "Producer name:");

    private void OnEditProducer(object sender, RoutedEventArgs e) =>
        EditSelectedNameIn(_vm.Producers, ProducerList, "Edit Producer", "Producer name:");

    private void OnRemoveProducer(object sender, RoutedEventArgs e) =>
        RemoveSelectedNameIn(_vm.Producers, ProducerList);

    private void OnProducerMoveUp(object sender, RoutedEventArgs e) =>
        MoveSelectedIn(_vm.Producers, ProducerList, delta: -1);

    private void OnProducerMoveDown(object sender, RoutedEventArgs e) =>
        MoveSelectedIn(_vm.Producers, ProducerList, delta: +1);

    private void OnProducerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProducerList.SelectedIndex < 0) return;
        OnEditProducer(sender, e);
    }

    // ── Shared list-mutation helpers ─────────────────────────────────────────

    private void AddNameTo(ObservableCollection<string> list, ListBox listBox,
                           string title, string prompt)
    {
        var dlg = new NameInputWindow(title, prompt) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        list.Add(dlg.Result);
        listBox.SelectedIndex = list.Count - 1;
    }

    private void EditSelectedNameIn(ObservableCollection<string> list, ListBox listBox,
                                    string title, string prompt)
    {
        var idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        var dlg = new NameInputWindow(title, prompt, list[idx]) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        list[idx] = dlg.Result;
        listBox.SelectedIndex = idx;
    }

    private static void RemoveSelectedNameIn(ObservableCollection<string> list, ListBox listBox)
    {
        var idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        list.RemoveAt(idx);
        // After remove, keep a sensible row selected so subsequent button
        // clicks still target something visible.
        if (list.Count > 0)
            listBox.SelectedIndex = Math.Min(idx, list.Count - 1);
    }

    private static void MoveSelectedIn(ObservableCollection<string> list, ListBox listBox, int delta)
    {
        var idx = listBox.SelectedIndex;
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= list.Count) return;
        list.Move(idx, target);
        listBox.SelectedIndex = target;
    }

    private static void UpdateListButtonState(
        ListBox listBox, ObservableCollection<string> list,
        Button editBtn, Button removeBtn, Button upBtn, Button downBtn)
    {
        var idx = listBox.SelectedIndex;
        var has = idx >= 0;
        editBtn.IsEnabled   = has;
        removeBtn.IsEnabled = has;
        upBtn.IsEnabled     = has && idx > 0;
        downBtn.IsEnabled   = has && idx < list.Count - 1;
    }
}
