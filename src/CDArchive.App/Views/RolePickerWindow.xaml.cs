using System.Windows;
using System.Windows.Controls;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class RolePickerWindow : Window
{
    private readonly List<RoleEntry> _available;

    /// <summary>
    /// The roles the user selected. Empty if none were chosen.
    /// </summary>
    public IReadOnlyList<RoleEntry> SelectedRoles { get; private set; } = [];

    /// <summary>
    /// Shows roles from <paramref name="ancestorRoles"/> that are not already
    /// present in <paramref name="alreadyAssigned"/> (matched by name, case-insensitive).
    /// </summary>
    public RolePickerWindow(
        IReadOnlyList<RoleEntry> ancestorRoles,
        IReadOnlyList<RoleEntry> alreadyAssigned)
    {
        InitializeComponent();

        var assignedNames = new HashSet<string>(
            alreadyAssigned.Select(r => r.Name),
            StringComparer.OrdinalIgnoreCase);

        _available = ancestorRoles
            .Where(r => !assignedNames.Contains(r.Name))
            .ToList();

        foreach (var role in _available)
        {
            var item = new ListBoxItem { Content = role.DisplayLabel, Tag = role };
            RoleList.Items.Add(item);
        }

        Loaded += (_, _) => RoleList.Focus();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        SelectedRoles = RoleList.SelectedItems
            .Cast<ListBoxItem>()
            .Select(item => (RoleEntry)item.Tag)
            .ToList();

        DialogResult = true;
    }
}
