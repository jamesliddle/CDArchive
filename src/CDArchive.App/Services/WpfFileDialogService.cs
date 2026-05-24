using Microsoft.Win32;

namespace CDArchive.App.Services;

/// <summary>
/// WPF implementation of <see cref="IFileDialogService"/>. Only class in
/// the app allowed to instantiate <see cref="SaveFileDialog"/> or
/// <see cref="OpenFileDialog"/> outside of view code-behind; VMs go through
/// <see cref="IFileDialogService"/> so they stay headless-testable.
/// </summary>
public sealed class WpfFileDialogService : IFileDialogService
{
    public string? PickSaveFile(string title, string filter, string defaultExt,
                                string? defaultFileName = null, string? initialDirectory = null)
    {
        var dlg = new SaveFileDialog
        {
            Title            = title,
            Filter           = filter,
            DefaultExt       = defaultExt,
            FileName         = defaultFileName ?? "",
            InitialDirectory = initialDirectory ?? "",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickOpenFile(string title, string filter, string defaultExt)
    {
        var dlg = new OpenFileDialog
        {
            Title      = title,
            Filter     = filter,
            DefaultExt = defaultExt,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
