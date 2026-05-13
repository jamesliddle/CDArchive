using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CDArchive.App.Helpers;

/// <summary>
/// Displays the sentinel word "Mixed" (gray italic) in an editor control whose
/// underlying values differ across a multi-edit selection, and wires behaviour so
/// that the first user edit — typing, backspace, delete, paste, cut — wipes the
/// placeholder entirely before the keystroke is processed, rather than letting
/// the edit mutate the word "Mixed" in place (e.g. backspace leaving "Mixe").
/// </summary>
public static class MixedPlaceholder
{
    public const string PlaceholderText = "Mixed";

    public static void Apply(TextBox box)
    {
        box.Text       = PlaceholderText;
        box.Foreground = Brushes.DarkGray;
        box.FontStyle  = FontStyles.Italic;
        WireClearBehavior(box, () => box.Text = "",
                               () => { box.CaretIndex = 0; box.SelectAll(); },
                               TextBox.ForegroundProperty, TextBox.FontStyleProperty);
    }

    public static void Apply(ComboBox box)
    {
        box.Text       = PlaceholderText;
        box.Foreground = Brushes.DarkGray;
        box.FontStyle  = FontStyles.Italic;
        WireClearBehavior(box, () => box.Text = "",
                               () => SelectAllInEditableCombo(box),
                               ComboBox.ForegroundProperty, ComboBox.FontStyleProperty);
    }

    /// <summary>
    /// Attaches preview handlers that wipe the placeholder before the first
    /// editing keystroke is processed. Navigation keys (arrows, tab, modifiers)
    /// do not clear the placeholder; only actual edits do.
    /// </summary>
    private static void WireClearBehavior(
        Control control,
        Action clearText,
        Action selectAll,
        DependencyProperty foregroundProperty,
        DependencyProperty fontStyleProperty)
    {
        var state = new PlaceholderState();

        control.GotFocus += (_, _) =>
        {
            if (state.Active) selectAll();
        };

        // Any text character input — clear first so the character lands in an empty box.
        control.PreviewTextInput += (_, _) => ClearIfActive();

        // Back / Delete — swallow the key; the whole placeholder is wiped in one step.
        // Ctrl+V / Ctrl+X — clear but let the clipboard command run against empty text.
        control.PreviewKeyDown += (_, e) =>
        {
            if (!state.Active) return;

            if (e.Key == Key.Back || e.Key == Key.Delete)
            {
                ClearIfActive();
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                (e.Key == Key.V || e.Key == Key.X))
            {
                ClearIfActive();
            }
        };

        // Paste via context menu or programmatic paste routes through the Paste command,
        // which doesn't always raise PreviewTextInput — hook DataObject.Pasting as a backstop.
        DataObject.AddPastingHandler(control, (_, _) => ClearIfActive());

        void ClearIfActive()
        {
            if (!state.Active) return;
            state.Active = false;
            clearText();
            control.ClearValue(foregroundProperty);
            control.ClearValue(fontStyleProperty);
        }
    }

    private static void SelectAllInEditableCombo(ComboBox cb)
    {
        // In IsEditable combos the visible editor is a template part named PART_EditableTextBox.
        if (cb.Template?.FindName("PART_EditableTextBox", cb) is TextBox editor)
            editor.SelectAll();
    }

    // Mutable flag captured by the handler closures so ClearIfActive is idempotent.
    private sealed class PlaceholderState { public bool Active = true; }
}
