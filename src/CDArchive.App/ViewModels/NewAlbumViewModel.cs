using System.Collections.ObjectModel;
using CDArchive.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CDArchive.App.ViewModels;

public partial class NewAlbumViewModel : ObservableObject
{
    private readonly IAlbumScaffoldingService _scaffoldingService;
    private readonly IDuplicateDetectionService _duplicateDetectionService;

    [ObservableProperty]
    private string _albumName = "";

    [ObservableProperty]
    private int _discCount = 1;

    [ObservableProperty]
    private ObservableCollection<string> _duplicateWarnings = new();

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isSuccess;

    public NewAlbumViewModel(
        IAlbumScaffoldingService scaffoldingService,
        IDuplicateDetectionService duplicateDetectionService)
    {
        _scaffoldingService = scaffoldingService;
        _duplicateDetectionService = duplicateDetectionService;
    }

    partial void OnAlbumNameChanged(string value)
    {
        CheckDuplicates();
        CreateAlbumCommand.NotifyCanExecuteChanged();
    }

    partial void OnDiscCountChanged(int value)
    {
        CreateAlbumCommand.NotifyCanExecuteChanged();
    }

    private void CheckDuplicates()
    {
        DuplicateWarnings.Clear();

        if (string.IsNullOrWhiteSpace(AlbumName))
            return;

        var duplicates = _duplicateDetectionService.FindPotentialDuplicates(AlbumName);
        foreach (var duplicate in duplicates)
        {
            DuplicateWarnings.Add(duplicate);
        }
    }

    private bool CanCreateAlbum => !string.IsNullOrWhiteSpace(AlbumName) && DiscCount >= 1;

    [RelayCommand(CanExecute = nameof(CanCreateAlbum))]
    private void CreateAlbum()
    {
        try
        {
            _scaffoldingService.CreateAlbumStructure(AlbumName, DiscCount);
            StatusMessage = $"Album '{AlbumName}' created successfully.";
            IsSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating album: {ex.Message}";
            IsSuccess = false;
        }
    }
}
