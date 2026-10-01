using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ClashTray.App;

internal sealed record ListViewUpdateState(ScrollViewer? Scroll, double Offset, bool HadFocus)
{
    internal static ListViewUpdateState Capture(ListView view)
    {
        ScrollViewer? scroll = FindScroll(view);
        return new(scroll, scroll?.VerticalOffset ?? 0, view.XamlRoot is { } root && IsInside(FocusManager.GetFocusedElement(root) as DependencyObject, view));
    }

    internal void Restore(ListView view, Func<bool> isCurrent)
    {
        view.DispatcherQueue.TryEnqueue(() =>
        {
            if (!view.IsLoaded || !isCurrent())
            {
                return;
            }

            Scroll?.ChangeView(null, Offset, null, disableAnimation: true);
            if (HadFocus && IsInside(FocusManager.GetFocusedElement(view.XamlRoot) as DependencyObject, view)
                && view.SelectedItem is { } selected && view.ContainerFromItem(selected) is Control container)
            {
                container.Focus(FocusState.Programmatic);
            }
        });
    }

    private static bool IsInside(DependencyObject? focused, DependencyObject parent)
    {
        for (DependencyObject? current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, parent)) { return true; }
        }

        return false;
    }

    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer scroll) { return scroll; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindScroll(VisualTreeHelper.GetChild(element, i)) is { } found) { return found; }
        }

        return null;
    }
}
