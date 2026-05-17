using System.Windows;
using System.Windows.Media;

namespace CDArchive.App.Helpers;

public static class WpfExtensions
{
    /// <summary>
    /// Walks the visual tree from <paramref name="obj"/> upward and returns the first
    /// element that is (or inherits from) <typeparamref name="T"/>, including
    /// <paramref name="obj"/> itself.  Returns null if no match is found.
    /// </summary>
    public static T? FindAncestorOrSelf<T>(this DependencyObject obj)
        where T : DependencyObject
    {
        while (obj != null)
        {
            if (obj is T t) return t;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    /// <summary>
    /// Breadth-first visual-tree search for the first descendant of <paramref name="obj"/>
    /// (excluding itself) that is (or inherits from) <typeparamref name="T"/>. Returns
    /// null if no match is found.
    /// </summary>
    public static T? FindVisualChild<T>(this DependencyObject obj)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(obj);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is T t) return t;
            var deeper = FindVisualChild<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
