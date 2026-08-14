using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgenTally.UI.ViewModels;

namespace AgenTally.UI.Views;

public partial class SettingsView : UserControl
{
    internal const double TwoColumnBreakpoint = 960d;
    private SettingsViewModel? _subscribedViewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SettingsContentGrid.SizeChanged += OnSettingsContentSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachToViewModel(DataContext as SettingsViewModel);
        UpdateCategoryColumns(SettingsContentGrid.ActualWidth);
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachToViewModel(null);
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (IsLoaded)
        {
            AttachToViewModel(eventArgs.NewValue as SettingsViewModel);
        }
    }

    private void AttachToViewModel(SettingsViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribedViewModel = viewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(SettingsViewModel.SelectedSection))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            SettingsScrollViewer.ScrollToTop);
    }

    private void OnSettingsContentSizeChanged(
        object sender,
        SizeChangedEventArgs eventArgs)
    {
        UpdateCategoryColumns(eventArgs.NewSize.Width);
    }

    private void UpdateCategoryColumns(double width)
    {
        SettingsCategoryGrid.Columns = width >= TwoColumnBreakpoint ? 2 : 1;
    }

    private async void OnManualCodexRescanClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            await window.RequestCodexRescanAsync();
        }
    }
}
