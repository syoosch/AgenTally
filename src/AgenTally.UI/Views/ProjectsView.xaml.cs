using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgenTally.UI.Views;

public partial class ProjectsView : UserControl
{
    private const double ScrollBoundaryTolerance = 0.5d;

    public ProjectsView()
    {
        InitializeComponent();
    }

    private void OnProjectTabContentRequestBringIntoView(
        object sender,
        RequestBringIntoViewEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.TargetObject, sender))
        {
            return;
        }

        eventArgs.Handled = true;
    }

    private void OnShareListPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs eventArgs)
    {
        if (sender is not ListBox list ||
            FindVisualDescendant<ScrollViewer>(list) is not { } listScroller ||
            CanScrollInDirection(listScroller, eventArgs.Delta))
        {
            return;
        }

        eventArgs.Handled = true;
        var forwardedEvent = new MouseWheelEventArgs(
            eventArgs.MouseDevice,
            eventArgs.Timestamp,
            eventArgs.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = ProjectsDetailScrollViewer
        };
        ProjectsDetailScrollViewer.RaiseEvent(forwardedEvent);
    }

    private static bool CanScrollInDirection(
        ScrollViewer scroller,
        int wheelDelta)
    {
        return wheelDelta switch
        {
            > 0 => scroller.VerticalOffset > ScrollBoundaryTolerance,
            < 0 => scroller.VerticalOffset <
                scroller.ScrollableHeight - ScrollBoundaryTolerance,
            _ => false
        };
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
