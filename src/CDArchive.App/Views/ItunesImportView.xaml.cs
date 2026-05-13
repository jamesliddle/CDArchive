using System.Windows.Controls;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ItunesImportView : UserControl
{
    public ItunesImportView()
    {
        InitializeComponent();
    }

    private async void OnImportSelectedClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not ItunesImportViewModel vm) return;
        var selected = TracksGrid.SelectedItems.OfType<ItunesTrack>().ToList();
        await vm.ImportTracksAsync(selected);
    }
}
