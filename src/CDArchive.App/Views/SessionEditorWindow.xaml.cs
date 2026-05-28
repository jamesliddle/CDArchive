using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class SessionEditorWindow : Window
{
    // H13 small-editors slice 3: field state moved to SessionEditorViewModel.

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
}
