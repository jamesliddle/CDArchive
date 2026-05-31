using System.Windows;

namespace CDArchive.App.Views;

/// <summary>
/// Single-field modal dialog used for adding or editing a single string
/// (e.g. an engineer name, a producer name) within a list. The caller sets
/// the window <see cref="Window.Title"/>, the prompt label, and the initial
/// value; the user's input (or null when they cancel) comes back via
/// <see cref="Result"/>.
/// </summary>
public partial class NameInputWindow : Window
{
    /// <summary>The user-entered name on OK; null when Cancel. Trimmed; empty
    /// string is rejected (OK button does nothing — keep typing or Cancel).</summary>
    public string? Result { get; private set; }

    /// <param name="title">Window title (e.g. "Add Engineer").</param>
    /// <param name="prompt">Label shown above the TextBox (e.g. "Engineer name:").</param>
    /// <param name="initial">Initial value (empty for Add, current value for Edit).</param>
    public NameInputWindow(string title, string prompt, string? initial = null)
    {
        InitializeComponent();
        Title          = title;
        PromptLabel.Text = prompt;
        NameBox.Text   = initial ?? "";
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var v = NameBox.Text?.Trim() ?? "";
        if (v.Length == 0)
        {
            // Empty input on OK is a no-op — keep the dialog open. The user
            // either types something or hits Cancel.
            NameBox.Focus();
            return;
        }
        Result       = v;
        DialogResult = true;
    }
}
