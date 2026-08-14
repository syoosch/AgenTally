using System.Windows;
using System.Windows.Controls;
using AgenTally.UI.ViewModels;

namespace AgenTally.UI.Views;

public partial class SessionsView : UserControl
{
    private const double LoadMoreThreshold = 24d;

    public SessionsView()
    {
        InitializeComponent();
    }

    private void OnPromptTimelineItemsRequestBringIntoView(
        object sender,
        RequestBringIntoViewEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.TargetObject, sender))
        {
            return;
        }

        eventArgs.Handled = true;
    }

    private void OnSessionsScrollChanged(
        object sender,
        ScrollChangedEventArgs eventArgs)
    {
        if (sender is not ScrollViewer scroller ||
            scroller.ExtentHeight <= 0 ||
            scroller.VerticalOffset + scroller.ViewportHeight <
                scroller.ExtentHeight - LoadMoreThreshold ||
            DataContext is not SessionsViewModel viewModel ||
            !viewModel.LoadMoreSessionsCommand.CanExecute(null))
        {
            return;
        }

        _ = viewModel.LoadMoreSessionsCommand.ExecuteAsync();
    }
}
