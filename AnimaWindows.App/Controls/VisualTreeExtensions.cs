using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AnimaWindows.App.Controls;

public static class VisualTreeExtensions
{
    /// <summary>First descendant of type <typeparamref name="T"/> (depth first) matching <paramref name="predicate"/>.</summary>
    public static T? FindDescendant<T>(this DependencyObject root, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (predicate is null || predicate(match)))
            {
                return match;
            }

            var nested = child.FindDescendant(predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
