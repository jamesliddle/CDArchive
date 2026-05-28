using System.Windows;
using System.Windows.Input;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class EnsembleEntryEditorWindow : Window
{
    // H13 small-editors slice 4: Members list moved onto
    // EnsembleEntryEditorViewModel as ObservableCollection<InstrumentEntry>.
    // The XAML's MembersList binds to it directly; the imperative
    // RefreshMembersList method retires (CollectionChanged drives the re-render).
    private readonly EnsembleEntryEditorViewModel _vm = new();

    public InstrumentEntry Entry { get; private set; }

    public EnsembleEntryEditorWindow(CanonPickLists pickLists, InstrumentEntry entry)
    {
        InitializeComponent();
        DataContext = _vm;

        Entry = entry;
        Title = $"Edit {entry.Instrument}";

        _vm.LoadFromEntry(entry);

        // Populate available instruments (static once loaded — stays in code-behind).
        foreach (var inst in pickLists.Instruments.Order())
            AvailableList.Items.Add(CanonFormat.TitleCase(inst));
    }

    private void OnAddMemberFromAvailableClick(object sender, RoutedEventArgs e)
    {
        if (AvailableList.SelectedItem is not string instrument) return;
        _vm.Members.Add(new InstrumentEntry { Instrument = instrument });
        MembersList.SelectedIndex = _vm.Members.Count - 1;
    }

    private void OnAvailableDoubleClick(object sender, MouseButtonEventArgs e) =>
        OnAddMemberFromAvailableClick(sender, e);

    private void OnRemoveMemberClick(object sender, RoutedEventArgs e)
    {
        var idx = MembersList.SelectedIndex;
        if (idx < 0) return;
        _vm.Members.RemoveAt(idx);
        if (_vm.Members.Count > 0)
            MembersList.SelectedIndex = Math.Min(idx, _vm.Members.Count - 1);
    }

    private void OnMoveMemberUpClick(object sender, RoutedEventArgs e)
    {
        var idx = MembersList.SelectedIndex;
        if (idx <= 0) return;
        var item = _vm.Members[idx];
        _vm.Members.RemoveAt(idx);
        _vm.Members.Insert(idx - 1, item);
        MembersList.SelectedIndex = idx - 1;
    }

    private void OnMoveMemberDownClick(object sender, RoutedEventArgs e)
    {
        var idx = MembersList.SelectedIndex;
        if (idx < 0 || idx >= _vm.Members.Count - 1) return;
        var item = _vm.Members[idx];
        _vm.Members.RemoveAt(idx);
        _vm.Members.Insert(idx + 1, item);
        MembersList.SelectedIndex = idx + 1;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // Rework H31 + H33: mutate the input Entry in place rather than
        // reconstructing. Pre-fix the OK handler built a new InstrumentEntry
        // with `IsEnsemble = true` hardcoded, which (a) silently dropped any
        // other field the editor doesn't know about (H31), and (b) silently
        // flipped IsEnsemble = true even if the caller had opened the editor
        // on a non-ensemble entry (H33). The VM's SaveToEntry preserves the
        // contract — only the Members list is touched.
        _vm.SaveToEntry(Entry);
        DialogResult = true;
    }
}
