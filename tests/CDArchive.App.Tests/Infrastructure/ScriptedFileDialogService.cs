using CDArchive.App.Services;

namespace CDArchive.App.Tests.Infrastructure;

/// <summary>
/// Headless test double for <see cref="IFileDialogService"/>. Returns
/// scripted paths from queues so tests can simulate the user picking
/// specific files (or cancelling, by enqueuing null).
/// </summary>
public sealed class ScriptedFileDialogService : IFileDialogService
{
    public Queue<string?> SavePathResponses { get; } = new();
    public Queue<string?> OpenPathResponses { get; } = new();

    public List<string> SaveCalls { get; } = new();
    public List<string> OpenCalls { get; } = new();

    public string? PickSaveFile(string title, string filter, string defaultExt,
                                string? defaultFileName = null, string? initialDirectory = null)
    {
        SaveCalls.Add(title);
        return SavePathResponses.Count > 0 ? SavePathResponses.Dequeue() : null;
    }

    public string? PickOpenFile(string title, string filter, string defaultExt)
    {
        OpenCalls.Add(title);
        return OpenPathResponses.Count > 0 ? OpenPathResponses.Dequeue() : null;
    }
}
