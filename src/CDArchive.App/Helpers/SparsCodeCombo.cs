using System.Windows.Controls;
using System.Windows.Media;

namespace CDArchive.App.Helpers;

/// <summary>
/// Helpers for the (non-editable) SPARS Code dropdown in the Album editor and
/// Track editor. The permitted values are DDD, ADD, AAD, and Unknown — that
/// is the full set the dropdown offers. Null / empty values from legacy data
/// map to "Unknown" on display (the dropdown has no blank entry, so "Unknown"
/// is the canonical "no specific code" choice). Legacy non-standard codes
/// (e.g. DDA from older data) are appended dynamically by <see cref="SelectValue"/>
/// so they remain visible and editable rather than being silently dropped.
/// <para>
/// In multi-edit mode, when the selected items disagree, we append a "Mixed"
/// sentinel <see cref="ComboBoxItem"/> instead of using the placeholder-text
/// pattern, because non-editable ComboBoxes don't support free-text placeholders.
/// </para>
/// </summary>
public static class SparsCodeCombo
{
    public const string MixedSentinel = "Mixed";

    /// <summary>
    /// Selects the ComboBoxItem matching <paramref name="value"/>. Null or
    /// empty maps to "Unknown" (the dropdown has no blank entry). If the
    /// value isn't already in the dropdown (legacy non-standard code), it is
    /// added dynamically so it round-trips correctly.
    /// </summary>
    public static void SelectValue(ComboBox box, string? value)
    {
        // No blank entry — null/empty is conceptually the same as "Unknown".
        if (string.IsNullOrEmpty(value))
            value = "Unknown";

        foreach (var obj in box.Items)
        {
            if (obj is ComboBoxItem cbi && (cbi.Content as string) == value)
            {
                box.SelectedItem = cbi;
                return;
            }
        }

        var newItem = new ComboBoxItem { Content = value };
        box.Items.Add(newItem);
        box.SelectedItem = newItem;
    }

    /// <summary>
    /// Returns the selected SPARS code. With no blank entry in the dropdown,
    /// the result is one of the standard codes ("DDD", "ADD", "AAD",
    /// "Unknown") or — for legacy data — a non-standard code that was
    /// appended via <see cref="SelectValue"/>.
    /// </summary>
    public static string? GetValue(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem cbi)
        {
            var s = cbi.Content as string ?? "";
            return string.IsNullOrEmpty(s) ? null : s;
        }
        return null;
    }

    /// <summary>
    /// True when the user hasn't picked a non-Mixed value (the Mixed sentinel
    /// item is still selected). Multi-edit code uses this to skip propagating
    /// the field to the underlying objects.
    /// </summary>
    public static bool IsMixedSentinelSelected(ComboBox box) =>
        box.SelectedItem is ComboBoxItem cbi
            && (cbi.Content as string) == MixedSentinel;

    /// <summary>
    /// Multi-edit population. If all <paramref name="values"/> agree, selects
    /// that value via <see cref="SelectValue"/>. Otherwise appends a "Mixed"
    /// sentinel item and selects it; returns true so the caller can register
    /// the field name in its mixed-fields set.
    /// </summary>
    public static bool PopulateMixed(ComboBox box, IEnumerable<string?> values)
    {
        var distinct = values.Distinct().ToList();
        if (distinct.Count == 1)
        {
            SelectValue(box, distinct[0]);
            return false;
        }

        var sentinel = new ComboBoxItem
        {
            Content    = MixedSentinel,
            Foreground = Brushes.DarkGray,
            FontStyle  = System.Windows.FontStyles.Italic
        };
        box.Items.Add(sentinel);
        box.SelectedItem = sentinel;
        return true;
    }
}
