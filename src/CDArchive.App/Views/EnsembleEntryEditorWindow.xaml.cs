using System.Windows;
using System.Windows.Input;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class EnsembleEntryEditorWindow : Window
{
    private readonly List<InstrumentEntry> _members = [];

    public InstrumentEntry Entry { get; private set; }

    public EnsembleEntryEditorWindow(CanonPickLists pickLists, InstrumentEntry entry)
    {
        InitializeComponent();

        Entry = entry;
        EnsembleNameLabel.Text = entry.Instrument;
        Title = $"Edit {entry.Instrument}";

        if (entry.Members != null)
            _members.AddRange(entry.Members);

        // Populate available instruments
        foreach (var inst in pickLists.Instruments.Order())
            AvailableList.Items.Add(CanonFormat.TitleCase(inst));

        RefreshMembersList();
    }

    private void RefreshMembersList()
    {
        var selectedIdx = MembersList.SelectedIndex;
        MembersList.Items.Clear();
        foreach (var m in _members)
            MembersList.Items.Add(m.DisplayLabel);
        if (selectedIdx >= 0 && selectedIdx < MembersList.Items.Count)
            MembersList.SelectedIndex = selectedIdx;
    }

    private void OnAddMemberFromAvailableClick(object sender, RoutedEventArgs e)
    {
        if (AvailableList.SelectedItem is not string instrument) return;
        _members.Add(new InstrumentEntry { Instrument = instrument });
        RefreshMembersList();
        MembersList.SelectedIndex = MembersList.Items.Count - 1;
    }

    private void OnAvailableDoubleClick(object sender, MouseButtonEventArgs e) =>
        OnAddMemberFromAvailableClick(sender, e);

    private void OnRemoveMemberClick(object sender, RoutedEventArgs e)
    {
        var idx = MembersList.SelectedIndex;
        if (idx < 0) return;
        _members.RemoveAt(idx);
        RefreshMembersList();
        if (_members.Count > 0)
            MembersList.SelectedIndex = Math.Min(idx, _members.Count - 1);
    }

    private void OnMoveMemberUpClick(object sender, RoutedEventArgs e)
    {
        var idx = MembersList.SelectedIndex;
        if (idx <= 0) return;
        (_members[idx], _members[idx - 1]) = (_members[idx - 1], _members[idx]);
        RefreshMembersList();
        MembersList.SelectedIndex = idx - 1;
    }

    private void OnMoveMemberDownClick(object sender, RoutedEventArgs e)
    {
        var idx = MembersList.SelectedIndex;
        if (idx < 0 || idx >= _members.Count - 1) return;
        (_members[idx], _members[idx + 1]) = (_members[idx + 1], _members[idx]);
        RefreshMembersList();
        MembersList.SelectedIndex = idx + 1;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Entry = new InstrumentEntry
        {
            Instrument = Entry.Instrument,
            IsEnsemble = true,
            Members = _members.Count > 0 ? new List<InstrumentEntry>(_members) : null,
        };
        DialogResult = true;
    }
}
