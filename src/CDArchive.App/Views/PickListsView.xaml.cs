using System.Windows;
using System.Windows.Controls;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class PickListsView : UserControl
{
    public PickListsView()
    {
        InitializeComponent();
    }

    private void OnMembersClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PickListsViewModel vm) return;
        var ens = vm.SelectedEnsemble();
        if (ens == null) return;

        // Build a transient InstrumentEntry to pass to the editor
        var entry = new InstrumentEntry
        {
            Instrument = ens.Name,
            IsEnsemble = true,
            Members    = ens.Members?.Select(m => new InstrumentEntry { Instrument = m }).ToList(),
        };

        var owner  = Window.GetWindow(this);
        var editor = new EnsembleEntryEditorWindow(vm.PickListsForDialog, entry) { Owner = owner };

        if (editor.ShowDialog() == true)
        {
            var memberNames = editor.Entry.Members?.Select(m => m.Instrument).ToList();
            vm.ApplyEnsembleMembers(memberNames);
        }
    }
}
