using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Queries;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Writing;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI;
using AgenTally.UI.Controls;
using AgenTally.UI.Infrastructure;
using AgenTally.UI.Runtime;
using AgenTally.UI.ViewModels;
using AgenTally.UI.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
[DoNotParallelize]
public sealed class UiCompositionTests
{
    [TestMethod]
    [TestCategory("WindowedDesktop")]
    public async Task WindowedDesktopHost_ClosesHandleAfterDesktopThreadExits()
    {
        var host = StaDispatcherTestHost.CreateWindowedDesktop();
        string desktopName = host.IsolatedDesktopName ??
            throw new AssertFailedException("隔离桌面名称应在宿主启动后可用。");
        try
        {
            Assert.IsTrue(
                IsolatedDesktop.CanOpen(desktopName),
                "宿主运行期间隔离桌面句柄应保持有效。");
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.IsFalse(
            IsolatedDesktop.CanOpen(desktopName),
            "隔离 STA 线程退出后必须释放桌面句柄和桌面对象。");
    }

    [TestMethod]
    public async Task UnifiedScrollBarStyle_ReachesListBoxAndScrollViewer()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            var root = new Grid();
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/AgenTally.UI;component/Resources/Themes.xaml",
                    UriKind.Relative),
            });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition());

            var list = new ListBox
            {
                ItemsSource = Enumerable.Range(1, 30).ToArray(),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var detailScroll = new ScrollViewer
            {
                Content = new Border { Height = 1200d },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetColumn(detailScroll, 1);
            root.Children.Add(list);
            root.Children.Add(detailScroll);

            Layout(root, 600d, 220d);
            AssertPageScrollBarsUseUnifiedStyle(
                list,
                detailScroll,
                "独立滚动条样式验证");
        });
    }

    [TestMethod]
    public async Task StatisticsListBoxStyle_AlignsVisibleTrackWithUnscrolledItemEdge()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            var root = new Grid();
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/AgenTally.UI;component/Resources/Themes.xaml",
                    UriKind.Relative),
            });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition());

            Style listStyle = Assert.IsInstanceOfType<Style>(
                root.FindResource("StatisticsListBoxStyle"));
            var unscrolled = new ListBox
            {
                Style = listStyle,
                ItemsSource = new[] { "单项" },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var scrolling = new ListBox
            {
                Style = listStyle,
                ItemsSource = Enumerable.Range(1, 30).ToArray(),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(
                unscrolled,
                ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(
                unscrolled,
                ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(
                scrolling,
                ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(
                scrolling,
                ScrollBarVisibility.Auto);
            Grid.SetColumn(scrolling, 1);
            root.Children.Add(unscrolled);
            root.Children.Add(scrolling);

            Layout(root, 480d, 220d);
            ListBoxItem unscrolledItem = Assert.IsInstanceOfType<ListBoxItem>(
                unscrolled.ItemContainerGenerator.ContainerFromIndex(0));
            ScrollViewer scrollingViewport = FindVisualChild<ScrollViewer>(scrolling) ??
                throw new AssertFailedException("滚动列表应生成内部滚动视口。");
            scrollingViewport.ApplyTemplate();
            scrollingViewport.UpdateLayout();
            ScrollBar scrollingBar = Assert.IsInstanceOfType<ScrollBar>(
                scrollingViewport.Template.FindName(
                    "PART_VerticalScrollBar",
                    scrollingViewport));
            Assert.AreEqual(
                12d,
                scrollingBar.ActualWidth,
                0.01d,
                "统计列表的纵向滚动条实际宽度不应继续受 17 DIP 系统最小值约束。");
            scrollingBar.ApplyTemplate();
            Track track = Assert.IsInstanceOfType<Track>(
                scrollingBar.Template.FindName("PART_Track", scrollingBar));

            double unscrolledItemRight = unscrolledItem.TranslatePoint(
                new Point(unscrolledItem.ActualWidth, 0d),
                unscrolled).X;
            double visibleTrackRight = track.TranslatePoint(
                new Point(track.ActualWidth, 0d),
                scrolling).X;
            Point viewportOrigin = scrollingViewport.TranslatePoint(
                new Point(),
                scrolling);
            Point scrollBarOrigin = scrollingBar.TranslatePoint(
                new Point(),
                scrolling);
            Point trackOrigin = track.TranslatePoint(
                new Point(),
                scrolling);
            Assert.AreEqual(
                unscrolledItemRight,
                visibleTrackRight,
                1d,
                "纵向滑块右端应与无滚动条时的列表项右端保持同一基准线。" +
                $" viewport={viewportOrigin.X}+{scrollingViewport.ActualWidth}," +
                $" bar={scrollBarOrigin.X}+{scrollingBar.ActualWidth}," +
                $" track={trackOrigin.X}+{track.ActualWidth}");
        });
    }

    [TestMethod]
    public async Task UnifiedScrollBarStyle_MaterializesDataGridTemplate()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            var root = new Grid();
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/AgenTally.UI;component/Resources/Themes.xaml",
                    UriKind.Relative),
            });
            var grid = new DataGrid
            {
                ItemsSource = Enumerable.Range(1, 30).ToArray(),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "值",
                Binding = new Binding("."),
            });
            root.Children.Add(grid);

            Layout(root, 420d, 220d);
            grid.ApplyTemplate();
            grid.UpdateLayout();
            ScrollBar verticalScrollBar =
                FindVisualChildren<ScrollBar>(grid)
                    .FirstOrDefault(scrollBar =>
                        scrollBar.Orientation == Orientation.Vertical) ??
                throw new AssertFailedException(
                    "独立 DataGrid 应生成模板内纵向滚动条。");
            AssertUnifiedVerticalScrollBar(
                verticalScrollBar,
                "独立 DataGrid 滚动条样式验证");
        });
    }

    [TestMethod]
    [TestCategory("WindowedDesktop")]
    public async Task ProductionApp_ComposesApprovedPagesWithExplicitTemporaryDatabase()
    {
        using var directory = new TestTempDirectory();
        string databasePath = directory.File("explicit-ui.db");
        string startupSimulationPath = directory.File("startup-simulation.json");
        var connections = new SqliteConnectionFactory(
            new StorageOptions(databasePath));
        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);
        AgenTallyRuntimeProfile preferenceProfile =
            AgenTallyRuntimeProfile.CreateStable(
                directory.File("app"),
                directory.File("local-app-data"),
                directory.File("user-profile"));
        var preferencesStore = new JsonUiPreferencesStore(preferenceProfile);

        await using var host = StaDispatcherTestHost.CreateWindowedDesktop();
        Assert.IsNotNull(
            host.IsolatedDesktopName,
            "真实 WPF 窗口测试必须运行在独立桌面。");
        await host.InvokeAsync(async () =>
        {
            Assert.IsNull(Application.Current);
            var app = new App(enableOwnedRuntimeStartup: false);
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AssertSystemFocusVisualSuppressed(app);
            MainWindow? window = null;
            MainWindow? reopenedWindow = null;
            Window? diagnosticWindow = null;
            MainViewModel? main = null;
            MainViewModel? reopenedMain = null;
            var runtime = new ParserRebuildRequiredCoreRuntimeController();
            bool windowClosed = false;
            try
            {
                window = App.ComposeWindow(
                    databasePath,
                    host.Dispatcher,
                    runtime,
                    "AgenTally Dev",
                    isDevelopment: true,
                    channel: AgenTallyChannel.Development,
                    preferencesStore: preferencesStore,
                    startupRegistrationStore:
                        new ExactStartupRegistrationStore(
                            StartupRegistrationCommand.Create(
                                preferenceProfile.UiExecutablePath),
                            new DevelopmentStartupRegistrationBackend(
                                startupSimulationPath)));
                main = Assert.IsInstanceOfType<MainViewModel>(window.DataContext);
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Opacity = 0;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Show();
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                Assert.AreEqual(
                    Visibility.Collapsed,
                    Assert.IsInstanceOfType<Border>(
                        window.FindName("SidebarCoreStatus")).Visibility);
                await main.StartAsync();
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                Layout(window, 1280, 800);

                DashboardViewModel dashboard = main.Dashboard;
                SourcesViewModel sources = main.Sources;
                SettingsViewModel settings = main.Settings;
                Assert.AreEqual(databasePath, settings.DatabasePath);
                Assert.IsNull(dashboard.ErrorMessage);
                Assert.AreEqual("0", dashboard.RequestCountText);

                Assert.AreSame(main, window.DataContext);
                Assert.AreEqual("AgenTally Dev", window.Title);
                Assert.AreEqual(960d, window.Width);
                Assert.AreEqual(680d, window.Height);
                Assert.AreEqual(900d, window.MinWidth);
                Assert.AreEqual(640d, window.MinHeight);
                Assert.AreEqual(
                    WindowStyle.None,
                    window.WindowStyle);
                System.Windows.Shell.WindowChrome? windowChrome =
                    System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.IsNotNull(windowChrome);
                Assert.AreEqual(40d, windowChrome.CaptionHeight);
                Assert.AreEqual(
                    new Thickness(0),
                    windowChrome.GlassFrameThickness,
                    "自绘标题栏不得扩展 DWM 玻璃区域；Windows 11 圆角由原生 DWM 属性提供。");
                Assert.AreEqual(
                    new CornerRadius(0),
                    windowChrome.CornerRadius,
                    "WindowChrome 不应在原生 DWM 圆角之外再次塑造窗口区域。");
                Assert.IsFalse(window.AllowsTransparency);
                Assert.IsNotNull(
                    window.FindName("CaptionBar"),
                    "融合标题栏应与侧栏连成一体。");
                foreach (string buttonName in new[]
                {
                    "CaptionMinimizeButton",
                    "CaptionMaximizeRestoreButton",
                    "CaptionCloseButton"
                })
                {
                    Button captionButton = Assert.IsInstanceOfType<Button>(
                        window.FindName(buttonName));
                    captionButton.ApplyTemplate();
                    Assert.IsNull(
                        captionButton.Template.FindName(
                            "FocusUnderline",
                            captionButton),
                        $"{buttonName} 不应在获得焦点后显示橙色下划线。");
                }
                Assert.IsNotNull(
                    window.Icon,
                    "主窗口应加载正式 AgenTally 图标。");
                Button minimizeButton = Assert.IsInstanceOfType<Button>(
                    window.FindName("CaptionMinimizeButton"));
                Button maximizeButton = Assert.IsInstanceOfType<Button>(
                    window.FindName("CaptionMaximizeRestoreButton"));
                Button closeButton = Assert.IsInstanceOfType<Button>(
                    window.FindName("CaptionCloseButton"));
                DataTemplate minimizeGlyphTemplate =
                    Assert.IsInstanceOfType<DataTemplate>(
                        Application.Current.FindResource(
                            "CaptionMinimizeGlyphTemplate"));
                DataTemplate maximizeGlyphTemplate =
                    Assert.IsInstanceOfType<DataTemplate>(
                        Application.Current.FindResource(
                            "CaptionMaximizeGlyphTemplate"));
                DataTemplate restoreGlyphTemplate =
                    Assert.IsInstanceOfType<DataTemplate>(
                        Application.Current.FindResource(
                            "CaptionRestoreGlyphTemplate"));
                DataTemplate closeGlyphTemplate =
                    Assert.IsInstanceOfType<DataTemplate>(
                        Application.Current.FindResource(
                            "CaptionCloseGlyphTemplate"));
                Assert.AreSame(
                    minimizeGlyphTemplate,
                    minimizeButton.ContentTemplate,
                    "最小化按钮应使用统一描边矢量图标。");
                Assert.AreSame(
                    maximizeGlyphTemplate,
                    maximizeButton.ContentTemplate,
                    "普通窗口状态应显示单框最大化图标。");
                Assert.AreSame(
                    closeGlyphTemplate,
                    closeButton.ContentTemplate,
                    "关闭按钮应使用统一描边矢量图标。");
                minimizeButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(WindowState.Minimized, window.WindowState);
                window.ActivateFromExternalRequest();
                Assert.AreEqual(
                    WindowState.Normal,
                    window.WindowState,
                    "外部激活应恢复最小化窗口。");
                maximizeButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(WindowState.Maximized, window.WindowState);
                Assert.AreSame(
                    restoreGlyphTemplate,
                    maximizeButton.ContentTemplate,
                    "最大化窗口状态应显示叠框还原图标。");
                minimizeButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(WindowState.Minimized, window.WindowState);
                window.ActivateFromExternalRequest();
                Assert.AreEqual(
                    WindowState.Maximized,
                    window.WindowState,
                    "恢复最小化窗口时应保留原最大化状态。");
                maximizeButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(WindowState.Normal, window.WindowState);
                Assert.AreSame(
                    maximizeGlyphTemplate,
                    maximizeButton.ContentTemplate,
                    "还原普通窗口后应恢复单框最大化图标。");
                Assert.IsNull(
                    window.FindName("DevelopmentBadge"),
                    "Development 身份应通过窗口标题字符串（含 Dev）表达，不应重复显示客户区徽标。");
                StringAssert.Contains(
                    string.Join("\n", EnumerateVisibleText(window)),
                    "AgenTally Dev",
                    "自定义标题栏应继续显示含 Dev 标记的窗口标题。");
                Assert.AreEqual(
                    Visibility.Visible,
                    Assert.IsInstanceOfType<Border>(
                        window.FindName("SidebarCoreStatus")).Visibility);
                Assert.IsNull(
                    window.FindName("CoreStatusBanner"),
                    "全宽 Core 状态横条应已由左下角紧凑状态区取代。");
                TextBlock sidebarVersion = Assert.IsInstanceOfType<TextBlock>(
                    window.FindName("SidebarVersionText"));
                StringAssert.StartsWith(sidebarVersion.Text, "版本 ");
                Assert.AreEqual(
                    Visibility.Collapsed,
                    Assert.IsInstanceOfType<StackPanel>(
                        window.FindName("SidebarRefreshStatus")).Visibility);
                Assert.AreEqual(
                    "正在加载…",
                    Assert.IsInstanceOfType<TextBlock>(
                        window.FindName("SidebarLoadingText")).Text);
                Assert.AreSame(
                    Application.Current.FindResource("LoadingProgressBarStyle"),
                    Assert.IsInstanceOfType<ProgressBar>(
                        window.FindName("SidebarLoadingProgress")).Style,
                    "页面与页内异步操作应继续复用唯一的左下角加载条样式。");
                Assert.IsNull(
                    window.FindName("RebuildCodexButton"),
                    "Parser 变化应自动更新统计，不应在全局状态栏要求用户处理。");
                Assert.IsNull(
                    window.FindName("FullExitButton"),
                    "主窗口不应提供完全退出；该入口属于后续托盘菜单。");
                Border navigationSidebar = Assert.IsInstanceOfType<Border>(
                    window.FindName("NavigationSidebar"));
                Assert.AreEqual(122d, navigationSidebar.ActualWidth, 0.5d);
                Border mainContentSurface = Assert.IsInstanceOfType<Border>(
                    window.FindName("MainContentSurface"));
                Assert.AreEqual(16d, mainContentSurface.CornerRadius.TopLeft);
                Assert.AreEqual(0d, mainContentSurface.CornerRadius.TopRight);
                Assert.AreEqual(
                    new Thickness(0),
                    navigationSidebar.BorderThickness,
                    "导航与主面板之间不应保留硬分割线。");
                ListBox navigation = Assert.IsInstanceOfType<ListBox>(
                    window.FindName("NavigationList"));
                CollectionAssert.AreEqual(
                    new[] { "概览", "分析", "项目", "会话", "数据来源", "设置" },
                    navigation.Items.Cast<PageViewModel>()
                        .Select(page => page.Title)
                        .ToArray());
                StackPanel navigationPanel = Assert.IsInstanceOfType<StackPanel>(
                    navigation.ItemsPanel.LoadContent());
                Assert.AreEqual(
                    Orientation.Vertical,
                    navigationPanel.Orientation,
                    "主导航应在左侧纵向排列。");
                AssertUnifiedControlTemplates();
                ContentControl content = Assert.IsInstanceOfType<ContentControl>(
                    window.FindName("PageContent"));
                Assert.AreSame(dashboard, content.Content);
                Assert.AreEqual(
                    HorizontalAlignment.Stretch,
                    content.HorizontalContentAlignment,
                    "页面宿主必须横向拉伸当前视图，不能保留最大化前后的旧测量宽度。");
                Assert.AreEqual(
                    VerticalAlignment.Stretch,
                    content.VerticalContentAlignment,
                    "页面宿主必须纵向拉伸当前视图。");
                Assert.AreSame(
                    dashboard,
                    navigation.SelectedItem,
                    "启动后导航选中项应与当前页面一致。");
                ListBoxItem analysisNavigationItem =
                    Assert.IsInstanceOfType<ListBoxItem>(
                        navigation.ItemContainerGenerator.ContainerFromIndex(1));
                Button analysisNavigationButton =
                    FindVisualChild<Button>(analysisNavigationItem) ??
                    throw new AssertFailedException("分析导航按钮未生成。");
                System.Windows.Input.MouseButtonEventArgs rightPreview =
                    RaiseMouseButton(
                        analysisNavigationButton,
                        System.Windows.Input.Mouse.PreviewMouseDownEvent,
                        System.Windows.Input.MouseButton.Right);
                if (!rightPreview.Handled)
                {
                    RaiseMouseButton(
                        analysisNavigationButton,
                        System.Windows.Input.Mouse.MouseDownEvent,
                        System.Windows.Input.MouseButton.Right);
                }
                Assert.AreSame(
                    dashboard,
                    navigation.SelectedItem,
                    "右键导航按钮不应使侧栏选中态脱离当前页面。");
                Assert.AreSame(
                    dashboard,
                    content.Content,
                    "右键导航按钮不应切换内容页面。");
                Assert.IsNotNull(analysisNavigationButton.Command);
                analysisNavigationButton.Command.Execute(
                    analysisNavigationButton.CommandParameter);
                if (main.NavigateCommand.ExecutionTask is Task navigationTask)
                {
                    await navigationTask;
                }
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreSame(
                    main.Analysis,
                    navigation.SelectedItem,
                    "右键后正常左键操作仍应同步导航选中项。");
                Assert.AreSame(
                    main.Analysis,
                    content.Content,
                    "右键后正常左键操作仍应切换内容页面。");
                await main.NavigateCommand.ExecuteAsync(dashboard);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await AssertCurrentPageReflowsAcrossWindowStatesAsync(
                    window,
                    content,
                    dashboard,
                    maximizeButton);
                await main.NavigateCommand.ExecuteAsync(main.Sessions);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreSame(main.Sessions, content.Content);
                await AssertCurrentPageReflowsAcrossWindowStatesAsync(
                    window,
                    content,
                    main.Sessions,
                    maximizeButton);
                await main.NavigateCommand.ExecuteAsync(dashboard);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                AssertTemplate<DashboardView>(app, typeof(DashboardViewModel));
                AssertTemplate<AnalysisView>(app, typeof(AnalysisViewModel));
                AssertTemplate<ProjectsView>(app, typeof(ProjectsViewModel));
                AssertTemplate<SessionsView>(app, typeof(SessionsViewModel));
                AssertTemplate<SourcesView>(app, typeof(SourcesViewModel));
                AssertTemplate<SettingsView>(app, typeof(SettingsViewModel));

                var dashboardView = new DashboardView { DataContext = dashboard };
                Layout(dashboardView, 1000, 760);
                AssertTopFilterLayout(
                    dashboardView,
                    "DashboardFilterBar",
                    "DashboardProjectFilter");
                Layout(dashboardView, 808, 760);
                AssertTopFilterLayout(
                    dashboardView,
                    "DashboardFilterBar",
                    "DashboardProjectFilter");
                Layout(dashboardView, 1000, 760);
                Assert.IsNull(
                    Assert.IsInstanceOfType<ScrollViewer>(
                        dashboardView.FindName("DashboardScrollViewer"))
                        .FocusVisualStyle);
                Assert.IsNotNull(dashboardView.FindName("UsageHeatmap"));
                Assert.IsNotNull(dashboardView.FindName("UsageTrendChart"));
                TextBlock totalTokens = Assert.IsInstanceOfType<TextBlock>(
                    dashboardView.FindName("TotalTokensValue"));
                Assert.AreEqual(
                    FontNumeralStyle.Lining,
                    Typography.GetNumeralStyle(totalTokens));
                Assert.AreEqual(
                    FontNumeralAlignment.Tabular,
                    Typography.GetNumeralAlignment(totalTokens));
                Assert.IsFalse(
                    totalTokens.FontFamily.Source.Contains(
                        "Georgia",
                        StringComparison.OrdinalIgnoreCase));
                AssertNeutralPricePresentation(
                    dashboardView,
                    "DashboardEquivalentValue",
                    "DashboardEquivalentCaption");
                var analysisView = new AnalysisView { DataContext = main.Analysis };
                Layout(analysisView, 896, 518);
                AssertTopFilterLayout(
                    analysisView,
                    "AnalysisFilterBar",
                    "AnalysisProjectFilter");
                Layout(analysisView, 808, 558);
                AssertTopFilterLayout(
                    analysisView,
                    "AnalysisFilterBar",
                    "AnalysisProjectFilter");
                Layout(analysisView, 896, 518);
                AssertNeutralPriceValuePresentation(
                    analysisView,
                    "AnalysisEquivalentValue");
                Assert.IsNull(
                    analysisView.FindName("AnalysisEquivalentCaption"),
                    "分析页不应继续呈现等效价格说明行。");
                var projectsView = new ProjectsView
                {
                    DataContext = main.Projects
                };
                Layout(analysisView, 1000, 760);
                Layout(projectsView, 1000, 760);
                AssertStatisticsHeaderAlignment(
                    dashboardView,
                    analysisView,
                    projectsView);
                Layout(dashboardView, 808, 760);
                Layout(analysisView, 808, 760);
                Layout(projectsView, 808, 760);
                AssertStatisticsHeaderAlignment(
                    dashboardView,
                    analysisView,
                    projectsView);
                AssertMetricCardRows(dashboardView, analysisView);
                Assert.IsFalse(
                    EnumerateVisibleText(analysisView).Contains("范围说明"),
                    "分析页必须完整移除范围说明栏。");
                AssertVirtualizedGrid(analysisView, "DailyGrid");
                AssertVirtualizedGrid(analysisView, "AgentGrid");
                AssertVirtualizedGrid(analysisView, "ModelGrid");
                AssertCenteredHeaders(analysisView, "DailyGrid");
                AssertCenteredHeaders(analysisView, "AgentGrid");
                AssertCenteredHeaders(analysisView, "ModelGrid");
                AssertBillingTokenCategoryOrder(
                    Assert.IsInstanceOfType<TextBlock>(
                        dashboardView.FindName("DashboardTotalDetail")).Text,
                    "Overview Token 明细");
                AssertBillingTokenCategoryOrder(analysisView, "DailyGrid");
                AssertBillingTokenCategoryOrder(analysisView, "AgentGrid");
                AssertBillingTokenCategoryOrder(analysisView, "ModelGrid");
                AssertCenteredDailyDateCells(analysisView);
                AssertSegmentedTabs(analysisView);
                var sourcesView = new SourcesView { DataContext = sources };
                Layout(sourcesView, 1000, 700);
                var sessionsView = new SessionsView
                {
                    DataContext = main.Sessions
                };
                Layout(sessionsView, 1000, 700);
                DateTime customStart = new(2026, 8, 1, 9, 0, 0);
                DateTime customEnd = new(2026, 8, 2, 10, 0, 0);
                dashboard.ApplySynchronizedFilters(
                    DashboardViewModel.Custom,
                    DashboardViewModel.AllAgents,
                    DashboardViewModel.AllModels,
                    customStart,
                    customEnd,
                    DashboardViewModel.AllProjects);
                main.Analysis.ApplySynchronizedFilters(
                    DashboardViewModel.Custom,
                    DashboardViewModel.AllAgents,
                    DashboardViewModel.AllModels,
                    customStart,
                    customEnd,
                    DashboardViewModel.AllProjects);
                main.Projects.ApplySynchronizedFilters(
                    ProjectsViewModel.Custom,
                    ProjectsViewModel.AllAgents,
                    ProjectsViewModel.AllModels,
                    customStart,
                    customEnd);
                main.Sessions.ApplySynchronizedFilters(
                    SessionsViewModel.Custom,
                    SessionsViewModel.AllAgents,
                    SessionsViewModel.AllModels,
                    customStart,
                    customEnd,
                    SessionsViewModel.AllProjects);
                foreach ((FrameworkElement view, string pickerName) in new[]
                {
                    ((FrameworkElement)dashboardView, "DashboardDateTimeRangePicker"),
                    ((FrameworkElement)analysisView, "AnalysisDateTimeRangePicker"),
                    ((FrameworkElement)projectsView, "ProjectsDateTimeRangePicker"),
                    ((FrameworkElement)sessionsView, "SessionsDateTimeRangePicker")
                })
                {
                    Layout(view, 868, 640);
                    DateTimeRangePicker picker =
                        Assert.IsInstanceOfType<DateTimeRangePicker>(
                            ((FrameworkElement)view).FindName(pickerName));
                    Assert.IsGreaterThan(0d, picker.ActualWidth);
                    Assert.IsLessThanOrEqualTo(
                        view.ActualWidth,
                        picker.ActualWidth,
                        $"{pickerName} 在 900×640 最小窗口内容区内不得横向裁切。");
                    Layout(view, 1384, 920);
                    Assert.IsLessThanOrEqualTo(
                        view.ActualWidth,
                        picker.ActualWidth,
                        $"{pickerName} 在 1536×960 窗口内容区内不得横向裁切。");
                }
                AssertStatisticsFirstContentGaps(
                    dashboardView,
                    analysisView,
                    projectsView,
                    sessionsView);
                AssertTopFilterLayout(
                    sessionsView,
                    "SessionsFilterBar",
                    "SessionsProjectFilter");
                var settingsHeaderView = new SettingsView
                {
                    DataContext = main.Settings
                };
                Layout(settingsHeaderView, 1000, 700);
                Assert.IsFalse(
                    EnumerateVisibleText(sessionsView).Contains(
                        "按 Prompt、会话构成和模型查看根会话用量"),
                    "会话页标题下说明应被移除。");
                Assert.IsFalse(
                    EnumerateVisibleText(analysisView).Contains("部分计价"),
                    "分析页不应继续呈现部分计价说明。");
                Assert.IsFalse(
                    EnumerateVisibleText(sourcesView).Contains(
                        "查看本地采集来源、解析版本与最近状态"),
                    "数据来源页标题下说明应被移除。");
                Assert.IsFalse(
                    EnumerateVisibleText(settingsHeaderView).Contains(
                        "管理模型价格、更新检查频率和本地存储信息"),
                    "设置页标题下说明应被移除。");
                AssertVirtualizedGrid(sourcesView, "SourcesGrid");
                AssertSourceGridLayout(sourcesView);

                TabControl analysisTabs = Assert.IsInstanceOfType<TabControl>(
                    analysisView.FindName("AnalysisTabs"));
                analysisTabs.SelectedIndex = 0;
                DataGrid dailyGrid = Assert.IsInstanceOfType<DataGrid>(
                    analysisView.FindName("DailyGrid"));
                BindingOperations.ClearBinding(
                    dailyGrid,
                    ItemsControl.ItemsSourceProperty);
                dailyGrid.ItemsSource = Enumerable.Range(0, 60)
                    .Select(index => new
                    {
                        DateText = index.ToString(
                            CultureInfo.InvariantCulture),
                        TotalTokensText = "1",
                        RequestCountText = "1",
                        UncachedInputText = "1",
                        OutputText = "1",
                        CacheReadText = "1",
                        CacheHitRateText = "1%",
                    })
                    .ToArray();
                DataGrid sourcesGrid = Assert.IsInstanceOfType<DataGrid>(
                    sourcesView.FindName("SourcesGrid"));
                BindingOperations.ClearBinding(
                    sourcesGrid,
                    ItemsControl.ItemsSourceProperty);
                sourcesGrid.ItemsSource = Enumerable.Range(0, 250)
                    .Select(index => new
                    {
                        Status = "正常",
                        Name = $"Source {index}",
                        SourceInstance = "Instance",
                        SourceEntity = "Entity",
                        Path = "Path",
                    })
                    .ToArray();
                diagnosticWindow = new Window
                {
                    Width = 1000,
                    Height = 700,
                    Left = -32000,
                    Top = -32000,
                    Opacity = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Content = analysisView,
                };
                diagnosticWindow.Show();
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                AssertDataGridThumbFitsTrackSlot(dailyGrid, "DailyGrid");
                AssertRoundedTableContentClip(dailyGrid, "DailyGrid");
                diagnosticWindow.Content = null;
                diagnosticWindow.Content = sourcesView;
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                AssertDataGridThumbFitsTrackSlot(
                    sourcesGrid,
                    "SourcesGrid");
                AssertRoundedTableContentClip(sourcesGrid, "SourcesGrid");
                await AssertPromptUsageProgressRendersAsync(host.Dispatcher);

                CustomTimeRange? committedRange = null;
                int cancelCount = 0;
                var rangePicker = new DateTimeRangePicker
                {
                    StartLocal = new DateTime(2026, 8, 1, 8, 0, 0),
                    EndExclusiveLocal = new DateTime(2026, 8, 1, 10, 0, 0),
                    TimeZone = TimeZoneInfo.Utc,
                    CommitCommand = new RelayCommand(parameter =>
                        committedRange = Assert.IsInstanceOfType<CustomTimeRange>(
                            parameter)),
                    CancelCommand = new RelayCommand(_ => cancelCount++)
                };
                diagnosticWindow.Content = null;
                diagnosticWindow.Content = rangePicker;
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                rangePicker.OpenForSelection();
                Assert.IsTrue(rangePicker.IsPopupOpen);
                Assert.AreEqual(
                    DateTimeRangePicker.PickerStage.StartDate,
                    rangePicker.ActiveStage);
                Assert.IsTrue(rangePicker.ClosesOnOutsideInput);
                Assert.IsTrue(rangePicker.IsCalendarPanelVisible);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                double calendarPanelHeight = rangePicker.PopupContentHeight;
                Assert.IsGreaterThan(0d, calendarPanelHeight);

                RaiseMouseButton(
                    rangePicker.CalendarInputSurface,
                    UIElement.PreviewMouseLeftButtonDownEvent);
                rangePicker.SelectDateForTest(new DateTime(2026, 8, 1));
                Assert.AreEqual(
                    DateTimeRangePicker.PickerStage.StartHour,
                    rangePicker.ActiveStage);
                Assert.IsFalse(rangePicker.IsHourPanelVisible);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Input);
                Assert.IsTrue(rangePicker.IsPopupOpen);
                Assert.IsTrue(rangePicker.IsCalendarPanelVisible);
                Assert.AreEqual(
                    calendarPanelHeight,
                    rangePicker.PopupContentHeight,
                    0.5d,
                    "鼠标仍按下时不应缩短弹层边界。");

                RaiseMouseButton(
                    rangePicker.CalendarInputSurface,
                    UIElement.PreviewMouseLeftButtonUpEvent);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Input);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                Assert.IsTrue(rangePicker.IsPopupOpen);
                Assert.IsFalse(rangePicker.IsCalendarPanelVisible);
                Assert.IsTrue(rangePicker.IsHourPanelVisible);
                Assert.IsTrue(
                    rangePicker.PopupContentHeight < calendarPanelHeight,
                    "释放鼠标后小时面板应恢复自身较短的真实弹层边界。");
                Assert.HasCount(24, rangePicker.VisibleHourOptions);
                CollectionAssert.AreEqual(
                    Enumerable.Range(0, 24)
                        .Select(hour => $"{hour:00}:00")
                        .ToArray(),
                    rangePicker.VisibleHourOptions
                        .Select(option => option.Label)
                        .ToArray());
                rangePicker.SelectHourForTest(9);
                Assert.AreEqual(
                    DateTimeRangePicker.PickerStage.EndDate,
                    rangePicker.ActiveStage);
                RaiseMouseButton(
                    rangePicker.CalendarInputSurface,
                    UIElement.PreviewMouseLeftButtonDownEvent);
                rangePicker.SelectDateForTest(new DateTime(2026, 8, 1));
                Assert.AreEqual(
                    DateTimeRangePicker.PickerStage.EndHour,
                    rangePicker.ActiveStage);
                Assert.IsFalse(rangePicker.IsHourPanelVisible);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Input);
                Assert.IsTrue(rangePicker.IsPopupOpen);
                Assert.IsTrue(rangePicker.IsCalendarPanelVisible);
                RaiseMouseButton(
                    rangePicker.CalendarInputSurface,
                    UIElement.PreviewMouseLeftButtonUpEvent);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Input);
                Assert.IsTrue(rangePicker.IsHourPanelVisible);
                Assert.IsTrue(rangePicker.VisibleHourOptions
                    .Where(option => option.Hour <= 9)
                    .All(option => !option.IsEnabled));
                Assert.IsTrue(rangePicker.VisibleHourOptions
                    .Single(option => option.Hour == 10)
                    .IsEnabled);
                rangePicker.SelectHourForTest(10);
                Assert.IsNotNull(committedRange);
                Assert.AreEqual(
                    new DateTime(2026, 8, 1, 9, 0, 0),
                    committedRange.StartLocal);
                Assert.AreEqual(
                    new DateTime(2026, 8, 1, 10, 0, 0),
                    committedRange.EndExclusiveLocal);
                Assert.IsFalse(rangePicker.IsPopupOpen);
                StringAssert.Contains(rangePicker.TimeZoneDescription, "UTC");
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                rangePicker.IsSelectionPending = true;
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.IsTrue(rangePicker.IsPopupOpen);
                Assert.IsTrue(rangePicker.IsOpenedForPendingSelection);
                Assert.IsFalse(rangePicker.IsCommittedDraft);
                rangePicker.CancelForTest();
                Assert.AreEqual(1, cancelCount);
                rangePicker.IsSelectionPending = false;

                var settingsView = new SettingsView { DataContext = settings };
                diagnosticWindow.Content = null;
                diagnosticWindow.DataContext = main;
                diagnosticWindow.Content = settingsView;
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                Layout(settingsView, 1000, 700);
                AssertSettingsPriceLayout(settingsView);
                Assert.IsNull(
                    Assert.IsInstanceOfType<ScrollViewer>(
                        settingsView.FindName("SettingsScrollViewer"))
                        .FocusVisualStyle);
                settings.OpenSettingsSectionCommand.Execute(
                    SettingsSection.DataAndBackup);
                Layout(settingsView, 1000, 700);
                foreach (string metric in new[]
                         {
                             "数据库大小",
                             "统计调用数",
                             "数据时间范围",
                             "最近备份"
                         })
                {
                    Assert.IsTrue(
                        EnumerateVisibleText(settingsView).Contains(metric),
                        $"数据与备份页缺少核心指标：{metric}");
                }
                Button createBackupButton = Assert.IsInstanceOfType<Button>(
                    settingsView.FindName("CreateBackupButton"));
                Button restoreBackupButton = Assert.IsInstanceOfType<Button>(
                    settingsView.FindName("RestoreBackupButton"));
                Button cancelBackupButton = Assert.IsInstanceOfType<Button>(
                    settingsView.FindName("CancelBackupButton"));
                Assert.AreSame(main.CreateBackupCommand, createBackupButton.Command);
                Assert.AreSame(main.RestoreBackupCommand, restoreBackupButton.Command);
                Assert.AreSame(main.CancelBackupCommand, cancelBackupButton.Command);
                Assert.IsFalse(cancelBackupButton.IsVisible);
                Assert.AreEqual(
                    1,
                    EnumerateVisibleText(settingsView).Count(static text =>
                        text.Contains(
                            "备份可能包含本地项目路径",
                            StringComparison.Ordinal)),
                    "备份敏感性提示应只保留一条。");
                Layout(settingsView, 868, 640);
                Point backupButtonRight = createBackupButton
                    .TransformToAncestor(settingsView)
                    .Transform(new Point(createBackupButton.ActualWidth, 0));
                FrameworkElement settingsContent =
                    Assert.IsInstanceOfType<FrameworkElement>(
                        settingsView.FindName("SettingsContentGrid"));
                Point settingsContentRight = settingsContent
                    .TransformToAncestor(settingsView)
                    .Transform(new Point(settingsContent.ActualWidth, 0));
                Assert.IsLessThanOrEqualTo(
                    settingsContentRight.X,
                    backupButtonRight.X,
                    "900×640 主窗口下备份按钮不得横向裁切。");
                Assert.IsFalse(settings.IsDataStorageExpanded);
                Assert.IsFalse(settings.IsDangerousDataActionsExpanded);
                Assert.IsFalse(
                    Assert.IsInstanceOfType<TextBox>(
                        settingsView.FindName("DatabasePathTextBox")).IsVisible,
                    "数据库路径默认应收进高级设置。");
                settings.IsDataStorageExpanded = true;
                Layout(settingsView, 1000, 700);
                AssertDatabasePathBinding(settingsView, databasePath);
                Assert.IsTrue(
                    Assert.IsInstanceOfType<TextBox>(
                        settingsView.FindName("DatabasePathTextBox")).IsVisible);
                Assert.AreEqual(
                    Visibility.Visible,
                    Assert.IsInstanceOfType<Button>(
                        settingsView.FindName("ManualCodexRescanButton"))
                        .Visibility,
                    "设置页应始终保留手动重新扫描入口。");
                Button clearStatisticsButton = Assert.IsInstanceOfType<Button>(
                    settingsView.FindName("ClearStatisticsButton"));
                Assert.IsFalse(
                    clearStatisticsButton.IsVisible,
                    "清除统计默认应收进危险操作。");
                settings.IsDangerousDataActionsExpanded = true;
                Layout(settingsView, 1000, 700);
                Assert.IsTrue(clearStatisticsButton.IsVisible);
                Assert.AreSame(
                    main.ClearStatisticsCommand,
                    clearStatisticsButton.Command,
                    "清除统计必须通过 MainViewModel 的只读 UI→Core 维护命令执行。");
                Assert.IsTrue(
                    clearStatisticsButton.IsEnabled,
                    "Parser 重扫待处理状态下仍应允许用户启动安全清除维护。");
                await AssertSettingsPriceBindingAndSelectionAsync(
                    host.Dispatcher,
                    diagnosticWindow,
                    databasePath);
                diagnosticWindow.Content = null;
                diagnosticWindow.DataContext = main;
                diagnosticWindow.Content = settingsView;
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                await AssertLoadingProgressAnimationFollowsVisibilityAsync(
                    diagnosticWindow);
                diagnosticWindow.Content = null;
                diagnosticWindow.Close();
                diagnosticWindow = null;
                await AssertProjectsPageBreakdownLayoutAsync(host.Dispatcher);

                string renderedText = string.Join(
                    "\n",
                    EnumerateVisibleText(window)
                        .Concat(EnumerateVisibleText(dashboardView))
                        .Concat(EnumerateVisibleText(sourcesView))
                        .Concat(EnumerateVisibleText(settingsView)));
                foreach (string forbidden in new[]
                {
                    "额度",
                    "联网",
                    "本地 · 零外联",
                    "完全退出",
                    "API 等值费用",
                    "项目与会话",
                    "图表占位区域"
                })
                {
                    Assert.IsFalse(
                        renderedText.Contains(forbidden, StringComparison.Ordinal),
                        $"界面不应包含未实现入口或文案：{forbidden}");
                }

                ListBoxItem sourceItem = Assert.IsInstanceOfType<ListBoxItem>(
                    navigation.ItemContainerGenerator.ContainerFromIndex(4));
                Button sourceButton = FindVisualChild<Button>(sourceItem) ??
                    throw new AssertFailedException("数据来源导航按钮未生成。");
                Assert.IsNotNull(sourceButton.Command);
                sourceItem.ApplyTemplate();
                Border itemSurface = Assert.IsInstanceOfType<Border>(
                    sourceItem.Template.FindName("ItemSurface", sourceItem));
                Assert.AreEqual(
                    Brushes.Transparent,
                    itemSurface.Background,
                    "导航外层 ListBoxItem 不应保留系统淡蓝选中背景。");
                Assert.AreEqual(
                    new Thickness(0),
                    itemSurface.BorderThickness,
                    "导航外层 ListBoxItem 不应绘制系统选中边框。");
                sourceButton.ApplyTemplate();
                Assert.IsNotNull(
                    sourceButton.Template.FindName(
                        "SelectedOverlay",
                        sourceButton),
                    "导航仍应由内层按钮提供灰色圆角选中背景。");
                Assert.IsNull(
                    sourceButton.Template.FindName(
                        "SelectedIndicator",
                        sourceButton),
                    "导航选中态不应继续依赖左侧强调条。");
                sourceButton.Command.Execute(sourceButton.CommandParameter);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Layout(window, 1280, 800);
                Assert.AreSame(sources, content.Content);
                Assert.IsNotNull(FindVisualChild<SourcesView>(content));

                foreach ((double width, double height) in new[]
                {
                    (900d, 640d),
                    (1280d, 720d),
                    (1536d, 864d),
                })
                {
                    window.Width = width;
                    window.Height = height;
                    foreach (PageViewModel page in main.Pages)
                    {
                        await main.NavigateCommand.ExecuteAsync(page);
                        await System.Windows.Threading.Dispatcher.Yield(
                            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Layout(window, width, height);
                        Assert.AreSame(page, content.Content);
                        Assert.AreEqual(
                            window.ActualWidth - 122d,
                            mainContentSurface.ActualWidth,
                            1d,
                            $"{page.Title} 在 {width}×{height} 下应保持固定外壳起点。");
                        Assert.IsGreaterThan(
                            0d,
                            content.ActualWidth,
                            $"{page.Title} 在 {width}×{height} 下应具有可见内容宽度。");
                        Assert.IsGreaterThan(
                            0d,
                            content.ActualHeight,
                            $"{page.Title} 在 {width}×{height} 下应具有可见内容高度。");
                        FrameworkElement renderedPage =
                            FindRenderedPage(content, page);
                        AssertRoundedCardsHaveNoShadowEffects(
                            renderedPage,
                            page.Title);
                        Assert.AreEqual(
                            content.ActualWidth,
                            renderedPage.ActualWidth,
                            1d,
                            $"{page.Title} 在连续窗口尺寸变化后应重新填满页面宿主宽度。");
                        Assert.AreEqual(
                            content.ActualHeight,
                            renderedPage.ActualHeight,
                            1d,
                            $"{page.Title} 在连续窗口尺寸变化后应重新填满页面宿主高度。");
                        if (FindVisualChild<DashboardView>(content) is { } renderedDashboard)
                        {
                            AssertTopFilterLayout(
                                renderedDashboard,
                                "DashboardFilterBar",
                                "DashboardProjectFilter");
                        }

                        if (FindVisualChild<AnalysisView>(content) is { } renderedAnalysis)
                        {
                            AssertTopFilterLayout(
                                renderedAnalysis,
                                "AnalysisFilterBar",
                                "AnalysisProjectFilter");
                        }

                        if (FindVisualChild<SessionsView>(content) is { } renderedSessions)
                        {
                            AssertTopFilterLayout(
                                renderedSessions,
                                "SessionsFilterBar",
                                "SessionsProjectFilter");
                            AssertSessionsLayout(renderedSessions);
                        }

                        if (FindVisualChild<ProjectsView>(content) is { } renderedProjects)
                        {
                            AssertProjectsLayout(renderedProjects);
                        }

                        if (FindVisualChild<SettingsView>(content) is { } renderedSettings)
                        {
                            AssertSettingsPriceLayout(renderedSettings);
                        }
                    }
                }

                window.Width = 930d;
                window.Height = 650d;
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                window.Close();
                windowClosed = true;
                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(
                    new UiWindowSize(930d, 650d),
                    preferencesStore.ReadWindowSize());
                main.Dispose();
                main = null;

                reopenedWindow = App.ComposeWindow(
                    databasePath,
                    host.Dispatcher,
                    runtime,
                    "AgenTally Dev",
                    isDevelopment: true,
                    channel: AgenTallyChannel.Development,
                    preferencesStore: preferencesStore);
                reopenedMain = Assert.IsInstanceOfType<MainViewModel>(
                    reopenedWindow.DataContext);
                Assert.AreEqual(930d, reopenedWindow.Width, 0.5d);
                Assert.AreEqual(650d, reopenedWindow.Height, 0.5d);
            }
            finally
            {
                diagnosticWindow?.Close();
                reopenedWindow?.Close();
                if (!windowClosed)
                {
                    window?.Close();
                }

                reopenedMain?.Dispose();
                main?.Dispose();
                app.Shutdown();
            }
        });
    }

    private static async Task AssertLoadingProgressAnimationFollowsVisibilityAsync(
        Window diagnosticWindow)
    {
        Style loadingStyle = Assert.IsInstanceOfType<Style>(
            Application.Current.FindResource("LoadingProgressBarStyle"));
        var progress = new ProgressBar
        {
            Style = loadingStyle,
            Visibility = Visibility.Collapsed,
        };
        diagnosticWindow.Content = null;
        diagnosticWindow.Content = progress;
        progress.ApplyTemplate();
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Render);
        Border indicator = Assert.IsInstanceOfType<Border>(
            progress.Template.FindName("Indicator", progress));
        TranslateTransform transform =
            Assert.IsInstanceOfType<TranslateTransform>(
                indicator.RenderTransform);
        AssertLoadingProgressAnimationDefinition(loadingStyle, progress.Template);
        Assert.AreEqual(120d, progress.Width, "可见性门控不得改变加载条宽度。");
        Assert.AreEqual(3d, progress.Height, "可见性门控不得改变加载条高度。");
        Assert.AreSame(
            Application.Current.FindResource("SurfaceRaisedBrush"),
            progress.Background,
            "可见性门控不得改变加载条轨道颜色。");
        Assert.AreSame(
            Application.Current.FindResource("AccentBrush"),
            progress.Foreground,
            "可见性门控不得改变加载条前景颜色。");
        Assert.IsFalse(
            transform.HasAnimatedProperties,
            "隐藏的加载进度条不得保留无限动画时钟。");
        Assert.IsFalse(
            progress.IsIndeterminate,
            "隐藏的加载进度条应退出 Indeterminate 状态。");

        progress.Visibility = Visibility.Visible;
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        Assert.IsTrue(
            progress.IsVisible,
            "加载进度条显示时应进入现有可见动画状态。");
        Assert.IsTrue(
            progress.IsIndeterminate,
            "可见性门控不得改变加载进度条原有的 Indeterminate 状态。");

        progress.Visibility = Visibility.Collapsed;
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        Assert.IsFalse(
            transform.HasAnimatedProperties,
            "加载进度条再次隐藏后应移除无限动画时钟。");
        Assert.IsFalse(
            progress.IsIndeterminate,
            "加载进度条再次隐藏后应退出 Indeterminate 状态。");
        diagnosticWindow.Content = null;
    }

    private static void AssertSystemFocusVisualSuppressed(Application app)
    {
        Style focusVisualStyle = Assert.IsInstanceOfType<Style>(
            app.TryFindResource(SystemParameters.FocusVisualStyleKey));
        Setter templateSetter = focusVisualStyle.Setters
            .OfType<Setter>()
            .Single(setter => setter.Property == Control.TemplateProperty);
        ControlTemplate template =
            Assert.IsInstanceOfType<ControlTemplate>(templateSetter.Value);
        FrameworkElement focusVisual =
            Assert.IsInstanceOfType<FrameworkElement>(template.LoadContent());

        Assert.AreEqual(
            0d,
            focusVisual.Opacity,
            "系统焦点装饰模板必须完全不可见，不能再绘制虚线框。");
        Assert.IsFalse(
            focusVisual.IsHitTestVisible,
            "隐藏的焦点装饰不应参与命中测试。");
    }

    private static void AssertLoadingProgressAnimationDefinition(
        Style loadingStyle,
        ControlTemplate template)
    {
        Assert.HasCount(1, loadingStyle.Triggers);
        DataTrigger visibilityTrigger = Assert.IsInstanceOfType<DataTrigger>(
            loadingStyle.Triggers[0]);
        Binding visibilityBinding = Assert.IsInstanceOfType<Binding>(
            visibilityTrigger.Binding);
        Assert.AreEqual(
            "IsVisible",
            visibilityBinding.Path?.Path,
            "加载动画门控必须绑定控件的实际可见性。");
        Assert.AreEqual(
            RelativeSourceMode.Self,
            visibilityBinding.RelativeSource?.Mode,
            "加载动画门控必须读取控件自身的实际可见性。");
        Assert.AreEqual("True", visibilityTrigger.Value);
        Assert.HasCount(1, visibilityTrigger.Setters);
        Setter indeterminateSetter = Assert.IsInstanceOfType<Setter>(
            visibilityTrigger.Setters[0]);
        Assert.AreEqual(
            ProgressBar.IsIndeterminateProperty,
            indeterminateSetter.Property);
        Assert.AreEqual(true, indeterminateSetter.Value);

        Assert.HasCount(1, template.Triggers);
        Trigger animationTrigger = Assert.IsInstanceOfType<Trigger>(
            template.Triggers[0]);
        Assert.AreEqual(
            ProgressBar.IsIndeterminateProperty,
            animationTrigger.Property);
        Assert.AreEqual(true, animationTrigger.Value);
        Assert.HasCount(1, animationTrigger.EnterActions);
        BeginStoryboard begin = Assert.IsInstanceOfType<BeginStoryboard>(
            animationTrigger.EnterActions[0]);
        Assert.AreEqual("IndeterminateSlide", begin.Name);
        Storyboard? storyboard = begin.Storyboard;
        Assert.IsNotNull(storyboard);
        Assert.AreEqual(
            RepeatBehavior.Forever,
            storyboard.RepeatBehavior,
            "可见加载动画必须保持无限循环。");
        Assert.HasCount(1, storyboard.Children);
        DoubleAnimation animation = Assert.IsInstanceOfType<DoubleAnimation>(
            storyboard.Children[0]);
        Assert.AreEqual(-40d, animation.From);
        Assert.AreEqual(120d, animation.To);
        Assert.AreEqual(TimeSpan.FromSeconds(1.2d), animation.Duration.TimeSpan);
        Assert.AreEqual("Indicator", Storyboard.GetTargetName(animation));
        PropertyPath? targetProperty = Storyboard.GetTargetProperty(animation);
        Assert.IsNotNull(targetProperty);
        Assert.AreEqual("(0).(1)", targetProperty.Path);
        Assert.HasCount(2, targetProperty.PathParameters);
        Assert.AreEqual(
            UIElement.RenderTransformProperty,
            targetProperty.PathParameters[0]);
        Assert.AreEqual(
            TranslateTransform.XProperty,
            targetProperty.PathParameters[1]);
        CubicEase easing = Assert.IsInstanceOfType<CubicEase>(
            animation.EasingFunction);
        Assert.AreEqual(EasingMode.EaseInOut, easing.EasingMode);
        Assert.HasCount(1, animationTrigger.ExitActions);
        RemoveStoryboard remove = Assert.IsInstanceOfType<RemoveStoryboard>(
            animationTrigger.ExitActions[0]);
        Assert.AreEqual("IndeterminateSlide", remove.BeginStoryboardName);
    }

    private static async Task AssertProjectsPageBreakdownLayoutAsync(
        System.Windows.Threading.Dispatcher dispatcher)
    {
        const string projectId = "0123456789abcdef01234567";
        const long total = 9_876_543_210;
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

        static MetricAggregate Known(long value, int records = 12) =>
            new(value, records, 0);

        static UsageMetricSet Metrics(
            long value,
            int records = 12,
            bool missingCacheWrite = false) => new(
            Known(value / 2, records),
            Known(value / 4, records),
            Known(value / 4, records),
            missingCacheWrite
                ? new MetricAggregate(null, 0, records)
                : Known(0, records),
            Known(value / 8, records),
            Known(value / 16, records),
            Known(0, records),
            Known(value, records),
            Known(value, records));

        var pricing = new PricingAggregate(
            1_234.5678m,
            CompleteRecords: 10,
            PartialRecords: 1,
            UnpricedRecords: 1,
            MissingCategories: PricingMissingCategory.CacheWriteTokens);
        UsageMetricSet projectMetrics = Metrics(
            total,
            records: 12,
            missingCacheWrite: true);
        var project = new ProjectUsageRow(
            projectId,
            @"C:\Projects\AgenTally-with-an-intentionally-long-project-folder-name",
            PathAvailability.Available,
            now.AddDays(-15),
            now,
            123_456,
            12_345,
            projectMetrics)
        {
            Pricing = pricing
        };
        var platform = new AgentUsageRow(
            "codex-platform-with-an-intentionally-long-display-name",
            123_456,
            Known(total),
            Known(total / 4),
            Known(total / 8),
            Known(total / 2),
            new MetricAggregate(null, 0, 12))
        {
            Metrics = projectMetrics,
            Pricing = pricing,
            StartedAtUtc = now.AddDays(-15),
            LastActivityUtc = now
        };
        long[] modelTotals =
        [
            8_000_000_000,
            800_000_000,
            400_000_000,
            250_000_000,
            180_000_000,
            120_000_000,
            80_000_000,
            46_543_210
        ];
        string[] modelNames =
        [
            "gpt-5.6-sol-with-an-intentionally-long-context-suffix",
            "codex-auto-review",
            "unknown-model-with-a-long-provider-qualified-name",
            "gpt-5.5-codex",
            "gpt-5.4-mini",
            "gpt-5.3-reasoning",
            "legacy-model-with-a-long-name",
            "another-provider-qualified-model"
        ];
        AgentModelUsageRow[] models = modelNames
            .Select((name, index) =>
            {
                long modelTotal = modelTotals[index];
                return new AgentModelUsageRow(
                    platform.AgentId,
                    name,
                    100_000 + index,
                    Known(modelTotal),
                    Known(modelTotal / 4),
                    Known(modelTotal / 8),
                    Known(modelTotal / 2),
                    new MetricAggregate(null, 0, 12))
                {
                    Metrics = Metrics(
                        modelTotal,
                        records: 12,
                        missingCacheWrite: true),
                    Pricing = pricing,
                    StartedAtUtc = now.AddDays(-15),
                    LastActivityUtc = now
                };
            })
            .ToArray();
        var overview = new UsageOverview(
            123_456,
            Known(total),
            Known(total / 4),
            Known(total / 8),
            Known(total / 2),
            new MetricAggregate(null, 0, 12),
            now)
        {
            Metrics = projectMetrics,
            Pricing = pricing
        };
        var queries = new FakeUsageQueryService
        {
            ProjectsResult = [project],
            AgentModels = models,
            RootSessionsResult = new RootSessionPage(
                [
                    new RootSessionSummaryRow(
                        new RootSessionIdentity(
                            "codex",
                            "codex:windows:project-layout",
                            "root-project-layout"),
                        now.AddHours(-2),
                        now,
                        projectId,
                        project.ProjectPath,
                        PathAvailability.Available,
                        12,
                        0,
                        Metrics(21_063_137))
                    {
                        SessionName = "继续完成首页前端开发，先完整阅读中文说明"
                    }
                ],
                null)
        };
        queries.SetProjectRoute(
            projectId,
            Task.FromResult(
                new DashboardQueryResult(
                    overview,
                    [],
                    [],
                    [],
                    [platform])));

        Window? window = null;
        try
        {
            var viewModel = new ProjectsViewModel(
                queries,
                dispatcher,
                new FixedTimeProvider(now),
                TimeZoneInfo.Utc);
            await viewModel.RefreshAsync(CancellationToken.None);
            Assert.IsNotNull(viewModel.Detail);
            Assert.HasCount(1, viewModel.Detail.Platforms);
            Assert.HasCount(8, viewModel.Detail.Models);

            var view = new ProjectsView { DataContext = viewModel };
            var layoutHost = new Canvas();
            layoutHost.Children.Add(view);
            window = new Window
            {
                Content = layoutHost,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000
            };
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Render);

            var projectListWidths = new List<double>();
            foreach ((double width, double height) in new[]
            {
                (808d, 560d),
                (872d, 600d),
                (1384d, 920d)
            })
            {
                viewModel.SelectedDetailTabIndex = 0;
                view.Width = width;
                view.Height = height;
                window.UpdateLayout();
                AssertProjectsLayout(view);
                Grid projectsContent = Assert.IsInstanceOfType<Grid>(
                    view.FindName("ProjectsContent"));
                projectListWidths.Add(
                    projectsContent.ColumnDefinitions[0].ActualWidth);

                string[] summaryText = EnumerateVisibleText(view).ToArray();
                Assert.IsFalse(
                    summaryText.Contains(viewModel.Detail.DataNote!),
                    "项目摘要不应继续显示数据状态说明。");
                Assert.IsFalse(
                    summaryText.Contains(viewModel.Detail.PriceCaption),
                    "项目摘要不应再在价格下面重复显示计价范围说明。");

                TabControl tabs = Assert.IsInstanceOfType<TabControl>(
                    view.FindName("ProjectDetailTabs"));
                await AssertProjectTabSwitchKeepsDetailAtTopAsync(
                    window,
                    view,
                    tabs,
                    "ProjectPlatformsTab");
                window.UpdateLayout();
                AssertStackedProjectBreakdown(
                    view,
                    "ProjectPlatformLayout",
                    "ProjectPlatformShareCard",
                    "ProjectPlatformDetailCard",
                    "ProjectPlatformList",
                    expectedRows: 1);
                if (width == 808d)
                {
                    AssertProjectShareWheelRouting(
                        view,
                        "ProjectPlatformList",
                        expectInnerScroll: false);
                }

                await AssertProjectTabSwitchKeepsDetailAtTopAsync(
                    window,
                    view,
                    tabs,
                    "ProjectModelsTab");
                window.UpdateLayout();
                AssertStackedProjectBreakdown(
                    view,
                    "ProjectModelLayout",
                    "ProjectModelShareCard",
                    "ProjectModelDetailCard",
                    "ProjectModelList",
                    expectedRows: 8);
                if (width == 808d)
                {
                    AssertProjectShareWheelRouting(
                        view,
                        "ProjectModelList",
                        expectInnerScroll: true);
                }

                await AssertProjectTabSwitchKeepsDetailAtTopAsync(
                    window,
                    view,
                    tabs,
                    "ProjectSessionsTab");
                ItemsControl sessionItems = Assert.IsInstanceOfType<ItemsControl>(
                    view.FindName("ProjectSessionItems"));
                Button sessionButton = Assert.ContainsSingle(
                    FindVisualChildren<Button>(sessionItems));
                Assert.IsTrue(
                    double.IsNaN(sessionButton.Height),
                    "项目会话项必须覆盖通用按钮的固定高度，按两行文字自然测量。");
                Assert.IsGreaterThan(
                    36d,
                    sessionButton.ActualHeight,
                    "项目会话项必须为中文标题和活动摘要保留完整垂直空间。");
                await AssertProjectTabSwitchKeepsDetailAtTopAsync(
                    window,
                    view,
                    tabs,
                    "ProjectOverviewTab");
                if (width == 808d)
                {
                    ScrollViewer detailScroll =
                        Assert.IsInstanceOfType<ScrollViewer>(
                            view.FindName("ProjectsDetailScrollViewer"));
                    UsageTrendChart trendChart =
                        Assert.IsInstanceOfType<UsageTrendChart>(
                            view.FindName("ProjectUsageTrendChart"));
                    trendChart.BringIntoView();
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Render);
                    Assert.IsGreaterThan(
                        0d,
                        detailScroll.VerticalOffset,
                        "仅应阻止项目详情滚动容器自身的自动定位，" +
                        "页签内容仍须能够正常滚入可视区域。");
                    Assert.IsTrue(trendChart.AllowHoverCardOutsidePlot);
                }
            }

            Assert.AreEqual(
                280d,
                projectListWidths[0],
                0.5d,
                "窄窗口下项目左栏应收缩到与会话页一致的 280 DIP 下限。");
            Assert.IsGreaterThan(
                projectListWidths[0],
                projectListWidths[^1],
                "宽窗口下项目左栏应按比例增长，不能继续固定为单一宽度。");
            Assert.IsLessThanOrEqualTo(
                360.5d,
                projectListWidths[^1],
                "项目左栏动态增长不得超过与会话页一致的 360 DIP 上限。");
        }
        finally
        {
            window?.Close();
        }
    }

    private static async Task AssertProjectTabSwitchKeepsDetailAtTopAsync(
        Window window,
        ProjectsView view,
        TabControl tabs,
        string tabName)
    {
        ScrollViewer detailScroll = Assert.IsInstanceOfType<ScrollViewer>(
            view.FindName("ProjectsDetailScrollViewer"));
        TabItem targetTab = Assert.IsInstanceOfType<TabItem>(
            view.FindName(tabName));
        targetTab.ApplyTemplate();
        FrameworkElement targetTabSurface =
            Assert.IsInstanceOfType<FrameworkElement>(
                targetTab.Template.FindName("TabBorder", targetTab));
        detailScroll.ScrollToTop();
        window.UpdateLayout();

        var automaticOffsets = new List<double>();
        ScrollChangedEventHandler recordAutomaticOffset = (_, _) =>
        {
            if (detailScroll.VerticalOffset > 0.5d)
            {
                automaticOffsets.Add(detailScroll.VerticalOffset);
            }
        };
        detailScroll.ScrollChanged += recordAutomaticOffset;
        var mouseDown = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice,
            Environment.TickCount,
            System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent,
            Source = targetTabSurface
        };
        targetTabSurface.RaiseEvent(mouseDown);
        Assert.AreSame(
            targetTab,
            tabs.SelectedItem,
            $"点击后应切换到 {targetTab.Header}。");
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Render);
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        detailScroll.ScrollChanged -= recordAutomaticOffset;

        Assert.IsEmpty(
            automaticOffsets,
            $"{targetTab.Header} 切换期间项目详情不应自动下移，实际偏移：" +
            $"{string.Join(", ", automaticOffsets)}。");
        Assert.AreEqual(
            0d,
            detailScroll.VerticalOffset,
            0.5d,
            $"切换到 {targetTab.Header} 后项目详情应保持顶部位置。");
    }

    [TestMethod]
    public async Task UsageTrendChart_RendersEmptySingleNullZeroAndLargeInputs()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            var chart = new UsageTrendChart();
            DateTimeOffset start =
                new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);
            IReadOnlyList<IReadOnlyList<UsageTrendPoint>> cases =
            [
                [],
                [Point(start, 0, 0, 0, 0)],
                [
                    Point(start, null, 0, null, 0),
                    Point(start.AddMinutes(10), 10, null, 0, 5)
                ],
                Enumerable.Range(0, 1000)
                    .Select(index => Point(
                        start.AddMinutes(index),
                        index,
                        index % 3 == 0 ? null : index / 2,
                        0,
                        index / 4))
                    .ToArray()
            ];

            bool totalSeriesPixelFound = false;
            bool outputSeriesPixelFound = false;
            foreach (IReadOnlyList<UsageTrendPoint> points in cases)
            {
                chart.Points = points;
                Layout(chart, 900, 280);
                var bitmap = new RenderTargetBitmap(
                    900,
                    280,
                    96,
                    96,
                    PixelFormats.Pbgra32);
                bitmap.Render(chart);
                byte[] pixels = new byte[900 * 280 * 4];
                bitmap.CopyPixels(pixels, 900 * 4, 0);
                for (int index = 0; index < pixels.Length; index += 4)
                {
                    byte blue = pixels[index];
                    byte green = pixels[index + 1];
                    byte red = pixels[index + 2];
                    if (blue > 170 && green is > 65 and < 165 && red < 115)
                    {
                        totalSeriesPixelFound = true;
                    }

                    if (blue > 140 && green is > 55 and < 150 && red is > 90 and < 180)
                    {
                        outputSeriesPixelFound = true;
                    }
                }

                if (points.Count == 1000)
                {
                    TrendHoverCardPresentation midpoint =
                        chart.CreateHoverPresentation(500d / 999d) ??
                        throw new AssertFailedException(
                            "趋势点应生成悬停说明。");
                    StringAssert.Contains(
                        midpoint.TotalText,
                        "500 Token",
                        "大数据集悬停仍须命中未经抽样的原始点。");
                }
            }

            chart.Points =
            [
                Point(start, 100_000_000, 0, 750_000, 0),
                Point(start.AddMinutes(10), 60_000_000, 0, 1_500_000, 0),
                Point(start.AddMinutes(20), 140_000_000, 0, 500_000, 0),
            ];
            Layout(chart, 900, 280);
            var separatedScaleBitmap = new RenderTargetBitmap(
                900,
                280,
                96,
                96,
                PixelFormats.Pbgra32);
            separatedScaleBitmap.Render(chart);
            byte[] separatedScalePixels = new byte[900 * 280 * 4];
            separatedScaleBitmap.CopyPixels(
                separatedScalePixels,
                900 * 4,
                0);
            TrendHoverCardPresentation peak = chart.CreateHoverPresentation(1d) ??
                throw new AssertFailedException("趋势点应生成悬停说明。");
            StringAssert.Contains(
                peak.IntervalText,
                start.AddMinutes(20).ToLocalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture));
            StringAssert.Contains(peak.TotalText, "140,000,000 Token");
            for (int index = 0; index < separatedScalePixels.Length; index += 4)
            {
                byte blue = separatedScalePixels[index];
                byte green = separatedScalePixels[index + 1];
                byte red = separatedScalePixels[index + 2];
                if (blue > 140 &&
                    green is > 55 and < 150 &&
                    red is > 90 and < 180)
                {
                    outputSeriesPixelFound = true;
                    break;
                }
            }

            Assert.IsTrue(totalSeriesPixelFound, "图表应绘制可辨识的蓝色总量序列。");
            Assert.IsTrue(
                outputSeriesPixelFound,
                "输出量级远小于总量时仍应通过独立纵轴绘制可辨识序列。");
        });
    }

    [TestMethod]
    public async Task UsageHeatmap_FitsFullYearInsideNarrowViewport()
    {
        await using var host = new StaDispatcherTestHost();
        await host.InvokeAsync(() =>
        {
            var heatmap = new UsageHeatmap
            {
                Days = Enumerable.Range(0, 365)
                    .Select(index => new UsageHeatmapDay(
                        new DateTime(2025, 7, 29).AddDays(index),
                        index + 1,
                        1,
                        0))
                    .ToArray(),
            };

            Layout(heatmap, 240, 126);
            var bitmap = new RenderTargetBitmap(
                240,
                126,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(heatmap);
            byte[] pixels = new byte[240 * 126 * 4];
            bitmap.CopyPixels(pixels, 240 * 4, 0);
            bool rightEdgeDayFound = false;
            for (int y = 18; y < 126 && !rightEdgeDayFound; y++)
            {
                for (int x = 228; x < 240; x++)
                {
                    int offset = ((y * 240) + x) * 4;
                    if (pixels[offset + 3] > 0)
                    {
                        rightEdgeDayFound = true;
                        break;
                    }
                }
            }

            Assert.AreEqual(240d, heatmap.ActualWidth);
            Assert.AreEqual(126d, heatmap.ActualHeight);
            Assert.IsTrue(
                rightEdgeDayFound,
                "窄宽度下最后几周仍应在热力图可视边界内绘制。");
        });
    }

    [TestMethod]
    public void NullableNumberConverter_DistinguishesUnknownAndExplicitZero()
    {
        var converter = new NullableNumberConverter();

        Assert.AreEqual(
            "—",
            converter.Convert(
                null,
                typeof(string),
                null,
                CultureInfo.InvariantCulture));
        Assert.AreEqual(
            "0",
            converter.Convert(
                0L,
                typeof(string),
                null,
                CultureInfo.InvariantCulture));
        Assert.AreEqual(
            "1,200",
            converter.Convert(
                1200L,
                typeof(string),
                null,
                CultureInfo.InvariantCulture));
    }

    private static void AssertDatabasePathBinding(
        SettingsView view,
        string expectedPath)
    {
        TextBox pathBox = Assert.IsInstanceOfType<TextBox>(
            view.FindName("DatabasePathTextBox"));
        Binding binding = Assert.IsInstanceOfType<Binding>(
            BindingOperations.GetBinding(pathBox, TextBox.TextProperty));
        BindingExpression? expression =
            pathBox.GetBindingExpression(TextBox.TextProperty);
        Assert.IsNotNull(expression);
        Assert.AreEqual(BindingMode.OneWay, binding.Mode);
        Assert.IsFalse(expression.HasError);
        Assert.IsFalse(Validation.GetHasError(pathBox));
        Assert.AreEqual(expectedPath, pathBox.Text);
        Assert.IsTrue(pathBox.IsReadOnly);
    }

    private static void AssertSettingsPriceLayout(SettingsView view)
    {
        ScrollViewer scrollViewer = Assert.IsInstanceOfType<ScrollViewer>(
            view.FindName("SettingsScrollViewer"));
        Assert.AreEqual(
            ScrollBarVisibility.Disabled,
            scrollViewer.HorizontalScrollBarVisibility);
        Layout(view, 1400, Math.Max(680d, view.ActualHeight));
        Grid settingsContent = Assert.IsInstanceOfType<Grid>(
            view.FindName("SettingsContentGrid"));
        Assert.AreEqual(1500d, settingsContent.MaxWidth);
        Assert.AreEqual(
            HorizontalAlignment.Stretch,
            settingsContent.HorizontalAlignment);
        double availableContentWidth =
            scrollViewer.ViewportWidth -
            settingsContent.Margin.Left -
            settingsContent.Margin.Right;
        Assert.AreEqual(
            Math.Min(settingsContent.MaxWidth, availableContentWidth),
            settingsContent.ActualWidth,
            1d,
            "设置页内容应填满滚动视口扣除页面边距后的可用宽度。");
        if (availableContentWidth > 1100d)
        {
            Assert.IsGreaterThan(
                1100d,
                settingsContent.ActualWidth,
                "宽屏设置页必须随可用区域展开，不能按旧的 1100 DIP 内容宽度收缩。");
        }
        SettingsViewModel settings = Assert.IsInstanceOfType<SettingsViewModel>(
            view.DataContext);
        settings.ShowSettingsHome();
        Layout(view, 1400, Math.Max(680d, view.ActualHeight));
        UniformGrid categories = Assert.IsInstanceOfType<UniformGrid>(
            view.FindName("SettingsCategoryGrid"));
        Assert.AreEqual(
            settingsContent.ActualWidth >= SettingsView.TwoColumnBreakpoint ? 2 : 1,
            categories.Columns,
            "设置分类必须按实际可用内容宽度切换一列或两列。");
        Button[] categoryCards = categories.Children
            .OfType<Button>()
            .ToArray();
        Assert.HasCount(5, categoryCards);
        Assert.IsTrue(categoryCards.All(static card => card.Command is not null));
        Assert.IsTrue(categoryCards.All(static card => card.FocusVisualStyle is null));
        Assert.IsTrue(categoryCards.All(static card => card.MinHeight <= 72d));
        Assert.IsTrue(categoryCards.All(static card => card.Margin == new Thickness(6d)));
        CollectionAssert.AreEqual(
            new[]
            {
                "常规设置",
                "数据与备份",
                "模型与计价",
                "隐私与安全",
                "关于与更新"
            },
            EnumerateVisibleText(view)
                .Where(text => text is
                    "常规设置" or
                    "数据与备份" or
                    "模型与计价" or
                    "隐私与安全" or
                    "关于与更新")
                .ToArray());
        IReadOnlyList<string> homeText = EnumerateVisibleText(view).ToArray();
        Assert.IsFalse(homeText.Contains("版本更新"));
        Assert.IsFalse(homeText.Contains("重新扫描全部 Agent 统计"));
        Assert.IsFalse(homeText.Contains("模型价格"));
        Assert.IsFalse(homeText.Contains("刷新间隔 3 秒"));
        Assert.IsFalse(homeText.Contains("本地数据与维护"));
        Assert.IsFalse(homeText.Contains("本地处理，明确联网边界"));
        Assert.IsFalse(homeText.Any(static text => text.StartsWith(
            "版本 ",
            StringComparison.Ordinal)));

        Layout(view, 1000, Math.Max(680d, view.ActualHeight));
        Assert.AreEqual(
            settingsContent.ActualWidth >= SettingsView.TwoColumnBreakpoint ? 2 : 1,
            categories.Columns,
            "设置分类在重新布局后仍必须跟随真实内容宽度。");
        Assert.IsLessThanOrEqualTo(
            scrollViewer.ViewportWidth + 1d,
            scrollViewer.ExtentWidth,
            "设置首页单列布局不得产生水平滚动范围。");

        settings.OpenSettingsSectionCommand.Execute(SettingsSection.Pricing);
        Layout(view, 1400, Math.Max(680d, view.ActualHeight));
        Assert.IsNull(view.FindName("PriceManagerToggle"));
        Assert.IsNotNull(view.FindName("PriceManagerHeader"));
        Layout(
            view,
            Math.Max(760d, view.ActualWidth),
            Math.Max(680d, view.ActualHeight));
        AssertVirtualizedGrid(view, "PriceModelGrid");
        DataGrid priceGrid = Assert.IsInstanceOfType<DataGrid>(
            view.FindName("PriceModelGrid"));
        FrameworkElement priceGridHost = Assert.IsInstanceOfType<FrameworkElement>(
            priceGrid.Parent);
        Assert.IsInstanceOfType<RoundedClipBorder>(priceGridHost.Parent);
        Assert.HasCount(3, priceGrid.Columns);
        Assert.IsInstanceOfType<DataGridTemplateColumn>(priceGrid.Columns[0]);
        Assert.IsGreaterThan(0d, priceGrid.ActualWidth);
        Assert.IsFalse(priceGrid.CanUserSortColumns);
        Assert.IsGreaterThanOrEqualTo(180d, priceGrid.Columns[0].ActualWidth);
        TextBox search = Assert.IsInstanceOfType<TextBox>(
            view.FindName("PriceSearchTextBox"));
        Assert.IsGreaterThan(0d, search.ActualWidth);
        TextBox inputPrice = Assert.IsInstanceOfType<TextBox>(
            view.FindName("InputPriceTextBox"));
        Assert.IsNotNull(inputPrice.Style);
        Assert.IsNotNull(view.FindName("AllPriceFilterToggle"));
        Assert.IsNotNull(view.FindName("UnpricedPriceFilterToggle"));
        Assert.IsNotNull(view.FindName("CustomPriceFilterToggle"));
        Assert.IsNull(view.FindName("IdentityUnmatchedPriceFilterToggle"));
        Assert.IsNull(view.FindName("NewPriceModelTextBox"));
        IReadOnlyList<string> visibleText = EnumerateVisibleText(view).ToArray();
        Assert.IsFalse(visibleText.Contains("应用生命周期"));
        Assert.IsTrue(visibleText.Contains("模型与计价"));
        Assert.IsFalse(visibleText.Contains(
            "自定义价格只影响尚未计价和未来记录，已计价历史保持不变。"));
        Assert.IsFalse(visibleText.Contains(
            "价格设置仅保存在本机；UI 仍通过当前频道 Core 串行应用修改。"));
        foreach (string removedDescription in new[]
        {
            "开发版不连接真实版本渠道，仅显示当前策略。",
            "只读重新扫描全部已支持 Agent 的现有日志。所有来源完整成功后才会原子更新统计数据。",
            "建立全部已支持 Agent 的当前日志末尾基线后清除统计；保留自定义模型价格，不修改任何 Agent 原始日志。",
            "仅在本地数据变化后刷新当前页面；选择会自动保存"
        })
        {
            Assert.IsFalse(
                visibleText.Contains(removedDescription),
                $"正式设置页不应保留说明文字：{removedDescription}");
        }
        Layout(view, 808, Math.Max(680d, view.ActualHeight));
        Border editorPanel = Assert.IsInstanceOfType<Border>(
            view.FindName("PriceEditorPanel"));
        foreach (string buttonName in new[]
        {
            "DiscardPriceChangesButton",
            "RestoreSelectedPriceButton",
            "SavePriceChangesButton"
        })
        {
            Button button = Assert.IsInstanceOfType<Button>(
                view.FindName(buttonName));
            Rect bounds = button.TransformToAncestor(editorPanel)
                .TransformBounds(new Rect(button.RenderSize));
            Assert.IsGreaterThanOrEqualTo(
                0d,
                bounds.Left,
                $"{buttonName} 左侧不得被编辑器边界裁切。");
            Assert.IsLessThanOrEqualTo(
                editorPanel.ActualWidth,
                bounds.Right,
                $"{buttonName} 右侧不得越出编辑器边界。");
        }

        Assert.IsNotNull(view.FindName("RestoreAllPricesButton"));
        Assert.IsNotNull(view.FindName("LongContextPriceToggle"));
        Assert.IsNotNull(view.FindName("ManualVersionCheckButton"));
        Assert.IsNotNull(view.FindName("OpenReleasePageButton"));

        settings.OpenSettingsSectionCommand.Execute(SettingsSection.About);
        Layout(view, 1000, Math.Max(680d, view.ActualHeight));
        AssertSettingsActionAlignment(
            view,
            "VersionCheckTitleText",
            "ManualVersionCheckButton");

        settings.OpenSettingsSectionCommand.Execute(
            SettingsSection.DataAndBackup);
        settings.IsDataStorageExpanded = true;
        settings.IsDangerousDataActionsExpanded = true;
        Layout(view, 730, 560);
        UniformGrid dataOverview = Assert.IsInstanceOfType<UniformGrid>(
            view.FindName("DataOverviewGrid"));
        Assert.AreEqual(
            2,
            dataOverview.Columns,
            "数据概况应固定为两列，避免日期被四列布局截断。");
        Assert.HasCount(4, dataOverview.Children);
        Assert.IsTrue(dataOverview.Children
            .OfType<Border>()
            .All(static card => card.Margin == new Thickness(6d)));
        foreach (string valueName in new[]
                 {
                     "DataTimeRangeValueText",
                     "LastBackupValueText"
                 })
        {
            TextBlock value = Assert.IsInstanceOfType<TextBlock>(
                view.FindName(valueName));
            Assert.AreEqual(
                TextTrimming.None,
                value.TextTrimming,
                $"{valueName} 不得用省略号隐藏日期。");
            Assert.AreEqual(
                TextWrapping.Wrap,
                value.TextWrapping,
                $"{valueName} 在极窄内容下应换行而不是裁切。");
        }
        AssertSettingsActionAlignment(
            view,
            "RescanStatisticsTitleText",
            "ManualCodexRescanButton");
        AssertSettingsActionAlignment(
            view,
            "ClearStatisticsTitleText",
            "ClearStatisticsButton");

        settings.OpenSettingsSectionCommand.Execute(SettingsSection.General);
        Layout(view, 1000, Math.Max(680d, view.ActualHeight));
        ToggleButton startupToggle = Assert.IsInstanceOfType<ToggleButton>(
            view.FindName("StartupRegistrationToggle"));
        Assert.IsTrue(startupToggle.IsEnabled);
        Assert.IsNull(
            startupToggle.FocusVisualStyle,
            "开机自启开关不应额外绘制鼠标或键盘焦点框。");
        Border switchSurface = Assert.IsInstanceOfType<Border>(
            startupToggle.Template.FindName("SwitchSurface", startupToggle));
        Assert.AreEqual(
            new Thickness(0d),
            switchSurface.BorderThickness,
            "鼠标点击开关后，控件模板不得显示持续选中外框。");
        Assert.AreEqual(
            settings.IsStartupEnabled,
            startupToggle.IsChecked == true);
        IReadOnlyList<string> generalText = EnumerateVisibleText(view).ToArray();
        Assert.IsTrue(generalText.Contains("开机自启"));
        Assert.IsTrue(generalText.Contains(
            "Development 模拟，不修改 Windows"));
        if (startupToggle.IsChecked != true)
        {
            startupToggle.IsChecked = true;
        }
        Assert.IsTrue(settings.IsStartupEnabled);
        AssertSettingsActionAlignment(
            view,
            "StartupRegistrationLabelPanel",
            "StartupRegistrationToggle");
        AssertSettingsActionAlignment(
            view,
            "RefreshTitleText",
            "RefreshIntervalPanel");
        Layout(view, 730, 560);
        Border generalCard = Assert.IsInstanceOfType<Border>(
            view.FindName("GeneralSettingsCardPanel"));
        foreach (string actionName in new[]
                 {
                     "StartupRegistrationToggle",
                     "RefreshIntervalPanel"
                 })
        {
            FrameworkElement action = Assert.IsInstanceOfType<FrameworkElement>(
                view.FindName(actionName));
            Rect bounds = action.TransformToAncestor(generalCard)
                .TransformBounds(new Rect(action.RenderSize));
            Assert.IsGreaterThanOrEqualTo(
                0d,
                bounds.Left,
                $"{actionName} 在 900×640 对应内容宽度下不得越出左边界。");
            Assert.IsLessThanOrEqualTo(
                generalCard.ActualWidth,
                bounds.Right,
                $"{actionName} 在 900×640 对应内容宽度下不得越出右边界。");
        }

        settings.OpenSettingsSectionCommand.Execute(SettingsSection.Privacy);
        Layout(view, 1000, Math.Max(680d, view.ActualHeight));
        IReadOnlyList<string> privacyText = EnumerateVisibleText(view).ToArray();
        Assert.IsTrue(privacyText.Contains("隐私与安全"));
        Assert.IsTrue(privacyText.Contains("隐私边界"));
        Assert.IsFalse(privacyText.Any(static text => text.Contains(
            "Development",
            StringComparison.Ordinal)));
        Assert.IsFalse(privacyText.Contains("不修改 Agent 配置"));
        Assert.IsFalse(privacyText.Contains("仅在界面打开时检查本地数据变化"));

        Button backButton = Assert.IsInstanceOfType<Button>(
            view.FindName("BackToSettingsHomeButton"));
        Assert.AreEqual(40d, backButton.Height);
        Assert.AreEqual(new Thickness(1d), backButton.BorderThickness);
        Assert.IsNotNull(view.FindName("BackToSettingsHomeIcon"));
        settings.IsDataStorageExpanded = false;
        settings.IsDangerousDataActionsExpanded = false;
        settings.BackToSettingsHomeCommand.Execute(null);
        Layout(view, 1000, Math.Max(680d, view.ActualHeight));
        Assert.IsTrue(settings.IsSettingsHome);
    }

    private static void AssertSettingsActionAlignment(
        SettingsView view,
        string labelName,
        string actionName)
    {
        FrameworkElement label = Assert.IsInstanceOfType<FrameworkElement>(
            view.FindName(labelName));
        FrameworkElement action = Assert.IsInstanceOfType<FrameworkElement>(
            view.FindName(actionName));
        Rect labelBounds = label.TransformToAncestor(view)
            .TransformBounds(new Rect(label.RenderSize));
        Rect actionBounds = action.TransformToAncestor(view)
            .TransformBounds(new Rect(action.RenderSize));
        double centerDelta = Math.Abs(
            (labelBounds.Top + (labelBounds.Height / 2d)) -
            (actionBounds.Top + (actionBounds.Height / 2d)));
        Assert.IsLessThanOrEqualTo(
            1d,
            centerDelta,
            $"{labelName} 与 {actionName} 应在同一水平中心线上。");
    }

    private static async Task AssertSettingsPriceBindingAndSelectionAsync(
        System.Windows.Threading.Dispatcher dispatcher,
        Window diagnosticWindow,
        string databasePath)
    {
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                new PriceSettingRow("unpriced-model", null, null, 3),
                new PriceSettingRow(
                    "kimi-k3-256k",
                    new ModelPriceRate(
                        "kimi-k3-256k",
                        3m,
                        0.3m,
                        null,
                        15m),
                    null,
                    2),
                new PriceSettingRow(
                    "qwen3.8-max",
                    new ModelPriceRate(
                        "qwen3.8-max",
                        0.5m,
                        0.25m,
                        null,
                        2m),
                    null,
                    1)
            ]
        };
        using var settings = new SettingsViewModel(
            queries,
            new UnavailablePriceCommandClient(),
            new RejectingPriceRestoreConfirmation(),
            dispatcher,
            databasePath,
            AgenTallyChannel.Development);
        await settings.RefreshAsync(CancellationToken.None);
        settings.OpenSettingsSectionCommand.Execute(SettingsSection.Pricing);
        var view = new SettingsView { DataContext = settings };
        diagnosticWindow.Content = null;
        diagnosticWindow.DataContext = null;
        diagnosticWindow.Content = view;
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Render);
        Layout(view, 808, 700);

        DataGrid grid = Assert.IsInstanceOfType<DataGrid>(
            view.FindName("PriceModelGrid"));
        AssertRoundedTableContentClip(grid, "PriceModelGrid");
        TextBox input = Assert.IsInstanceOfType<TextBox>(
            view.FindName("InputPriceTextBox"));
        Assert.AreEqual("unpriced-model", settings.SelectedPriceModel?.NormalizedModel);
        Assert.AreEqual(string.Empty, input.Text);

        PriceSettingPresentation kimi = settings.PriceModels.Single(row =>
            row.NormalizedModel == "kimi-k3-256k");
        grid.SelectedItem = kimi;
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.DataBind);
        Assert.AreEqual("kimi-k3-256k", settings.SelectedPriceModel?.NormalizedModel);
        Assert.AreEqual("3", input.Text);

        PriceSettingPresentation qwen = settings.PriceModels.Single(row =>
            row.NormalizedModel == "qwen3.8-max");
        grid.SelectedItem = qwen;
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.DataBind);
        Assert.AreEqual("qwen3.8-max", settings.SelectedPriceModel?.NormalizedModel);
        Assert.AreEqual("0.5", input.Text);

        SolidColorBrush navActive = Assert.IsInstanceOfType<SolidColorBrush>(
            view.FindResource("NavActiveBrush"));
        SolidColorBrush focusedSelection = Assert.IsInstanceOfType<SolidColorBrush>(
            grid.FindResource(SystemColors.HighlightBrushKey));
        SolidColorBrush inactiveSelection = Assert.IsInstanceOfType<SolidColorBrush>(
            grid.FindResource(SystemColors.InactiveSelectionHighlightBrushKey));
        Assert.AreEqual(navActive.Color, focusedSelection.Color);
        Assert.AreEqual(navActive.Color, inactiveSelection.Color);
        DataGridRow selectedRow = Assert.IsInstanceOfType<DataGridRow>(
            grid.ItemContainerGenerator.ContainerFromItem(qwen));
        selectedRow.ApplyTemplate();
        Border selectedOverlay = Assert.IsInstanceOfType<Border>(
            selectedRow.Template.FindName("SelectedOverlay", selectedRow));
        SolidColorBrush overlayBrush = Assert.IsInstanceOfType<SolidColorBrush>(
            selectedOverlay.Background);
        Assert.AreEqual(navActive.Color, overlayBrush.Color);
        Assert.AreEqual(1d, selectedOverlay.Opacity);

        grid.SelectedItem = settings.PriceModels.Single(row =>
            row.NormalizedModel == "unpriced-model");
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.DataBind);
        Assert.AreEqual(string.Empty, input.Text);
    }

    private static async Task AssertPromptUsageProgressRendersAsync(
        System.Windows.Threading.Dispatcher dispatcher)
    {
        DateTimeOffset nowUtc =
            new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var summary = new RootSessionSummaryRow(
            new RootSessionIdentity(
                "codex",
                "codex:windows:test",
                "root-prompt-render"),
            nowUtc.AddHours(-1),
            nowUtc,
            "project-prompt-render",
            @"D:\Projects\prompt-render",
            PathAvailability.Available,
            1,
            0,
            TestData.MetricSet(100));
        var queries = new FakeUsageQueryService
        {
            RootSessionsResult = new RootSessionPage([summary], null),
            RootSessionDetailResult = new RootSessionDetail(
                summary,
                [
                    new SessionContributionRow(
                        summary.RootSessionId,
                        null,
                        SessionKind.Primary,
                        0,
                        1,
                        TestData.MetricSet(100),
                        [])
                ]),
            TurnsResult = new TurnUsagePage(
                TurnCoverageStatus.Complete,
                Enumerable.Range(0, 14)
                    .Select(index => new TurnUsageRow(
                        $"turn-prompt-render-{index}",
                        nowUtc.AddMinutes(-index - 1),
                        nowUtc.AddMinutes(-index),
                        1,
                        TestData.MetricSet(50 + index))
                    {
                        PromptPreview = $"渲染 Prompt 用量条 {index}",
                        UserMessageCount = 1,
                        MaxPromptTokens = 100
                    })
                    .ToArray(),
                new UnattributedUsageSummary(0, TestData.MetricSet(0)),
                14),
            TurnCallsResult =
            [
                new TurnCallUsageRow(
                    nowUtc.AddMinutes(-1),
                    "gpt-5.6-super-long-rendering-model-name",
                    summary.RootSessionId,
                    SessionKind.Primary,
                    SessionRole.Main,
                    [],
                    TestData.MetricSet(50))
            ]
        };
        var viewModel = new SessionsViewModel(
            queries,
            dispatcher,
            new FixedTimeProvider(nowUtc),
            TimeZoneInfo.Utc);
        await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.Detail!.Turns[0].ToggleExpandedCommand.ExecuteAsync();

        var view = new SessionsView { DataContext = viewModel };
        var window = new Window
        {
            Content = view,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };
        try
        {
            window.Show();
            Layout(window, 1024, 720);
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Render);

            ScrollViewer detailScroll = Assert.IsInstanceOfType<ScrollViewer>(
                view.FindName("SessionsDetailScrollViewer"));
            TabControl tabs = Assert.IsInstanceOfType<TabControl>(
                view.FindName("SessionDetailTabs"));
            TabItem promptTab = Assert.IsInstanceOfType<TabItem>(
                view.FindName("PromptTimelineTab"));
            TabItem compositionTab = Assert.IsInstanceOfType<TabItem>(
                view.FindName("SessionCompositionTab"));

            tabs.SelectedItem = compositionTab;
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Render);
            detailScroll.ScrollToTop();
            window.UpdateLayout();
            Assert.AreEqual(
                0d,
                detailScroll.ScrollableHeight,
                0.5d,
                "短会话构成在该验收尺寸下不应产生详情滚动距离。");

            promptTab.ApplyTemplate();
            FrameworkElement promptTabSurface =
                Assert.IsInstanceOfType<FrameworkElement>(
                    promptTab.Template.FindName("TabBorder", promptTab));
            var mouseDown = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent,
                Source = promptTabSurface
            };
            var automaticOffsets = new List<double>();
            ScrollChangedEventHandler recordAutomaticOffset = (_, _) =>
            {
                if (detailScroll.VerticalOffset > 0.5d)
                {
                    automaticOffsets.Add(detailScroll.VerticalOffset);
                }
            };
            detailScroll.ScrollChanged += recordAutomaticOffset;
            promptTabSurface.RaiseEvent(mouseDown);
            Assert.IsTrue(
                promptTab.IsSelected,
                "从页签模板表面点击后应切换到 Prompt 时间线。");
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Render);
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            detailScroll.ScrollChanged -= recordAutomaticOffset;
            Assert.IsGreaterThan(
                0d,
                detailScroll.ScrollableHeight,
                "长 Prompt 时间线应产生详情滚动距离。");
            Assert.IsEmpty(
                automaticOffsets,
                $"页签切换期间详情区不应先滚动再恢复，实际偏移：{string.Join(", ", automaticOffsets)}。");
            Assert.AreEqual(
                0d,
                detailScroll.VerticalOffset,
                0.5d,
                "从无滚动条的会话构成切到 Prompt 后应保持详情顶部位置。");

            ProgressBar[] promptUsages = FindVisualChildren<ProgressBar>(view)
                .Where(progress =>
                    BindingOperations.GetBinding(
                        progress,
                        ProgressBar.ValueProperty)?.Path.Path ==
                    nameof(TurnUsagePresentation.RelativeUsage))
                .ToArray();
            Assert.HasCount(14, promptUsages);
            foreach (ProgressBar promptUsage in promptUsages)
            {
                Binding binding = Assert.IsInstanceOfType<Binding>(
                    BindingOperations.GetBinding(
                        promptUsage,
                        ProgressBar.ValueProperty));
                Assert.AreEqual(BindingMode.OneWay, binding.Mode);
            }

            Assert.AreEqual(0.5d, promptUsages[0].Value, 0.001d);
            TextBlock[] activitySummaries = FindVisualChildren<TextBlock>(view)
                .Where(text =>
                    text.DataContext is TurnUsagePresentation &&
                    BindingOperations.GetBinding(
                        text,
                        TextBlock.TextProperty)?.Path.Path ==
                    nameof(TurnUsagePresentation.ActivityText))
                .ToArray();
            Assert.HasCount(14, activitySummaries);
            foreach (TextBlock activitySummary in activitySummaries)
            {
                Assert.AreEqual(
                    TextTrimming.CharacterEllipsis,
                    activitySummary.TextTrimming,
                    "Prompt 调用摘要超长时应显示省略号。");
                Grid activityGrid =
                    Assert.IsInstanceOfType<Grid>(activitySummary.Parent);
                Assert.AreEqual(1, Grid.GetColumn(activitySummary));
                Assert.IsTrue(
                    activityGrid.ColumnDefinitions[1].Width.IsStar,
                    "Prompt 调用摘要必须位于受约束的弹性列中。");
            }

            TextBlock callModel = FindVisualChildren<TextBlock>(view)
                .Single(text =>
                    text.DataContext is TurnCallUsagePresentation &&
                    BindingOperations.GetBinding(
                        text,
                        TextBlock.TextProperty)?.Path.Path ==
                    nameof(TurnCallUsagePresentation.ModelText));
            Assert.AreEqual(
                TextTrimming.CharacterEllipsis,
                callModel.TextTrimming,
                "模型名超长时应显示省略号。");
            Assert.AreEqual(
                callModel.Text,
                callModel.ToolTip,
                "模型名的完整内容应保留在提示中。");
            Grid callIdentityGrid =
                Assert.IsInstanceOfType<Grid>(callModel.Parent);
            Assert.AreEqual(2, Grid.GetColumn(callModel));
            Assert.IsTrue(
                callIdentityGrid.ColumnDefinitions[2].Width.IsStar,
                "模型名必须位于受约束的弹性列中。");
            Grid callHeaderGrid =
                Assert.IsInstanceOfType<Grid>(callIdentityGrid.Parent);
            Assert.AreEqual(88d, callHeaderGrid.ColumnDefinitions[1].Width.Value);
            Assert.AreEqual(68d, callHeaderGrid.ColumnDefinitions[2].Width.Value);

            Button? lastPromptButton = FindVisualChildren<Button>(view)
                .Where(button =>
                    BindingOperations.GetBinding(
                        button,
                        Button.CommandProperty)?.Path.Path ==
                    nameof(TurnUsagePresentation.ToggleExpandedCommand))
                .LastOrDefault();
            Assert.IsNotNull(lastPromptButton);
            lastPromptButton.BringIntoView();
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Render);
            Assert.IsGreaterThan(
                0d,
                detailScroll.VerticalOffset,
                "只应阻止页签标题的自动定位，Prompt 内容按钮仍须能够滚入可视区域。");
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertSessionsLayout(SessionsView view)
    {
        AssertNoLocalLoadingProgress(view, "会话页");
        Border sessionsListCard = Assert.IsInstanceOfType<Border>(
            view.FindName("SessionsListCard"));
        ListBox sessionsList = Assert.IsInstanceOfType<ListBox>(
            view.FindName("SessionsListBox"));
        ScrollViewer detailScroll = Assert.IsInstanceOfType<ScrollViewer>(
            view.FindName("SessionsDetailScrollViewer"));
        Point listOrigin = sessionsListCard.TranslatePoint(new Point(), view);
        Point detailOrigin = detailScroll.TranslatePoint(new Point(), view);
        Assert.AreEqual(
            listOrigin.Y,
            detailOrigin.Y,
            0.5d,
            "会话详情滚动视口的上边界应与左侧会话列表卡片平齐。");
        Assert.AreEqual(
            ScrollBarVisibility.Disabled,
            detailScroll.HorizontalScrollBarVisibility);
        AssertPageScrollBarsUseUnifiedStyle(
            sessionsList,
            detailScroll,
            "会话页");
        Border summaryCard = Assert.IsInstanceOfType<Border>(
            view.FindName("SessionSummaryCard"));
        Assert.AreEqual(
            new Thickness(20d, 16d, 20d, 16d),
            summaryCard.Padding);
        Grid summaryMetricsHost = Assert.IsInstanceOfType<Grid>(
            view.FindName("SessionSummaryMetricsHost"));
        Grid summaryMetrics = Assert.IsInstanceOfType<Grid>(
            view.FindName("SessionSummaryMetrics"));
        Assert.HasCount(5, summaryMetrics.ColumnDefinitions);
        Assert.AreEqual(
            summaryMetricsHost.ActualWidth,
            summaryMetrics.ActualWidth,
            0.5d,
            "会话指标组应使用摘要卡的完整可用宽度。");
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[0].Width.IsAuto);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[1].Width.IsStar);
        Assert.AreEqual(
            32d,
            summaryMetrics.ColumnDefinitions[1].MinWidth,
            0.01d);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[2].Width.IsAuto);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[3].Width.IsStar);
        Assert.AreEqual(
            32d,
            summaryMetrics.ColumnDefinitions[3].MinWidth,
            0.01d);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[4].Width.IsAuto);
        StackPanel totalMetric = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("SessionTotalMetric"));
        StackPanel priceMetric = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("SessionPriceMetric"));
        StackPanel promptMetric = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("SessionPromptTurnMetric"));
        Point totalOrigin = totalMetric.TranslatePoint(
            new Point(),
            summaryMetrics);
        Point priceOrigin = priceMetric.TranslatePoint(
            new Point(),
            summaryMetrics);
        Point promptOrigin = promptMetric.TranslatePoint(
            new Point(),
            summaryMetrics);
        Assert.AreEqual(
            totalOrigin.Y,
            priceOrigin.Y,
            0.5d,
            "会话 Token 与价格指标应从同一水平线开始。");
        Assert.AreEqual(totalOrigin.Y, promptOrigin.Y, 0.5d);
        double firstMetricGap =
            priceOrigin.X - totalOrigin.X - totalMetric.ActualWidth;
        double secondMetricGap =
            promptOrigin.X - priceOrigin.X - priceMetric.ActualWidth;
        Assert.IsGreaterThanOrEqualTo(
            31.5d,
            firstMetricGap,
            "会话 Token 与价格的实际内容边缘之间至少保留 32 DIP。");
        Assert.AreEqual(
            firstMetricGap,
            secondMetricGap,
            1d,
            "会话两个弹性指标间隔应平均分享剩余宽度。");
        Assert.AreEqual(
            summaryMetrics.ActualWidth,
            promptOrigin.X + promptMetric.ActualWidth,
            0.5d,
            "会话最后一个指标应贴齐指标组右边界。");
        foreach (StackPanel metric in new[]
        {
            totalMetric,
            priceMetric,
            promptMetric
        })
        {
            Assert.IsTrue(
                metric.Children.OfType<TextBlock>().All(block =>
                    block.TextAlignment == TextAlignment.Left),
                "会话指标标题和数值应统一左对齐。");
        }
        Button projectLink = Assert.IsInstanceOfType<Button>(
            view.FindName("SessionProjectLink"));
        Assert.AreEqual(
            28d,
            projectLink.ActualHeight,
            0.5d,
            "会话项目跳转按钮应使用紧凑高度。");
        Assert.IsNull(
            view.FindName("SessionPriceBadge"),
            "会话摘要不应保留计价状态徽标。");
        CollectionAssert.AreEqual(
            new[] { "总 Token", "等效 API 价格（估算）", "Prompt 轮次" },
            new[] { totalMetric, priceMetric, promptMetric }
                .Select(metric =>
                    metric.Children.OfType<TextBlock>().First().Text)
                .ToArray());
        string[] removedSessionBindings =
        [
            "Detail.PriceNote",
            "Detail.MetricsNote"
        ];
        Assert.IsFalse(
            FindVisualChildren<TextBlock>(summaryCard).Any(block =>
                BindingOperations.GetBinding(
                    block,
                    TextBlock.TextProperty)?.Path.Path is string path &&
                removedSessionBindings.Contains(path)),
            "会话摘要不应继续绑定计价或字段覆盖说明。");
        string[] sessionSummaryText = EnumerateVisibleText(summaryCard)
            .ToArray();
        Assert.IsFalse(
            sessionSummaryText.Any(text =>
                text.Contains("金额为已计价部分合计", StringComparison.Ordinal) ||
                text.Contains("合计仅覆盖已统计到的记录", StringComparison.Ordinal)),
            "会话摘要不应继续显示已删除的解释文字。");
        TabControl tabs = Assert.IsInstanceOfType<TabControl>(
            view.FindName("SessionDetailTabs"));
        Assert.IsNull(
            tabs.FocusVisualStyle,
            "详情分段控件不应显示系统虚线焦点框。");
        Assert.AreEqual(0, tabs.SelectedIndex);
        CollectionAssert.AreEqual(
            new[] { "Prompt 时间线", "会话构成", "模型" },
            tabs.Items.Cast<TabItem>()
                .Select(static item => item.Header?.ToString())
                .ToArray());
        Assert.IsNotNull(view.FindName("PromptTimelineTab"));
        Assert.IsNotNull(view.FindName("SessionCompositionTab"));
        Assert.IsNotNull(view.FindName("SessionModelsTab"));
        Button loadMorePrompts = Assert.IsInstanceOfType<Button>(
            view.FindName("LoadMorePromptsButton"));
        Assert.AreEqual("加载更多 Prompt", loadMorePrompts.Content);
        Assert.AreSame(
            Assert.IsInstanceOfType<SessionsViewModel>(view.DataContext)
                .LoadMoreTurnsCommand,
            loadMorePrompts.Command,
            "移除局部加载反馈不能删除 Prompt 分页能力。");
        foreach (TabItem item in tabs.Items.Cast<TabItem>())
        {
            item.ApplyTemplate();
            Assert.IsNull(
                item.Template.FindName("FocusRing", item),
                $"{item.Header} 页签不应显示橙色焦点环。");
            Assert.IsNotNull(
                item.Template.FindName("TabBorder", item),
                $"{item.Header} 页签应继续保留正常选中边框。");
        }
    }

    private static void AssertPageScrollBarsUseUnifiedStyle(
        ListBox list,
        ScrollViewer detailScroll,
        string pageName)
    {
        Assert.IsFalse(
            detailScroll.Resources.Contains(typeof(ScrollBar)),
            $"{pageName}右侧详情不应再维护页面级滚动条样式。");
        ScrollViewer listScroll = FindVisualChild<ScrollViewer>(list) ??
            throw new AssertFailedException(
                $"{pageName}左侧列表应生成内部滚动视口。");
        listScroll.ApplyTemplate();
        detailScroll.ApplyTemplate();
        ScrollBar listBar = Assert.IsInstanceOfType<ScrollBar>(
            listScroll.Template.FindName("PART_VerticalScrollBar", listScroll));
        ScrollBar detailBar = Assert.IsInstanceOfType<ScrollBar>(
            detailScroll.Template.FindName("PART_VerticalScrollBar", detailScroll));
        Style ledgerStyle = Assert.IsInstanceOfType<Style>(
            detailScroll.FindResource("LedgerScrollBarStyle"));
        Assert.AreSame(
            ledgerStyle,
            Assert.IsInstanceOfType<Style>(listBar.Style).BasedOn,
            $"{pageName}左侧列表必须基于全局滚动条样式。");
        Assert.AreSame(
            ledgerStyle,
            Assert.IsInstanceOfType<Style>(detailBar.Style).BasedOn,
            $"{pageName}右侧详情必须基于全局滚动条样式。");
        AssertUnifiedVerticalScrollBar(listBar, $"{pageName}左侧列表");
        AssertUnifiedVerticalScrollBar(detailBar, $"{pageName}右侧详情");
        Assert.AreSame(
            listBar.Template,
            detailBar.Template,
            $"{pageName}左右滚动条必须使用同一模板。");

        if (detailBar.Visibility == Visibility.Visible)
        {
            Point liveOrigin = detailBar.TranslatePoint(new Point(), detailScroll);
            Assert.IsGreaterThanOrEqualTo(
                0d,
                liveOrigin.X,
                $"{pageName}滚动条不得伸出详情滚动视口左边界。");
            Assert.IsLessThanOrEqualTo(
                detailScroll.ActualWidth + 0.5d,
                liveOrigin.X + detailBar.ActualWidth,
                $"{pageName}滚动条不得被详情滚动视口右边界裁切。");
        }
    }

    private static void AssertUnifiedVerticalScrollBar(
        ScrollBar scrollBar,
        string scrollBarName)
    {
        Assert.AreEqual(
            12d,
            scrollBar.Width,
            0.01d,
            $"{scrollBarName}应保留 12 DIP 透明交互区。");
        scrollBar.ApplyTemplate();
        Track track = Assert.IsInstanceOfType<Track>(
            scrollBar.Template.FindName("PART_Track", scrollBar));
        Assert.AreEqual(
            1,
            Grid.GetColumn(track),
            $"{scrollBarName}的可见轨道应位于 4 DIP 内容间隔之后。");
        Assert.AreSame(
            scrollBar.FindResource("ScrollBarThumbTemplate"),
            track.Thumb.Template,
            $"{scrollBarName}应复用全局 8 DIP 滑块模板。");
    }

    private static void AssertProjectsLayout(ProjectsView view)
    {
        AssertNoLocalLoadingProgress(view, "项目页");
        Assert.IsFalse(
            EnumerateVisibleText(view).Contains(
                "按工作目录查看项目用量、内部构成与关联会话"),
            "项目标题下方的旧副标题应被删除。");
        Grid projectsContent = Assert.IsInstanceOfType<Grid>(
            view.FindName("ProjectsContent"));
        ColumnDefinition masterColumn = projectsContent.ColumnDefinitions[0];
        Assert.IsTrue(masterColumn.Width.IsStar);
        Assert.AreEqual(0.36d, masterColumn.Width.Value, 0.001d);
        Assert.AreEqual(280d, masterColumn.MinWidth, 0.01d);
        Assert.AreEqual(360d, masterColumn.MaxWidth, 0.01d);
        ScrollViewer detailScroll = Assert.IsInstanceOfType<ScrollViewer>(
            view.FindName("ProjectsDetailScrollViewer"));
        ListBox projectsList = Assert.IsInstanceOfType<ListBox>(
            view.FindName("ProjectsListBox"));
        Assert.AreEqual(
            ScrollBarVisibility.Disabled,
            detailScroll.HorizontalScrollBarVisibility);
        AssertPageScrollBarsUseUnifiedStyle(
            projectsList,
            detailScroll,
            "项目页");
        Border summaryCard = Assert.IsInstanceOfType<Border>(
            view.FindName("ProjectSummaryCard"));
        Grid summaryMetricsHost = Assert.IsInstanceOfType<Grid>(
            view.FindName("ProjectSummaryMetricsHost"));
        Grid summaryMetrics = Assert.IsInstanceOfType<Grid>(
            view.FindName("ProjectSummaryMetrics"));
        Assert.HasCount(5, summaryMetrics.ColumnDefinitions);
        Assert.AreEqual(
            summaryMetricsHost.ActualWidth,
            summaryMetrics.ActualWidth,
            0.5d,
            "项目指标组应使用摘要卡的完整可用宽度。");
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[0].Width.IsAuto);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[1].Width.IsStar);
        Assert.AreEqual(
            32d,
            summaryMetrics.ColumnDefinitions[1].MinWidth,
            0.01d);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[2].Width.IsAuto);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[3].Width.IsStar);
        Assert.AreEqual(
            32d,
            summaryMetrics.ColumnDefinitions[3].MinWidth,
            0.01d);
        Assert.IsTrue(summaryMetrics.ColumnDefinitions[4].Width.IsAuto);
        StackPanel totalMetric = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("ProjectTotalMetric"));
        StackPanel priceMetric = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("ProjectPriceMetric"));
        StackPanel sessionMetric = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("ProjectSessionMetric"));
        Point totalOrigin = totalMetric.TranslatePoint(
            new Point(),
            summaryMetrics);
        Point priceOrigin = priceMetric.TranslatePoint(
            new Point(),
            summaryMetrics);
        Point sessionOrigin = sessionMetric.TranslatePoint(
            new Point(),
            summaryMetrics);
        Assert.AreEqual(totalOrigin.Y, priceOrigin.Y, 0.5d);
        Assert.AreEqual(totalOrigin.Y, sessionOrigin.Y, 0.5d);
        double firstMetricGap =
            priceOrigin.X - totalOrigin.X - totalMetric.ActualWidth;
        double secondMetricGap =
            sessionOrigin.X - priceOrigin.X - priceMetric.ActualWidth;
        Assert.IsGreaterThanOrEqualTo(
            31.5d,
            firstMetricGap,
            "项目 Token 与价格的实际内容边缘之间至少保留 32 DIP。");
        Assert.AreEqual(
            firstMetricGap,
            secondMetricGap,
            1d,
            "项目两个弹性指标间隔应平均分享剩余宽度。");
        Assert.AreEqual(
            summaryMetrics.ActualWidth,
            sessionOrigin.X + sessionMetric.ActualWidth,
            0.5d,
            "项目最后一个指标应贴齐指标组右边界。");
        foreach (StackPanel metric in new[]
        {
            totalMetric,
            priceMetric,
            sessionMetric
        })
        {
            Assert.AreEqual(
                HorizontalAlignment.Stretch,
                metric.HorizontalAlignment,
                "项目指标内容应在自己的 Auto 列内保持左对齐。");
            Assert.IsTrue(
                metric.Children.OfType<TextBlock>().All(block =>
                    block.TextAlignment == TextAlignment.Left),
                "项目指标标题和数值应统一左对齐。");
        }
        Assert.IsFalse(
            FindVisualChildren<TextBlock>(summaryCard).Any(block =>
                BindingOperations.GetBinding(
                    block,
                    TextBlock.TextProperty)?.Path.Path ==
                "Detail.DataNote"),
            "项目摘要不应继续绑定数据状态说明。");
        TabControl tabs = Assert.IsInstanceOfType<TabControl>(
            view.FindName("ProjectDetailTabs"));
        Assert.IsNull(tabs.FocusVisualStyle);
        Assert.AreEqual(0, tabs.SelectedIndex);
        CollectionAssert.AreEqual(
            new[] { "项目概览", "平台", "模型", "会话" },
            tabs.Items.Cast<TabItem>()
                .Select(static item => item.Header?.ToString())
                .ToArray());
        TextBox searchBox = Assert.IsInstanceOfType<TextBox>(
            view.FindName("ProjectSearchBox"));
        Grid searchHost = Assert.IsInstanceOfType<Grid>(
            view.FindName("ProjectSearchHost"));
        Assert.AreEqual(36d, searchBox.ActualHeight, 0.5d);
        Assert.IsGreaterThanOrEqualTo(
            searchBox.ActualHeight,
            searchHost.ActualHeight,
            "搜索框宿主不能比共享 TextBox 模板更矮。");
        searchBox.ApplyTemplate();
        Border searchBorder = Assert.IsInstanceOfType<Border>(
            searchBox.Template.FindName("InputBorder", searchBox));
        Assert.AreEqual(searchBox.ActualHeight, searchBorder.ActualHeight, 0.5d);
        Assert.AreEqual(searchBox.ActualWidth, searchBorder.ActualWidth, 0.5d);

        ComboBox sortSelector = Assert.IsInstanceOfType<ComboBox>(
            view.FindName("ProjectSortSelector"));
        Assert.AreEqual(0d, sortSelector.MinWidth);
        FrameworkElement sortHost = Assert.IsInstanceOfType<FrameworkElement>(
            VisualTreeHelper.GetParent(sortSelector));
        Point sortOrigin = sortSelector.TranslatePoint(
            new Point(0d, 0d),
            sortHost);
        Assert.IsLessThanOrEqualTo(
            sortHost.ActualWidth + 0.5d,
            sortOrigin.X + sortSelector.ActualWidth,
            "项目排序框右边缘不能越出列表卡片标题区域。");
        sortSelector.ApplyTemplate();
        FrameworkElement selectionHost = Assert.IsInstanceOfType<FrameworkElement>(
            sortSelector.Template.FindName("SelectionHost", sortSelector));
        FrameworkElement glyph = Assert.IsInstanceOfType<FrameworkElement>(
            sortSelector.Template.FindName("DropDownGlyph", sortSelector));
        Point glyphOrigin = glyph.TranslatePoint(
            new Point(0d, 0d),
            sortSelector);
        Assert.IsLessThanOrEqualTo(
            sortSelector.ActualWidth + 0.5d,
            glyphOrigin.X + glyph.ActualWidth,
            "项目排序框的箭头必须完整位于控件边界内。");
        Assert.IsLessThanOrEqualTo(
            glyphOrigin.X + 0.5d,
            selectionHost.ActualWidth,
            "项目排序文字区域不能覆盖右侧箭头。");

        StackPanel platformLayout = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("ProjectPlatformLayout"));
        StackPanel modelLayout = Assert.IsInstanceOfType<StackPanel>(
            view.FindName("ProjectModelLayout"));
        Assert.AreEqual(Orientation.Vertical, platformLayout.Orientation);
        Assert.AreEqual(Orientation.Vertical, modelLayout.Orientation);
        Assert.IsNotNull(view.FindName("ProjectPlatformShareCard"));
        Assert.IsNotNull(view.FindName("ProjectPlatformDetailCard"));
        Assert.IsNotNull(view.FindName("ProjectModelShareCard"));
        Assert.IsNotNull(view.FindName("ProjectModelDetailCard"));
        Assert.IsNotNull(view.FindName("ProjectsListBox"));
        UsageTrendChart trendChart = Assert.IsInstanceOfType<UsageTrendChart>(
            view.FindName("ProjectUsageTrendChart"));
        Assert.IsTrue(trendChart.AllowHoverCardOutsidePlot);
    }

    private static void AssertStackedProjectBreakdown(
        ProjectsView view,
        string layoutName,
        string shareCardName,
        string detailCardName,
        string listName,
        int expectedRows)
    {
        StackPanel layout = Assert.IsInstanceOfType<StackPanel>(
            view.FindName(layoutName));
        Border shareCard = Assert.IsInstanceOfType<Border>(
            view.FindName(shareCardName));
        ContentControl detailCard = Assert.IsInstanceOfType<ContentControl>(
            view.FindName(detailCardName));
        ListBox list = Assert.IsInstanceOfType<ListBox>(
            view.FindName(listName));

        Assert.AreEqual(Orientation.Vertical, layout.Orientation);
        Assert.IsGreaterThan(0d, shareCard.ActualWidth);
        Assert.IsGreaterThan(0d, shareCard.ActualHeight);
        Assert.IsGreaterThan(0d, detailCard.ActualWidth);
        Assert.IsGreaterThan(0d, detailCard.ActualHeight);
        Point shareOrigin = shareCard.TranslatePoint(new Point(0d, 0d), layout);
        Point detailOrigin = detailCard.TranslatePoint(new Point(0d, 0d), layout);
        Assert.IsGreaterThanOrEqualTo(
            shareOrigin.Y + shareCard.ActualHeight + 15.5d,
            detailOrigin.Y,
            $"{layoutName} 必须固定为上方占比、下方详情。");
        Assert.AreEqual(
            shareCard.ActualWidth,
            detailCard.ActualWidth,
            0.5d,
            $"{layoutName} 的上下两张卡片应使用同一可用宽度。");
        Assert.IsLessThanOrEqualTo(
            320.5d,
            list.ActualHeight,
            $"{listName} 数据较多时应在有界高度内纵向浏览。");
        Assert.IsLessThan(
            expectedRows == 1 ? 170d : 410d,
            shareCard.ActualHeight,
            $"{shareCardName} 的高度应跟随实际条目，不能与详情卡强制等高。");

        Assert.AreEqual(expectedRows, list.Items.Count);
        ListBoxItem firstItem = Assert.IsInstanceOfType<ListBoxItem>(
            list.ItemContainerGenerator.ContainerFromIndex(0));
        Point firstOrigin = firstItem.TranslatePoint(new Point(0d, 0d), list);
        Assert.IsLessThanOrEqualTo(
            2.5d,
            firstOrigin.Y,
            $"{listName} 的第一个条目应从列表顶部自然排列。");

        ProgressBar[] progressBars = FindVisualChildren<ProgressBar>(list)
            .Where(progress =>
                BindingOperations.GetBinding(
                    progress,
                    ProgressBar.ValueProperty)?.Path.Path ==
                nameof(UsageSharePresentation.ShareValue))
            .ToArray();
        ListBoxItem[] realizedItems = Enumerable.Range(0, expectedRows)
            .Select(index =>
                list.ItemContainerGenerator.ContainerFromIndex(index) as
                    ListBoxItem)
            .Where(static item => item is not null)
            .Cast<ListBoxItem>()
            .ToArray();
        Assert.IsNotEmpty(realizedItems);
        Assert.HasCount(realizedItems.Length, progressBars);
        foreach (ProgressBar progressBar in progressBars)
        {
            Assert.AreEqual(
                progressBars[0].ActualWidth,
                progressBar.ActualWidth,
                0.5d,
                $"{listName} 的占比进度条必须使用一致宽度。");
        }

        for (int index = 0; index < realizedItems.Length; index++)
        {
            ListBoxItem item = realizedItems[index];
            TextBlock name = FindVisualChildren<TextBlock>(item).Single(block =>
                BindingOperations.GetBinding(
                    block,
                    TextBlock.TextProperty)?.Path.Path == "NameText");
            TextBlock tokens = FindVisualChildren<TextBlock>(item).Single(block =>
                BindingOperations.GetBinding(
                    block,
                    TextBlock.TextProperty)?.Path.Path == "TokensText");
            TextBlock share = FindVisualChildren<TextBlock>(item).Single(block =>
                BindingOperations.GetBinding(
                    block,
                    TextBlock.TextProperty)?.Path.Path == "ShareText");
            ProgressBar progress = FindVisualChildren<ProgressBar>(item).Single();

            Assert.AreEqual(TextTrimming.CharacterEllipsis, name.TextTrimming);
            Assert.AreEqual(name.Text, name.ToolTip);
            Assert.AreEqual(tokens.Text, tokens.ToolTip);
            Rect nameBounds = name.TransformToAncestor(item)
                .TransformBounds(new Rect(name.RenderSize));
            Rect tokenBounds = tokens.TransformToAncestor(item)
                .TransformBounds(new Rect(tokens.RenderSize));
            Rect progressBounds = progress.TransformToAncestor(item)
                .TransformBounds(new Rect(progress.RenderSize));
            Rect shareBounds = share.TransformToAncestor(item)
                .TransformBounds(new Rect(share.RenderSize));
            Assert.IsLessThanOrEqualTo(
                tokenBounds.Left + 0.5d,
                nameBounds.Right,
                $"{listName} 第 {index + 1} 行名称不能覆盖 Token 数值。");
            Assert.IsLessThanOrEqualTo(
                shareBounds.Left + 0.5d,
                progressBounds.Right,
                $"{listName} 第 {index + 1} 行进度条不能覆盖百分比。");
            Assert.IsLessThanOrEqualTo(
                item.ActualWidth + 0.5d,
                tokenBounds.Right,
                $"{listName} 第 {index + 1} 行 Token 数值不能越出卡片。");
            Assert.IsLessThanOrEqualTo(
                item.ActualWidth + 0.5d,
                shareBounds.Right,
                $"{listName} 第 {index + 1} 行百分比不能越出卡片。");
        }

        foreach (string path in new[]
        {
            "TotalTokensText",
            "RequestCountText",
            "CacheHitRateText",
            "StartedAtText",
            "LastActivityText",
            "PriceText"
        })
        {
            TextBlock value = FindVisualChildren<TextBlock>(detailCard).Single(block =>
                BindingOperations.GetBinding(
                    block,
                    TextBlock.TextProperty)?.Path.Path == path);
            Rect bounds = value.TransformToAncestor(detailCard)
                .TransformBounds(new Rect(value.RenderSize));
            Assert.IsGreaterThanOrEqualTo(
                -0.5d,
                bounds.Left,
                $"{detailCardName} 的 {path} 不得越出左边界。");
            Assert.IsLessThanOrEqualTo(
                detailCard.ActualWidth + 0.5d,
                bounds.Right,
                $"{detailCardName} 的 {path} 不得越出右边界。");
            if (path is "StartedAtText" or "LastActivityText")
            {
                Assert.AreEqual(
                    TextTrimming.None,
                    value.TextTrimming,
                    $"{detailCardName} 的日期不应截断。");
            }
        }
    }

    private static void AssertProjectShareWheelRouting(
        ProjectsView view,
        string listName,
        bool expectInnerScroll)
    {
        ScrollViewer outerScroller = Assert.IsInstanceOfType<ScrollViewer>(
            view.FindName("ProjectsDetailScrollViewer"));
        ListBox list = Assert.IsInstanceOfType<ListBox>(view.FindName(listName));
        ScrollViewer innerScroller = FindVisualChild<ScrollViewer>(list) ??
            throw new AssertFailedException($"{listName} 的内部滚动区未生成。");
        ListBoxItem wheelTarget = Assert.IsInstanceOfType<ListBoxItem>(
            list.ItemContainerGenerator.ContainerFromIndex(0));

        outerScroller.ScrollToTop();
        innerScroller.ScrollToTop();
        view.UpdateLayout();
        Assert.AreEqual(
            expectInnerScroll,
            innerScroller.ScrollableHeight > 0.5d,
            expectInnerScroll
                ? $"{listName} 超过高度上限时应启用内部滚动。"
                : $"{listName} 内容未超限时不应产生内部滚动距离。");

        RaiseMouseWheel(wheelTarget, -120);
        view.UpdateLayout();
        if (!expectInnerScroll)
        {
            Assert.IsGreaterThan(
                0d,
                outerScroller.VerticalOffset,
                $"{listName} 无内部滚动距离时应把向下滚轮交给外层详情。");
            Assert.AreEqual(0d, innerScroller.VerticalOffset, 0.5d);
            outerScroller.ScrollToTop();
            return;
        }

        Assert.IsGreaterThan(
            0d,
            innerScroller.VerticalOffset,
            $"{listName} 仍能向下滚动时应优先滚动内部列表。");
        Assert.AreEqual(
            0d,
            outerScroller.VerticalOffset,
            0.5d,
            $"{listName} 内部仍能移动时不应提前滚动外层详情。");

        innerScroller.ScrollToEnd();
        outerScroller.ScrollToTop();
        view.UpdateLayout();
        RaiseMouseWheel(wheelTarget, -120);
        view.UpdateLayout();
        Assert.IsGreaterThan(
            0d,
            outerScroller.VerticalOffset,
            $"{listName} 到达底部后应把继续向下的滚轮交给外层详情。");

        innerScroller.ScrollToTop();
        outerScroller.ScrollToEnd();
        view.UpdateLayout();
        double outerOffsetBeforeUp = outerScroller.VerticalOffset;
        RaiseMouseWheel(wheelTarget, 120);
        view.UpdateLayout();
        Assert.IsTrue(
            outerScroller.VerticalOffset < outerOffsetBeforeUp,
            $"{listName} 到达顶部后应把继续向上的滚轮交给外层详情。");

        innerScroller.ScrollToTop();
        outerScroller.ScrollToTop();
        view.UpdateLayout();
    }

    private static void RaiseMouseWheel(
        FrameworkElement target,
        int delta)
    {
        var previewEvent = new System.Windows.Input.MouseWheelEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice,
            Environment.TickCount,
            delta)
        {
            RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent,
            Source = target
        };
        target.RaiseEvent(previewEvent);
        if (previewEvent.Handled)
        {
            return;
        }

        var bubbleEvent = new System.Windows.Input.MouseWheelEventArgs(
            previewEvent.MouseDevice,
            previewEvent.Timestamp,
            delta)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent,
            Source = target
        };
        target.RaiseEvent(bubbleEvent);
    }

    private static void AssertNoLocalLoadingProgress(
        DependencyObject root,
        string scope)
    {
        Style loadingStyle = Assert.IsInstanceOfType<Style>(
            Application.Current.FindResource("LoadingProgressBarStyle"));
        Assert.IsFalse(
            FindVisualChildren<ProgressBar>(root).Any(progress =>
                ReferenceEquals(progress.Style, loadingStyle)),
            $"{scope}不应保留局部加载进度条；加载反馈只属于左下角全局状态区。");
    }

    private static System.Windows.Input.MouseButtonEventArgs RaiseMouseButton(
        UIElement target,
        RoutedEvent routedEvent,
        System.Windows.Input.MouseButton mouseButton =
            System.Windows.Input.MouseButton.Left)
    {
        var mouseEvent = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice,
            Environment.TickCount,
            mouseButton)
        {
            RoutedEvent = routedEvent,
            Source = target
        };
        target.RaiseEvent(mouseEvent);
        return mouseEvent;
    }

    private static void AssertTemplate<TView>(App app, Type viewModelType)
        where TView : FrameworkElement
    {
        var key = new DataTemplateKey(viewModelType);
        DataTemplate template = Assert.IsInstanceOfType<DataTemplate>(
            app.Resources[key]);
        Assert.IsInstanceOfType<TView>(template.LoadContent());
    }

    private static void AssertVirtualizedGrid(
        FrameworkElement view,
        string gridName)
    {
        DataGrid grid = Assert.IsInstanceOfType<DataGrid>(view.FindName(gridName));
        Assert.IsTrue(grid.IsReadOnly);
        Assert.IsTrue(grid.EnableRowVirtualization);
        Assert.IsTrue(grid.EnableColumnVirtualization);
        Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(grid));
        Assert.IsTrue(ScrollViewer.GetCanContentScroll(grid));
        Assert.IsTrue(double.IsFinite(grid.MaxHeight));
        Assert.IsGreaterThan(0, grid.MaxHeight);
        Assert.AreEqual(
            VirtualizationMode.Recycling,
            VirtualizingPanel.GetVirtualizationMode(grid));
        Style dataGridStyle = Assert.IsInstanceOfType<Style>(grid.Style);
        Style localScrollBarStyle = Assert.IsInstanceOfType<Style>(
            dataGridStyle.Resources[typeof(ScrollBar)]);
        var tableScrollBar = new ScrollBar
        {
            Style = localScrollBarStyle,
        };
        var generalScrollBar = new ScrollBar
        {
            Style = Assert.IsInstanceOfType<Style>(
                Application.Current.FindResource("LedgerScrollBarStyle")),
        };
        Layout(tableScrollBar, 12, 120);
        Layout(generalScrollBar, 12, 120);
        Assert.AreEqual(
            generalScrollBar.Background,
            tableScrollBar.Background,
            $"{gridName} 不应覆盖通用滚动条背景。");
        Track tableTrack = Assert.IsInstanceOfType<Track>(
            tableScrollBar.Template.FindName(
                "PART_Track",
                tableScrollBar));
        Track generalTrack = Assert.IsInstanceOfType<Track>(
            generalScrollBar.Template.FindName(
                "PART_Track",
                generalScrollBar));
        Assert.AreSame(
            generalTrack.Thumb.Template,
            tableTrack.Thumb.Template,
            $"{gridName} 必须复用通用滑块视觉模板。");
        Assert.AreSame(
            generalScrollBar.Template,
            tableScrollBar.Template,
            $"{gridName} 不应再维护重复的滚动条模板。");
        Assert.AreEqual(
            0d,
            tableTrack.Thumb.MinHeight,
            $"{gridName} 不应在 Track 计算后强制撑高滑块。");
        Assert.AreEqual(
            0d,
            generalTrack.Thumb.MinHeight,
            "通用滚动条也不应在 Track 计算后强制撑高滑块。");

        if (grid.IsVisible)
        {
            grid.ApplyTemplate();
            ScrollBar dataGridScrollBar =
                FindVisualChildren<ScrollBar>(grid)
                    .FirstOrDefault(scrollBar =>
                        scrollBar.Orientation == Orientation.Vertical) ??
                throw new AssertFailedException(
                    $"{gridName} 应生成模板内纵向滚动条。");
            dataGridScrollBar.ApplyTemplate();
            Assert.AreEqual(
                Brushes.Transparent,
                dataGridScrollBar.Background,
                $"{gridName} 的滚动轨道应保持原有透明规则。");
            Assert.AreSame(
                tableScrollBar.Template,
                dataGridScrollBar.Template,
                $"{gridName} 的实际滚动条必须使用 Track 原生尺寸模板。");
            Track actualTrack = Assert.IsInstanceOfType<Track>(
                dataGridScrollBar.Template.FindName(
                    "PART_Track",
                    dataGridScrollBar));
            Assert.AreSame(
                generalTrack.Thumb.Template,
                actualTrack.Thumb.Template,
                $"{gridName} 的实际滑块必须保持通用视觉模板。");
        }
    }

    private static void AssertDataGridThumbFitsTrackSlot(
        DataGrid grid,
        string gridName)
    {
        grid.ApplyTemplate();
        grid.UpdateLayout();
        ScrollBar verticalScrollBar =
            FindVisualChildren<ScrollBar>(grid)
                .FirstOrDefault(scrollBar =>
                    scrollBar.Orientation == Orientation.Vertical &&
                    scrollBar.IsVisible) ??
            throw new AssertFailedException(
                $"{gridName} 应显示纵向滚动条。");
        Thumb thumb = AssertScrollBarThumbFitsTrackSlot(
            verticalScrollBar,
            gridName);
        thumb.ApplyTemplate();
        Border thumbSurface =
            FindVisualChildren<Border>(thumb).FirstOrDefault() ??
            throw new AssertFailedException(
                $"{gridName} 应生成滑块表面。");
        Assert.AreEqual(
            new CornerRadius(4),
            thumbSurface.CornerRadius,
            $"{gridName} 应继续复用通用四角圆角。");
    }

    private static Thumb AssertScrollBarThumbFitsTrackSlot(
        ScrollBar scrollBar,
        string scrollBarName)
    {
        scrollBar.ApplyTemplate();
        Track track = Assert.IsInstanceOfType<Track>(
            scrollBar.Template.FindName(
                "PART_Track",
                scrollBar));
        Thumb thumb = Assert.IsInstanceOfType<Thumb>(track.Thumb);
        Rect layoutSlot = LayoutInformation.GetLayoutSlot(thumb);
        double slotLength = scrollBar.Orientation == Orientation.Vertical
            ? layoutSlot.Height
            : layoutSlot.Width;
        double renderedLength = scrollBar.Orientation == Orientation.Vertical
            ? thumb.RenderSize.Height
            : thumb.RenderSize.Width;
        Assert.IsGreaterThan(
            0d,
            slotLength,
            $"{scrollBarName} 的滑块布局槽必须可见。");
        Assert.IsLessThanOrEqualTo(
            slotLength + 0.01d,
            renderedLength,
            $"{scrollBarName} 的滑块不得大于 Track 分配的布局槽，否则末端圆角会被裁切。");
        return thumb;
    }

    private static void AssertSourceGridLayout(SourcesView view)
    {
        DataGrid grid = Assert.IsInstanceOfType<DataGrid>(
            view.FindName("SourcesGrid"));
        CollectionAssert.AreEqual(
            new[]
            {
                "采集状态",
                "兼容性",
                "兼容说明",
                "名称",
                "来源实例",
                "来源实体",
                "路径"
            },
            grid.Columns.Take(7)
                .Select(column => column.Header?.ToString())
                .ToArray());
        Assert.IsGreaterThanOrEqualTo(260d, grid.Columns[2].ActualWidth);
        Assert.IsGreaterThanOrEqualTo(150d, grid.Columns[3].ActualWidth);
        Assert.IsGreaterThanOrEqualTo(190d, grid.Columns[4].ActualWidth);
        Assert.IsGreaterThanOrEqualTo(220d, grid.Columns[5].ActualWidth);
        Assert.IsGreaterThanOrEqualTo(290d, grid.Columns[6].ActualWidth);
        Assert.AreEqual(
            ScrollBarVisibility.Auto,
            ScrollViewer.GetHorizontalScrollBarVisibility(grid));
        Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(grid));
        Assert.AreEqual(
            VirtualizationMode.Recycling,
            VirtualizingPanel.GetVirtualizationMode(grid));

        foreach (DataGridColumn column in grid.Columns.Skip(2).Take(5))
        {
            DataGridTextColumn textColumn =
                Assert.IsInstanceOfType<DataGridTextColumn>(column);
            Style? style = textColumn.ElementStyle;
            Assert.IsNotNull(style);
            bool exposesFullText = style.Setters.OfType<Setter>().Any(setter =>
                setter.Property == FrameworkElement.ToolTipProperty &&
                setter.Value is Binding);
            Assert.IsTrue(
                exposesFullText,
                $"{column.Header} 列应通过 ToolTip 暴露完整值。");
        }

        Layout(view, 720, 560);
        double columnsWidth = grid.Columns.Sum(column => column.ActualWidth);
        Assert.IsGreaterThan(grid.ActualWidth, columnsWidth);
    }

    private static void AssertUnifiedControlTemplates()
    {
        var comboBox = new ComboBox
        {
            ItemsSource = new[] { "全部平台", "Codex" },
            SelectedIndex = 0,
            Style = Assert.IsInstanceOfType<Style>(
                Application.Current.FindResource(typeof(ComboBox))),
        };
        Layout(comboBox, 160, 36);
        Assert.IsNotNull(comboBox.Template);
        Assert.IsNotNull(
            comboBox.Template.FindName("ToggleButton", comboBox),
            "ComboBox 应使用共享自定义开关与箭头模板。");
        Assert.IsNull(
            comboBox.Template.FindName("FocusRing", comboBox),
            "ComboBox 不应在获得焦点后叠加橙色选中框。");

        var comboBoxItem = new ComboBoxItem
        {
            Content = "kimi-code",
            Style = Assert.IsInstanceOfType<Style>(
                Application.Current.FindResource(typeof(ComboBoxItem))),
        };
        Layout(comboBoxItem, 160, 36);
        Assert.IsNotNull(comboBoxItem.Template);
        Trigger[] comboBoxItemTriggers = comboBoxItem.Template.Triggers
            .OfType<Trigger>()
            .ToArray();
        Assert.IsTrue(
            comboBoxItemTriggers.Any(trigger =>
                trigger.Property == UIElement.IsMouseOverProperty &&
                Equals(trigger.Value, true)),
            "ComboBoxItem 的悬停背景必须跟随真实 IsMouseOver，鼠标离开弹层后应立即退出。");
        Assert.IsFalse(
            comboBoxItemTriggers.Any(trigger =>
                string.Equals(
                    trigger.Property?.Name,
                    "IsHighlighted",
                    StringComparison.Ordinal) &&
                trigger.Setters.OfType<Setter>().Any(setter =>
                    string.Equals(
                        setter.TargetName,
                        "ItemBorder",
                        StringComparison.Ordinal) &&
                    setter.Property == Border.BackgroundProperty)),
            "ComboBoxItem 不应把可残留的逻辑 IsHighlighted 当成鼠标悬停背景。");

        foreach ((string styleKey, string controlName) in new[]
        {
            ("BaseButtonStyle", "基础按钮"),
            ("SecondaryButtonStyle", "次级按钮"),
            ("PrimaryButtonStyle", "主按钮"),
            ("NavButtonStyle", "导航按钮"),
        })
        {
            var button = new Button
            {
                Content = controlName,
                Style = Assert.IsInstanceOfType<Style>(
                    Application.Current.FindResource(styleKey)),
            };
            Layout(button, 160, 40);
            Assert.IsNotNull(button.Template);
            Assert.IsNull(
                button.Template.FindName("FocusRing", button),
                $"{controlName}不应在获得焦点后叠加橙色选中框。");
        }

        var textBox = new TextBox
        {
            Text = @"C:\Projects\AgenTally\artifacts\development\data\agentally.db",
            IsReadOnly = true,
            Style = Assert.IsInstanceOfType<Style>(
                Application.Current.FindResource(typeof(TextBox))),
        };
        Layout(textBox, 360, 36);
        Assert.IsNotNull(textBox.Template);
        Assert.IsNotNull(
            textBox.Template.FindName("InputBorder", textBox),
            "TextBox 应使用共享圆角输入框模板。");

        var datePicker = new DatePicker
        {
            SelectedDate = new DateTime(2026, 7, 28),
            Style = Assert.IsInstanceOfType<Style>(
                Application.Current.FindResource(typeof(DatePicker))),
        };
        Layout(datePicker, 160, 36);
        Assert.IsNotNull(datePicker.Template);
        Assert.IsNotNull(
            datePicker.Template.FindName("PART_Button", datePicker),
            "DatePicker 应使用共享日历按钮模板。");

        var scrollViewer = new ScrollViewer
        {
            Content = new Border { Width = 4000, Height = 4000 },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = Assert.IsInstanceOfType<Style>(
                Application.Current.FindResource(typeof(ScrollViewer))),
        };
        Layout(scrollViewer, 180, 120);
        Assert.IsNotNull(scrollViewer.Template);
        Assert.IsNotNull(
            scrollViewer.Template.FindName(
                "PART_VerticalScrollBar",
                scrollViewer));
        Assert.IsNotNull(
            scrollViewer.Template.FindName(
                "PART_HorizontalScrollBar",
                scrollViewer));
        ScrollBar verticalScrollBar = Assert.IsInstanceOfType<ScrollBar>(
            scrollViewer.Template.FindName(
                "PART_VerticalScrollBar",
                scrollViewer));
        Assert.AreEqual(
            Brushes.Transparent,
            verticalScrollBar.Background,
            "非表格滚动条应保持原有透明轨道规则。");
        ScrollBar horizontalScrollBar = Assert.IsInstanceOfType<ScrollBar>(
            scrollViewer.Template.FindName(
                "PART_HorizontalScrollBar",
                scrollViewer));
        scrollViewer.ScrollToEnd();
        scrollViewer.ScrollToRightEnd();
        scrollViewer.UpdateLayout();
        AssertUnifiedVerticalScrollBar(
            verticalScrollBar,
            "通用纵向滚动条");
        Thumb verticalThumb = AssertScrollBarThumbFitsTrackSlot(
            verticalScrollBar,
            "通用纵向滚动条");
        Track verticalTrack = Assert.IsInstanceOfType<Track>(
            verticalScrollBar.Template.FindName(
                "PART_Track",
                verticalScrollBar));
        Assert.AreEqual(8d, verticalTrack.ActualWidth, 0.01d);
        Assert.AreEqual(
            4d,
            verticalTrack.TranslatePoint(new Point(), verticalScrollBar).X,
            0.01d,
            "通用纵向滚动条应保留 4 DIP 内容间隔和完整 8 DIP 轨道。");
        verticalThumb.ApplyTemplate();
        Border verticalThumbSurface = FindVisualChildren<Border>(verticalThumb)
            .First();
        Assert.AreEqual(
            new Thickness(0d, 1d, 0d, 1d),
            verticalThumbSurface.Margin,
            "通用纵向滑块不得再横向缩窄。");

        Assert.AreEqual(
            12d,
            horizontalScrollBar.Height,
            0.01d,
            "通用横向滚动条应保留 12 DIP 透明交互区。");
        Thumb horizontalThumb = AssertScrollBarThumbFitsTrackSlot(
            horizontalScrollBar,
            "通用横向滚动条");
        Track horizontalTrack = Assert.IsInstanceOfType<Track>(
            horizontalScrollBar.Template.FindName(
                "PART_Track",
                horizontalScrollBar));
        Assert.AreEqual(1, Grid.GetRow(horizontalTrack));
        Assert.AreEqual(8d, horizontalTrack.ActualHeight, 0.01d);
        Assert.AreEqual(
            4d,
            horizontalTrack.TranslatePoint(new Point(), horizontalScrollBar).Y,
            0.01d,
            "通用横向滚动条应保留 4 DIP 内容间隔和完整 8 DIP 轨道。");
        horizontalThumb.ApplyTemplate();
        Border horizontalThumbSurface = FindVisualChildren<Border>(horizontalThumb)
            .First();
        Assert.AreEqual(
            new Thickness(1d, 0d, 1d, 0d),
            horizontalThumbSurface.Margin,
            "通用横向滑块不得再纵向缩窄。");
    }

    private static void AssertSegmentedTabs(AnalysisView view)
    {
        TabControl tabs = Assert.IsInstanceOfType<TabControl>(
            view.FindName("AnalysisTabs"));
        tabs.ApplyTemplate();
        Border headerSurface = Assert.IsInstanceOfType<Border>(
            tabs.Template.FindName("HeaderSurface", tabs));
        Assert.IsFalse(headerSurface.ClipToBounds);

        for (int selectedIndex = 0; selectedIndex < tabs.Items.Count; selectedIndex++)
        {
            tabs.SelectedIndex = selectedIndex;
            view.UpdateLayout();
            TabItem selectedItem = Assert.IsInstanceOfType<TabItem>(
                tabs.ItemContainerGenerator.ContainerFromIndex(selectedIndex));
            selectedItem.ApplyTemplate();
            Border selectedBorder = Assert.IsInstanceOfType<Border>(
                selectedItem.Template.FindName("TabBorder", selectedItem));
            Assert.IsNull(
                selectedItem.Template.FindName("FocusRing", selectedItem),
                $"分段项 {selectedIndex} 不应显示橙色焦点环。");
            Assert.AreEqual(new Thickness(1), selectedBorder.Margin);
            Assert.AreEqual(2, Panel.GetZIndex(selectedItem));

            Rect bounds = selectedBorder.TransformToAncestor(headerSurface)
                .TransformBounds(new Rect(selectedBorder.RenderSize));
            Assert.IsGreaterThanOrEqualTo(
                -0.5d,
                bounds.Left,
                $"分段项 {selectedIndex} 的左边界不应被裁切。");
            Assert.IsLessThanOrEqualTo(
                headerSurface.ActualWidth + 0.5d,
                bounds.Right,
                $"分段项 {selectedIndex} 的右边界不应被裁切。");
        }

        tabs.SelectedIndex = 0;
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static IEnumerable<string> EnumerateVisibleText(DependencyObject root)
    {
        if (root is UIElement element &&
            element.Visibility != Visibility.Visible)
        {
            yield break;
        }

        if (root is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            yield return textBlock.Text;
        }

        if (root is ContentControl contentControl && contentControl.Content is string text)
        {
            yield return text;
        }

        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < children; index++)
        {
            foreach (string nestedText in EnumerateVisibleText(
                VisualTreeHelper.GetChild(root, index)))
            {
                yield return nestedText;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < children; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void AssertTopFilterLayout(
        FrameworkElement view,
        string filterBarName,
        string projectFilterName)
    {
        Grid filterBar = Assert.IsInstanceOfType<Grid>(
            view.FindName(filterBarName));
        ComboBox projectFilter = Assert.IsInstanceOfType<ComboBox>(
            view.FindName(projectFilterName));
        Assert.IsGreaterThan(0d, filterBar.ActualWidth);
        Assert.IsLessThanOrEqualTo(
            Math.Min(view.ActualWidth, 900d) + 0.5d,
            filterBar.ActualWidth,
            $"{filterBarName} 不应超过 900px。");
        Assert.IsGreaterThan(
            0d,
            projectFilter.ActualWidth,
            $"{projectFilterName} 应保持可见。");
        Assert.AreEqual(
            112d,
            projectFilter.ActualWidth,
            0.5d,
            $"{projectFilterName} 应使用紧凑的固定触发器宽度。");
        var popupContainer = new ComboBoxItem
        {
            Style = projectFilter.ItemContainerStyle
        };
        Assert.AreEqual(
            300d,
            popupContainer.MinWidth,
            0.5d,
            $"{projectFilterName} 的下拉面板应可宽于触发器。");

        double previousRight = 0d;
        foreach (FrameworkElement control in filterBar.Children
                     .OfType<FrameworkElement>()
                     .OrderBy(Grid.GetColumn))
        {
            Point origin = control.TranslatePoint(new Point(0d, 0d), filterBar);
            Assert.IsGreaterThanOrEqualTo(
                previousRight - 0.5d,
                origin.X,
                $"{filterBarName} 中的筛选控件不应重叠。");
            Assert.IsLessThanOrEqualTo(
                filterBar.ActualWidth + 0.5d,
                origin.X + control.ActualWidth,
                $"{filterBarName} 中的筛选控件不应越出可用宽度。");
            previousRight = origin.X + control.ActualWidth;
        }

        foreach (ComboBox comboBox in filterBar.Children.OfType<ComboBox>())
        {
            Assert.AreEqual(
                0d,
                comboBox.MinWidth,
                $"{filterBarName} 中的筛选框必须允许在最小窗口内收缩。");
            comboBox.ApplyTemplate();
            FrameworkElement selectionHost =
                Assert.IsInstanceOfType<FrameworkElement>(
                    comboBox.Template.FindName("SelectionHost", comboBox));
            FrameworkElement dropDownGlyph =
                Assert.IsInstanceOfType<FrameworkElement>(
                    comboBox.Template.FindName("DropDownGlyph", comboBox));
            Point selectionOrigin = selectionHost.TranslatePoint(
                new Point(0d, 0d),
                comboBox);
            Point glyphOrigin = dropDownGlyph.TranslatePoint(
                new Point(0d, 0d),
                comboBox);
            Assert.IsLessThanOrEqualTo(
                glyphOrigin.X + 0.5d,
                selectionOrigin.X + selectionHost.ActualWidth,
                $"{filterBarName} 的选择文本区域不得覆盖下拉箭头。");
        }

        Grid commonGroup = Assert.ContainsSingle(
            filterBar.Children.OfType<Grid>());
        Assert.AreEqual(
            432d,
            commonGroup.ActualWidth,
            0.5d,
            $"{filterBarName} 的公共筛选组应保持固定宽度。");
        ComboBox[] commonFilters =
            commonGroup.Children.OfType<ComboBox>().ToArray();
        Assert.HasCount(3, commonFilters);
        Assert.IsTrue(commonFilters.All(filter =>
            Math.Abs(filter.ActualWidth - 112d) <= 0.5d &&
            Math.Abs(filter.ActualHeight - 36d) <= 0.5d));
        Button refresh = Assert.ContainsSingle(
            commonGroup.Children.OfType<Button>());
        Assert.AreEqual(72d, refresh.ActualWidth, 0.5d);
        Assert.AreEqual(36d, refresh.ActualHeight, 0.5d);
    }

    private static void AssertStatisticsHeaderAlignment(
        DashboardView dashboard,
        AnalysisView analysis,
        ProjectsView projects)
    {
        (
            FrameworkElement View,
            string HeaderName,
            string GroupName,
            string[] ControlNames)[] pages =
        [
            (
                dashboard,
                "DashboardHeader",
                "DashboardCommonFilterGroup",
                [
                    "DashboardAgentFilter",
                    "DashboardModelFilter",
                    "DashboardPeriodFilter",
                    "DashboardRefreshButton"
                ]),
            (
                analysis,
                "AnalysisHeader",
                "AnalysisCommonFilterGroup",
                [
                    "AnalysisAgentFilter",
                    "AnalysisModelFilter",
                    "AnalysisPeriodFilter",
                    "AnalysisRefreshButton"
                ]),
            (
                projects,
                "ProjectsHeader",
                "ProjectsCommonFilterGroup",
                [
                    "ProjectsAgentFilter",
                    "ProjectsModelFilter",
                    "ProjectsPeriodFilter",
                    "ProjectsRefreshButton"
                ])
        ];
        Point? expectedGroupOrigin = null;
        Point[]? expectedControlOrigins = null;
        foreach ((
                     FrameworkElement view,
                     string headerName,
                     string groupName,
                     string[] controlNames) in pages)
        {
            Grid header = Assert.IsInstanceOfType<Grid>(
                view.FindName(headerName));
            FrameworkElement[] headerColumns =
                header.Children.OfType<FrameworkElement>().ToArray();
            Assert.HasCount(2, headerColumns);
            Point titleOrigin = headerColumns[0].TranslatePoint(
                new Point(),
                header);
            Point filtersOrigin = headerColumns[1].TranslatePoint(
                new Point(),
                header);
            Assert.IsGreaterThanOrEqualTo(
                titleOrigin.X + headerColumns[0].ActualWidth,
                filtersOrigin.X,
                $"{headerName} 的标题不得与筛选器重叠。");

            Grid group = Assert.IsInstanceOfType<Grid>(
                view.FindName(groupName));
            Point groupOrigin = group.TranslatePoint(new Point(), view);
            if (expectedGroupOrigin is Point expected)
            {
                Assert.AreEqual(
                    expected.X,
                    groupOrigin.X,
                    0.5d,
                    $"{groupName} 的公共筛选组横向起点应保持一致。");
                Assert.AreEqual(
                    expected.Y,
                    groupOrigin.Y,
                    0.5d,
                    $"{groupName} 的公共筛选组纵向起点应保持一致。");
            }
            else
            {
                expectedGroupOrigin = groupOrigin;
            }

            Point[] origins = controlNames
                .Select(name => Assert.IsInstanceOfType<FrameworkElement>(
                        view.FindName(name))
                    .TranslatePoint(new Point(), view))
                .ToArray();
            for (int index = 0; index < origins.Length; index++)
            {
                FrameworkElement control =
                    Assert.IsInstanceOfType<FrameworkElement>(
                        view.FindName(controlNames[index]));
                Assert.IsLessThanOrEqualTo(
                    view.ActualWidth,
                    origins[index].X + control.ActualWidth,
                    $"{controlNames[index]} 不得被页面右侧裁切。");
            }

            if (expectedControlOrigins is not null)
            {
                for (int index = 0; index < origins.Length; index++)
                {
                    Assert.AreEqual(
                        expectedControlOrigins[index].X,
                        origins[index].X,
                        0.5d,
                        $"{controlNames[index]} 的横向位置应跨页面稳定。");
                    Assert.AreEqual(
                        expectedControlOrigins[index].Y,
                        origins[index].Y,
                        0.5d,
                        $"{controlNames[index]} 的纵向位置应跨页面稳定。");
                }
            }
            else
            {
                expectedControlOrigins = origins;
            }
        }

        Grid projectsFilterBar = Assert.IsInstanceOfType<Grid>(
            projects.FindName("ProjectsFilterBar"));
        Assert.HasCount(
            3,
            FindVisualChildren<ComboBox>(projectsFilterBar).ToArray(),
            "项目页只应包含平台、模型和时间三个公共筛选器。");
    }

    private static void AssertMetricCardRows(
        DashboardView dashboard,
        AnalysisView analysis)
    {
        Grid dashboardGrid = Assert.IsInstanceOfType<Grid>(
            dashboard.FindName("DashboardMetricsGrid"));
        Grid analysisGrid = Assert.IsInstanceOfType<Grid>(
            analysis.FindName("AnalysisMetricsGrid"));
        Assert.HasCount(3, dashboardGrid.RowDefinitions);
        Assert.HasCount(
            2,
            analysisGrid.RowDefinitions,
            "分析指标卡移除计价说明后只应保留标签与主数值两行。");
        Assert.AreEqual(
            48d,
            dashboardGrid.RowDefinitions[1].Height.Value,
            0.01d,
            "概览主数值应保留固定排版区域。");
        Assert.AreEqual(
            48d,
            analysisGrid.RowDefinitions[1].Height.Value,
            0.01d,
            "分析主数值应保留固定排版区域。");
        Border analysisCard = Assert.IsInstanceOfType<Border>(
            analysis.FindName("AnalysisMetricsCard"));
        Assert.AreEqual(
            104d,
            analysisCard.MinHeight,
            0.01d,
            "分析指标卡移除说明行后应同步收紧最小高度。");
        Assert.HasCount(7, analysisGrid.ColumnDefinitions);
        for (int index = 0; index < analysisGrid.ColumnDefinitions.Count; index++)
        {
            ColumnDefinition column = analysisGrid.ColumnDefinitions[index];
            if (index % 2 == 0)
            {
                Assert.IsTrue(
                    column.Width.IsAuto,
                    "分析指标内容列应按实际文字宽度排版。");
            }
            else
            {
                Assert.IsTrue(
                    column.Width.IsStar,
                    "分析指标间隔应平均分享剩余宽度。");
                Assert.AreEqual(32d, column.MinWidth, 0.01d);
            }
        }

        foreach (string name in new[]
                 {
                     "TotalTokensValue",
                     "DashboardEquivalentValue",
                     "DashboardRequestValue"
                 })
        {
            TextBlock value = Assert.IsInstanceOfType<TextBlock>(
                dashboard.FindName(name));
            Assert.AreEqual(1, Grid.GetRow(value));
            Assert.AreEqual(VerticalAlignment.Center, value.VerticalAlignment);
        }

        foreach (string name in new[]
                 {
                     "AnalysisTotalValue",
                     "AnalysisRequestValue",
                     "AnalysisDailyAverageValue",
                     "AnalysisEquivalentValue"
                 })
        {
            TextBlock value = Assert.IsInstanceOfType<TextBlock>(
                analysis.FindName(name));
            Assert.AreEqual(1, Grid.GetRow(value));
            Assert.AreEqual(VerticalAlignment.Center, value.VerticalAlignment);
            Assert.AreEqual(
                23d,
                value.FontSize,
                0.01d,
                "分析页四个主数值应使用一致字号。");
        }

        Assert.AreEqual(
            2,
            Grid.GetRow(Assert.IsInstanceOfType<TextBlock>(
                dashboard.FindName("DashboardEquivalentCaption"))));
        Assert.IsNull(analysis.FindName("AnalysisEquivalentCaption"));
    }

    private static void AssertStatisticsFirstContentGaps(
        DashboardView dashboard,
        AnalysisView analysis,
        ProjectsView projects,
        SessionsView sessions)
    {
        (
            FrameworkElement View,
            string HeaderName,
            string ContentName)[] pages =
        [
            (
                dashboard,
                "DashboardHeader",
                "DashboardMetricsCard"),
            (
                analysis,
                "AnalysisHeader",
                "AnalysisMetricsCard"),
            (
                projects,
                "ProjectsHeader",
                "ProjectsContent"),
            (
                sessions,
                "SessionsHeader",
                "SessionsListCard")
        ];

        foreach ((
                     FrameworkElement view,
                     string headerName,
                     string contentName) in pages)
        {
            FrameworkElement header =
                Assert.IsInstanceOfType<FrameworkElement>(
                    view.FindName(headerName));
            FrameworkElement content =
                Assert.IsInstanceOfType<FrameworkElement>(
                    view.FindName(contentName));
            Point headerOrigin = header.TranslatePoint(new Point(), view);
            Point contentOrigin = content.TranslatePoint(new Point(), view);
            double gap =
                contentOrigin.Y - (headerOrigin.Y + header.ActualHeight);
            Assert.AreEqual(
                8d,
                gap,
                0.5d,
                $"{contentName} 与标题区之间应在移除顶部状态行后" +
                "使用统一的紧凑间距。");
        }
    }

    private static void AssertRoundedTableContentClip(
        DataGrid grid,
        string gridName)
    {
        FrameworkElement content = Assert.IsInstanceOfType<FrameworkElement>(
            grid.Parent);
        Border card = Assert.IsInstanceOfType<Border>(content.Parent);
        Assert.IsFalse(
            card.ClipToBounds,
            $"{gridName} 的外层卡片继续由内部几何负责圆角裁剪。");
        Assert.IsNull(
            card.Effect,
            $"{gridName} 的圆角卡片不得保留可能呈现直角边缘的阴影。");
        RectangleGeometry clip = Assert.IsInstanceOfType<RectangleGeometry>(
            content.Clip);
        Assert.AreEqual(content.ActualWidth, clip.Rect.Width, 0.5d);
        Assert.AreEqual(content.ActualHeight, clip.Rect.Height, 0.5d);
        Assert.IsGreaterThan(
            0d,
            clip.RadiusX,
            $"{gridName} 的表格内容必须按卡片圆角裁剪。");
        Assert.AreEqual(clip.RadiusX, clip.RadiusY, 0.01d);
        Assert.IsLessThanOrEqualTo(
            card.CornerRadius.TopLeft + 0.01d,
            clip.RadiusX,
            $"{gridName} 的内容圆角不能越出卡片外圆角。");
    }

    private static void AssertRoundedCardsHaveNoShadowEffects(
        FrameworkElement view,
        string pageTitle)
    {
        Border[] roundedCards = FindVisualChildren<Border>(view)
            .Where(border =>
                (border.CornerRadius.TopLeft > 0d ||
                 border.CornerRadius.TopRight > 0d ||
                 border.CornerRadius.BottomRight > 0d ||
                 border.CornerRadius.BottomLeft > 0d))
            .ToArray();
        Assert.IsGreaterThan(
            0,
            roundedCards.Length,
            $"{pageTitle} 应至少生成一个圆角表面。");
        foreach (Border card in roundedCards)
        {
            Assert.IsFalse(
                card.Effect is
                    System.Windows.Media.Effects.DropShadowEffect,
                $"{pageTitle} 的圆角表面不得保留可能呈现直角边缘的阴影。");
        }
    }

    private static void AssertCenteredHeaders(
        FrameworkElement view,
        string gridName)
    {
        DataGrid grid = Assert.IsInstanceOfType<DataGrid>(
            view.FindName(gridName));
        Style implicitHeaderStyle = Assert.IsInstanceOfType<Style>(
            grid.FindResource(typeof(DataGridColumnHeader)));
        foreach (DataGridColumn column in grid.Columns)
        {
            var header = new DataGridColumnHeader
            {
                Style = column.HeaderStyle ??
                    grid.ColumnHeaderStyle ??
                    implicitHeaderStyle
            };
            Assert.AreEqual(
                HorizontalAlignment.Center,
                header.HorizontalContentAlignment,
                $"{gridName} 的“{column.Header}”表头应居中对齐。");
        }
    }

    private static void AssertCenteredDailyDateCells(
        FrameworkElement view)
    {
        DataGrid grid = Assert.IsInstanceOfType<DataGrid>(
            view.FindName("DailyGrid"));
        DataGridTextColumn dateColumn =
            Assert.IsInstanceOfType<DataGridTextColumn>(grid.Columns[0]);
        Style style = Assert.IsInstanceOfType<Style>(dateColumn.ElementStyle);
        var textBlock = new TextBlock { Style = style };
        Assert.AreEqual(
            HorizontalAlignment.Center,
            textBlock.HorizontalAlignment,
            "DailyGrid 的日期正文应居中对齐。");
        Assert.AreEqual(
            TextAlignment.Center,
            textBlock.TextAlignment,
            "DailyGrid 的日期正文文字应居中对齐。");
    }

    private static void AssertBillingTokenCategoryOrder(
        string text,
        string context)
    {
        int cachedIndex = text.IndexOf(
            "缓存输入",
            StringComparison.Ordinal);
        int uncachedIndex = text.IndexOf(
            "未缓存输入",
            StringComparison.Ordinal);
        int outputIndex = text.IndexOf(
            "输出",
            StringComparison.Ordinal);
        Assert.IsTrue(
            cachedIndex >= 0 &&
            uncachedIndex > cachedIndex &&
            outputIndex > uncachedIndex,
            $"{context} 应按“缓存输入 / 未缓存输入 / 输出”排列。");
    }

    private static void AssertBillingTokenCategoryOrder(
        FrameworkElement view,
        string gridName)
    {
        DataGrid grid = Assert.IsInstanceOfType<DataGrid>(
            view.FindName(gridName));
        AssertBillingTokenCategoryOrder(
            string.Join(
                " / ",
                grid.Columns.Select(column => column.Header?.ToString())),
            $"{gridName} 表头");
    }

    private static FrameworkElement FindRenderedPage(
        ContentControl content,
        PageViewModel page)
    {
        FrameworkElement? renderedPage = page switch
        {
            DashboardViewModel => FindVisualChild<DashboardView>(content) as FrameworkElement,
            AnalysisViewModel => FindVisualChild<AnalysisView>(content),
            ProjectsViewModel => FindVisualChild<ProjectsView>(content),
            SessionsViewModel => FindVisualChild<SessionsView>(content),
            SourcesViewModel => FindVisualChild<SourcesView>(content),
            SettingsViewModel => FindVisualChild<SettingsView>(content),
            _ => null,
        };
        return renderedPage ?? throw new AssertFailedException(
            $"{page.Title} 的当前页面视图未生成。");
    }

    private static async Task AssertCurrentPageReflowsAcrossWindowStatesAsync(
        MainWindow window,
        ContentControl content,
        PageViewModel page,
        Button maximizeButton)
    {
        for (int cycle = 0; cycle < 4; cycle++)
        {
            foreach (WindowState expectedState in new[]
            {
                WindowState.Maximized,
                WindowState.Normal,
            })
            {
                maximizeButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Render);
                Assert.AreEqual(
                    expectedState,
                    window.WindowState,
                    $"第 {cycle + 1} 轮窗口切换状态错误。");
                FrameworkElement renderedPage = FindRenderedPage(content, page);
                Assert.AreEqual(
                    content.ActualWidth,
                    renderedPage.ActualWidth,
                    1d,
                    $"第 {cycle + 1} 轮 {expectedState} 后{page.Title}页应重新填满页面宿主宽度。");
                Assert.AreEqual(
                    content.ActualHeight,
                    renderedPage.ActualHeight,
                    1d,
                    $"第 {cycle + 1} 轮 {expectedState} 后{page.Title}页应重新填满页面宿主高度。");
            }
        }
    }

    private static void AssertNeutralPricePresentation(
        FrameworkElement view,
        string valueName,
        string captionName)
    {
        AssertNeutralPriceValuePresentation(view, valueName);
        TextBlock caption = Assert.IsInstanceOfType<TextBlock>(
            view.FindName(captionName));
        Assert.AreEqual(
            Application.Current.FindResource("TextTertiaryBrush"),
            caption.Foreground,
            "价格短说明应保持现有中性 Caption 颜色。");
    }

    private static void AssertNeutralPriceValuePresentation(
        FrameworkElement view,
        string valueName)
    {
        TextBlock value = Assert.IsInstanceOfType<TextBlock>(
            view.FindName(valueName));
        Assert.AreEqual(
            Application.Current.FindResource("TextPrimaryBrush"),
            value.Foreground,
            "价格主值应保持现有中性主文字颜色。");
    }

    private static IEnumerable<T> FindVisualChildren<T>(
        DependencyObject root)
        where T : DependencyObject
    {
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < children; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static UsageTrendPoint Point(
        DateTimeOffset atUtc,
        long? total,
        long? uncached,
        long? output,
        long? cacheRead) => new(
        atUtc,
        TestData.Aggregate(total),
        TestData.Aggregate(uncached),
        TestData.Aggregate(output),
        TestData.Aggregate(cacheRead),
        TestData.Aggregate(null));

    private sealed class ParserRebuildRequiredCoreRuntimeController
        : ICoreRuntimeController
    {
        private static readonly CoreRuntimeUiStatus ParserRebuildRequired = new(
            CoreRuntimeUiState.ParserRebuildRequired,
            "需要重扫 AgenTally 的 Codex 派生数据；原始日志不会被修改。",
            true,
            false);

        public Task<CoreRuntimeUiStatus> EnsureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ParserRebuildRequired);
        }

        public Task<CoreRuntimeUiStatus> ReadStatusAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ParserRebuildRequired);
        }

        public Task<CoreRuntimeUiStatus> RebuildCodexAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ParserRebuildRequired);
        }

        public Task<CoreRuntimeUiStatus> ClearStatisticsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ParserRebuildRequired);
        }

    }

}
