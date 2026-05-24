using CDArchive.App.Services;

namespace CDArchive.App.Tests.Infrastructure;

/// <summary>
/// Headless test double for <see cref="IDialogService"/>. Records every
/// invocation so tests can assert "VM asked for confirmation before
/// destructive action X". <see cref="ConfirmResponse"/> can be scripted
/// in advance to simulate either user-clicked-OK or user-clicked-Cancel.
/// </summary>
public sealed class RecordingDialogService : IDialogService
{
    public List<(string Message, string Title)> ConfirmCalls { get; } = new();
    public List<(string Message, string Title)> InfoCalls    { get; } = new();
    public List<(string Message, string Title)> ErrorCalls   { get; } = new();

    /// <summary>Value returned by the next <see cref="Confirm"/> call. Default: true (user clicked OK).</summary>
    public bool ConfirmResponse { get; set; } = true;

    public bool Confirm(string message, string title)
    {
        ConfirmCalls.Add((message, title));
        return ConfirmResponse;
    }

    public void ShowInfo(string message, string title)  => InfoCalls.Add((message, title));
    public void ShowError(string message, string title) => ErrorCalls.Add((message, title));
}
