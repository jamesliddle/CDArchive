namespace CDArchive.App.Services;

/// <summary>
/// Abstraction over the modal MessageBox surface so view-models can prompt
/// the user without referencing <c>System.Windows</c> directly (H3). The
/// production implementation (<see cref="WpfDialogService"/>) wraps WPF's
/// <c>MessageBox.Show</c>; tests substitute a recording fake to assert
/// "did the VM ask for confirmation before destructive action X?" without
/// instantiating a real Window.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Modal OK/Cancel confirmation. Returns true when the user clicks OK.
    /// Use for destructive actions (Reject, Restore, Delete) where the user
    /// should have a chance to back out.
    /// </summary>
    bool Confirm(string message, string title);

    /// <summary>Modal informational message with a single OK button.</summary>
    void ShowInfo(string message, string title);

    /// <summary>Modal error message with a single OK button.</summary>
    void ShowError(string message, string title);
}
