using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using AgenTally.UI.ViewModels;

namespace AgenTally.UI.Views;

public partial class AnalysisView : UserControl
{
    public AnalysisView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ScrollToSelectedDay();
    }

    private void OnDataContextChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.OldValue is AnalysisViewModel previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (eventArgs.NewValue is AnalysisViewModel current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(AnalysisViewModel.DailyRows) or
            nameof(AnalysisViewModel.SelectedDailyRow))
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                ScrollToSelectedDay);
        }
    }

    private void ScrollToSelectedDay()
    {
        if (DataContext is AnalysisViewModel { SelectedDailyRow: not null } viewModel)
        {
            DailyGrid.ScrollIntoView(viewModel.SelectedDailyRow);
        }
    }
}
