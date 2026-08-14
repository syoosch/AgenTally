using System.IO;
using System.Windows.Threading;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Runtime;
using AgenTally.UI.Runtime;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class SettingsViewModelTests
{
    [TestMethod]
    public async Task StartupRegistration_UsesExplicitChoiceAndSurfacesConflict()
    {
        await using var host = new StaDispatcherTestHost();
        var store = new FakeStartupRegistrationStore(
            new StartupRegistrationStatus(
                StartupRegistrationState.Disabled));
        SettingsViewModel viewModel = await CreateAsync(
            host,
            new FakeUsageQueryService(),
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation(),
            startupRegistration: store,
            channel: AgenTallyChannel.Development);

        await host.InvokeAsync(() =>
        {
            Assert.IsFalse(viewModel.IsStartupEnabled);
            Assert.IsTrue(viewModel.CanChangeStartupRegistration);
            Assert.AreEqual(
                "Development 模拟，不修改 Windows",
                viewModel.StartupRegistrationDescription);

            viewModel.IsStartupEnabled = true;
            Assert.IsTrue(viewModel.IsStartupEnabled);
            CollectionAssert.AreEqual(
                new[] { true },
                store.Requests.ToArray());

            viewModel.IsStartupEnabled = false;
            Assert.IsFalse(viewModel.IsStartupEnabled);
            CollectionAssert.AreEqual(
                new[] { true, false },
                store.Requests.ToArray());
        });

        var conflictStore = new FakeStartupRegistrationStore(
            new StartupRegistrationStatus(
                StartupRegistrationState.Conflict,
                "检测到同名启动项"));
        SettingsViewModel conflict = await CreateAsync(
            host,
            new FakeUsageQueryService(),
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation(),
            startupRegistration: conflictStore,
            channel: AgenTallyChannel.Stable);

        await host.InvokeAsync(() =>
        {
            Assert.IsFalse(conflict.IsStartupEnabled);
            Assert.IsFalse(conflict.CanChangeStartupRegistration);
            Assert.IsTrue(conflict.HasStartupRegistrationMessage);
            Assert.AreEqual(
                "检测到同名启动项",
                conflict.StartupRegistrationMessage);
            conflict.IsStartupEnabled = true;
            Assert.IsEmpty(conflictStore.Requests);
        });
    }

    [TestMethod]
    public async Task CategoryNavigation_PreservesPricingDraftAndDetailState()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings = [BuiltInRow("gpt-5.3-codex")]
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation());
        await viewModel.RefreshAsync(CancellationToken.None);

        await host.InvokeAsync(() =>
        {
            Assert.AreEqual(SettingsSection.Home, viewModel.SelectedSection);
            Assert.IsTrue(viewModel.IsSettingsHome);
            viewModel.OpenSettingsSectionCommand.Execute(SettingsSection.Pricing);
            Assert.IsTrue(viewModel.IsPricingSettings);
            Assert.AreEqual("模型与计价", viewModel.SettingsSectionTitle);
            viewModel.PriceSearchText = "gpt";
            viewModel.IsLongContextExpanded = true;
            viewModel.InputRateText = "9";
            Assert.IsTrue(viewModel.HasUnsavedPriceChanges);

            viewModel.BackToSettingsHomeCommand.Execute(null);
            Assert.IsTrue(viewModel.IsSettingsHome);
            viewModel.OpenSettingsSectionCommand.Execute(SettingsSection.Privacy);
            Assert.IsTrue(viewModel.IsPrivacySettings);
            viewModel.OpenSettingsSectionCommand.Execute(SettingsSection.Pricing);

            Assert.AreEqual("gpt", viewModel.PriceSearchText);
            Assert.AreEqual("9", viewModel.InputRateText);
            Assert.IsTrue(viewModel.IsLongContextExpanded);
            Assert.IsTrue(viewModel.HasUnsavedPriceChanges);

            viewModel.RefreshIntervalSeconds = 5;
            Assert.AreEqual(5, viewModel.RefreshIntervalSeconds);

            viewModel.OpenSettingsSectionCommand.Execute(
                SettingsSection.DataAndBackup);
            Assert.IsFalse(viewModel.IsDataStorageExpanded);
            Assert.IsFalse(viewModel.IsDangerousDataActionsExpanded);
            viewModel.IsDataStorageExpanded = true;
            viewModel.IsDangerousDataActionsExpanded = true;
            viewModel.BackToSettingsHomeCommand.Execute(null);
            viewModel.OpenSettingsSectionCommand.Execute(
                SettingsSection.DataAndBackup);
            Assert.IsTrue(viewModel.IsDataStorageExpanded);
            Assert.IsTrue(viewModel.IsDangerousDataActionsExpanded);
        });
    }

    [TestMethod]
    public async Task Refresh_ExposesOnlyActionablePriceStatesAndFilters()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                BuiltInRow("gpt-5.3-codex"),
                new PriceSettingRow("observed-private", null, null, 7)
            ]
        };
        var client = new FakePriceCommandClient();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            client,
            new FakeRestoreConfirmation());

        await viewModel.RefreshAsync(CancellationToken.None);
        await host.InvokeAsync(() =>
        {
            Assert.HasCount(2, viewModel.PriceModels);
            Assert.AreEqual(
                "默认价格",
                viewModel.PriceModels.Single(row =>
                    row.NormalizedModel == "gpt-5.3-codex").SourceText);
            Assert.AreEqual(
                "未计价",
                viewModel.PriceModels.Single(row =>
                    row.NormalizedModel == "observed-private").SourceText);
            Assert.AreEqual(2, viewModel.ObservedPriceModelCount);
            Assert.AreEqual(1, viewModel.UnpricedPriceModelCount);
            Assert.AreEqual(0, viewModel.CustomPriceModelCount);
            Assert.AreEqual(
                "2 个已使用模型 · 1 个未计价 · 0 个自定义",
                viewModel.PriceSummaryText);

            viewModel.SetPriceFilterCommand.Execute(
                PriceModelFilter.Unpriced);
            Assert.HasCount(1, viewModel.PriceModels);
            Assert.AreEqual(
                "observed-private",
                viewModel.SelectedPriceModel?.NormalizedModel);
            viewModel.PriceSearchText = "missing";
            Assert.HasCount(0, viewModel.PriceModels);
            Assert.IsTrue(viewModel.HasNoVisiblePriceModels);
        });
    }

    [TestMethod]
    public async Task SelectingAnotherModel_RaisesEachConcreteEditorProperty()
    {
        await using var host = new StaDispatcherTestHost();
        var firstRate = new ModelPriceRate(
            "model-a",
            1m,
            0.1m,
            0.2m,
            10m,
            1_000,
            2m,
            3m);
        var secondRate = new ModelPriceRate(
            "model-b",
            4m,
            0.4m,
            null,
            20m);
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                new PriceSettingRow("model-a", firstRate, null, 2),
                new PriceSettingRow("model-b", secondRate, null, 1)
            ]
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation());
        await viewModel.RefreshAsync(CancellationToken.None);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) =>
            changedProperties.Add(eventArgs.PropertyName);

        await host.InvokeAsync(() =>
        {
            viewModel.IsLongContextExpanded = true;
            viewModel.SelectedPriceModel = viewModel.PriceModels.Single(row =>
                row.NormalizedModel == "model-b");
        });

        CollectionAssert.IsSubsetOf(
            new[]
            {
                nameof(SettingsViewModel.InputRateText),
                nameof(SettingsViewModel.CachedInputRateText),
                nameof(SettingsViewModel.CacheWriteRateText),
                nameof(SettingsViewModel.OutputRateText),
                nameof(SettingsViewModel.LongContextThresholdText),
                nameof(SettingsViewModel.LongContextInputMultiplierText),
                nameof(SettingsViewModel.LongContextOutputMultiplierText)
            },
            changedProperties.Where(static property => property is not null)
                .Cast<string>()
                .ToArray());
        Assert.DoesNotContain("SetEditorText", changedProperties);
        Assert.AreEqual("4", viewModel.InputRateText);
        Assert.AreEqual("0.4", viewModel.CachedInputRateText);
        Assert.AreEqual(string.Empty, viewModel.CacheWriteRateText);
        Assert.AreEqual("20", viewModel.OutputRateText);
        Assert.AreEqual(string.Empty, viewModel.LongContextThresholdText);
        Assert.AreEqual("1", viewModel.LongContextInputMultiplierText);
        Assert.AreEqual("1", viewModel.LongContextOutputMultiplierText);
        Assert.IsFalse(viewModel.IsLongContextExpanded);
        Assert.IsFalse(viewModel.HasUnsavedPriceChanges);
    }

    [TestMethod]
    public async Task DataOverview_FormatsFourUserMetricsAndPersistedBackupTime()
    {
        await using var host = new StaDispatcherTestHost();
        using var directory = new AgenTally.Tests.Support.TestTempDirectory();
        string databasePath = directory.File("agentally.db");
        await File.WriteAllBytesAsync(databasePath, new byte[1536]);
        DateTimeOffset first = new(2026, 8, 1, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset last = new(2026, 8, 11, 2, 0, 0, TimeSpan.Zero);
        var queries = new FakeUsageQueryService();
        queries.DashboardResult = queries.DashboardResult with
        {
            Overview = queries.DashboardResult.Overview with
            {
                RequestCount = 42,
                FirstOccurredAtUtc = first,
                LastOccurredAtUtc = last
            }
        };
        var state = new FakeDataManagementStateStore
        {
            LastSuccessfulBackupUtc =
                new DateTimeOffset(2026, 8, 10, 3, 4, 0, TimeSpan.Zero)
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation(),
            databasePath,
            state,
            TimeZoneInfo.Utc);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("1.5 KB", viewModel.DatabaseSizeText);
        Assert.AreEqual("42", viewModel.DataRequestCountText);
        Assert.AreEqual(
            "2026年8月1日 — 2026年8月11日",
            viewModel.DataTimeRangeText);
        Assert.AreEqual("2026年8月10日 03:04", viewModel.LastBackupText);
    }

    [TestMethod]
    public async Task DataOverview_QueryFailureNeverDisplaysUnknownAsZero()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardException = new IOException("unavailable")
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation());

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("暂时不可用", viewModel.DataRequestCountText);
        Assert.AreEqual("暂时不可用", viewModel.DataTimeRangeText);
        Assert.AreNotEqual("0", viewModel.DataRequestCountText);
        Assert.AreEqual("尚未备份", viewModel.LastBackupText);
    }

    [TestMethod]
    public async Task BackgroundRefresh_PreservesExpandedLongContextEditor()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings = [BuiltInRow("gpt-5.3-codex")]
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation());
        await viewModel.RefreshAsync(CancellationToken.None);
        await host.InvokeAsync(() =>
        {
            viewModel.IsLongContextExpanded = true;
            Assert.IsFalse(viewModel.HasUnsavedPriceChanges);
        });
        queries.PriceSettings =
        [
            new PriceSettingRow(
                "gpt-5.3-codex",
                BuiltInRate("gpt-5.3-codex"),
                null,
                2)
        ];

        await viewModel.RefreshInBackgroundAsync(CancellationToken.None);

        await host.InvokeAsync(() =>
        {
            Assert.AreEqual(
                "gpt-5.3-codex",
                viewModel.SelectedPriceModel?.NormalizedModel);
            Assert.AreEqual(2, viewModel.SelectedPriceModel?.ObservedRecords);
            Assert.IsTrue(viewModel.IsLongContextExpanded);
            Assert.IsFalse(viewModel.HasUnsavedPriceChanges);
        });
    }

    [TestMethod]
    public async Task Save_SendsCompleteRatePayloadAndRefreshesReadOnlySnapshot()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                new PriceSettingRow("private-model", null, null, 5)
            ]
        };
        var client = new FakePriceCommandClient();
        client.Handler = request =>
        {
            ModelPriceRate saved = request.Rate!.ToRate(request.NormalizedModel!);
            queries.PriceSettings =
            [
                new PriceSettingRow("private-model", null, saved, 5)
            ];
            return Task.FromResult(new PriceCommandResponse(
                PriceCommandProtocol.CurrentVersion,
                request.RequestId,
                PriceCommandResultCode.Success,
                PriceCommandMessageCodes.PriceUpdated,
                5));
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            client,
            new FakeRestoreConfirmation());
        await viewModel.RefreshAsync(CancellationToken.None);

        await host.InvokeAsync(async () =>
        {
            viewModel.SetPriceFilterCommand.Execute(PriceModelFilter.Unpriced);
            viewModel.InputRateText = "2";
            viewModel.CachedInputRateText = "0.2";
            viewModel.CacheWriteRateText = "2.5";
            viewModel.OutputRateText = "8";
            viewModel.LongContextThresholdText = "100000";
            viewModel.LongContextInputMultiplierText = "2";
            viewModel.LongContextOutputMultiplierText = "1.5";
            await viewModel.SavePriceCommand.ExecuteAsync();
        });

        Assert.IsNotNull(client.LastRequest);
        PriceCommandRequest request = client.LastRequest!;
        Assert.AreEqual(PriceCommandKind.SetPriceOverride, request.Command);
        Assert.AreEqual("private-model", request.NormalizedModel);
        Assert.AreEqual(2m, request.Rate?.InputUsdPerMillion);
        Assert.AreEqual(0.2m, request.Rate?.CachedInputUsdPerMillion);
        Assert.AreEqual(2.5m, request.Rate?.CacheWriteUsdPerMillion);
        Assert.AreEqual(8m, request.Rate?.OutputUsdPerMillion);
        Assert.AreEqual(100_000L, request.Rate?.LongContextThresholdTokens);
        Assert.AreEqual(2m, request.Rate?.LongContextInputMultiplier);
        Assert.AreEqual(1.5m, request.Rate?.LongContextOutputMultiplier);
        Assert.AreEqual(2, queries.PriceSettingCalls);
        Assert.AreEqual(PriceModelFilter.All, viewModel.SelectedPriceFilter);
        Assert.AreEqual(
            "自定义价格",
            viewModel.SelectedPriceModel?.SourceText);
        Assert.AreEqual("2", viewModel.InputRateText);
        Assert.AreEqual("0.2", viewModel.CachedInputRateText);
        Assert.AreEqual("2.5", viewModel.CacheWriteRateText);
        Assert.AreEqual("8", viewModel.OutputRateText);
        Assert.AreEqual("100000", viewModel.LongContextThresholdText);
        Assert.IsFalse(viewModel.HasUnsavedPriceChanges);
        Assert.IsFalse(viewModel.IsInheritedPriceEditor);
        StringAssert.Contains(viewModel.PriceOperationMessage, "5 条");
    }

    [TestMethod]
    public async Task Save_InvalidFieldsNeverSendCommand()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                new PriceSettingRow("private-model", null, null, 1)
            ]
        };
        var client = new FakePriceCommandClient();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            client,
            new FakeRestoreConfirmation());
        await viewModel.RefreshAsync(CancellationToken.None);

        await host.InvokeAsync(async () =>
        {
            viewModel.InputRateText = string.Empty;
            viewModel.OutputRateText = "not-a-number";
            await viewModel.SavePriceCommand.ExecuteAsync();
        });

        Assert.IsNull(client.LastRequest);
        Assert.IsTrue(viewModel.HasPriceValidationMessage);
    }

    [TestMethod]
    public async Task Restore_RequiresConfirmationAndUsesCorrectCommandScope()
    {
        await using var host = new StaDispatcherTestHost();
        ModelPriceRate custom = Rate("gpt-5.3-codex");
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                new PriceSettingRow(
                    "gpt-5.3-codex",
                    BuiltInRate("gpt-5.3-codex"),
                    custom,
                    2)
            ]
        };
        var client = new FakePriceCommandClient();
        var confirmation = new FakeRestoreConfirmation();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            client,
            confirmation);
        await viewModel.RefreshAsync(CancellationToken.None);
        await host.InvokeAsync(() =>
        {
            viewModel.SetPriceFilterCommand.Execute(PriceModelFilter.Custom);
            Assert.AreEqual("2", viewModel.InputRateText);
            Assert.AreEqual("8", viewModel.OutputRateText);
            Assert.IsFalse(viewModel.IsInheritedPriceEditor);
        });

        await host.InvokeAsync(() =>
            viewModel.RestorePriceCommand.ExecuteAsync());
        Assert.IsNull(client.LastRequest);
        Assert.AreEqual(1, confirmation.ModelCalls);

        confirmation.AllowModel = true;
        client.Handler = request =>
        {
            queries.PriceSettings =
            [
                BuiltInRow("gpt-5.3-codex")
            ];
            return Task.FromResult(Success(request));
        };
        await host.InvokeAsync(() =>
            viewModel.RestorePriceCommand.ExecuteAsync());

        Assert.AreEqual(
            PriceCommandKind.RestorePriceDefault,
            client.LastRequest?.Command);
        Assert.AreEqual(PriceModelFilter.All, viewModel.SelectedPriceFilter);
        Assert.AreEqual("默认价格", viewModel.SelectedPriceModel?.SourceText);
        Assert.AreEqual("1.75", viewModel.InputRateText);
        Assert.AreEqual("0.175", viewModel.CachedInputRateText);
        Assert.AreEqual(string.Empty, viewModel.CacheWriteRateText);
        Assert.AreEqual("14", viewModel.OutputRateText);
        Assert.AreEqual(string.Empty, viewModel.LongContextThresholdText);
        Assert.AreEqual("1", viewModel.LongContextInputMultiplierText);
        Assert.AreEqual("1", viewModel.LongContextOutputMultiplierText);
        Assert.IsFalse(viewModel.HasUnsavedPriceChanges);
        Assert.IsTrue(viewModel.IsInheritedPriceEditor);
    }

    [TestMethod]
    public async Task RestoreAll_RequiresConfirmationAndClearsEveryCustomRow()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                new PriceSettingRow(
                    "gpt-5.3-codex",
                    BuiltInRate("gpt-5.3-codex"),
                    Rate("gpt-5.3-codex"),
                    2),
                new PriceSettingRow(
                    "private-model",
                    null,
                    Rate("private-model"),
                    3)
            ]
        };
        var client = new FakePriceCommandClient();
        var confirmation = new FakeRestoreConfirmation();
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            client,
            confirmation);
        await viewModel.RefreshAsync(CancellationToken.None);

        await host.InvokeAsync(() =>
            viewModel.RestoreAllPricesCommand.ExecuteAsync());
        Assert.IsNull(client.LastRequest);
        Assert.AreEqual(1, confirmation.AllCalls);

        confirmation.AllowAll = true;
        client.Handler = request =>
        {
            queries.PriceSettings =
            [
                BuiltInRow("gpt-5.3-codex"),
                new PriceSettingRow("private-model", null, null, 3)
            ];
            return Task.FromResult(Success(request));
        };
        await host.InvokeAsync(() =>
            viewModel.RestoreAllPricesCommand.ExecuteAsync());

        Assert.AreEqual(
            PriceCommandKind.RestoreAllPriceDefaults,
            client.LastRequest?.Command);
        Assert.IsFalse(viewModel.PriceModels.Any(row => row.HasCustomPrice));
        Assert.IsFalse(viewModel.CanRestoreAllPrices);
    }

    [TestMethod]
    public async Task UnconfirmedResult_RefreshesSnapshotWithoutRetry()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                new PriceSettingRow("private-model", null, null, 1)
            ]
        };
        var client = new FakePriceCommandClient();
        client.Handler = request =>
        {
            queries.PriceSettings =
            [
                new PriceSettingRow(
                    "private-model",
                    null,
                    request.Rate!.ToRate("private-model"),
                    1)
            ];
            throw new PriceCommandResultUnconfirmedException("lost response");
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            client,
            new FakeRestoreConfirmation());
        await viewModel.RefreshAsync(CancellationToken.None);

        await host.InvokeAsync(async () =>
        {
            viewModel.InputRateText = "2";
            viewModel.OutputRateText = "8";
            await viewModel.SavePriceCommand.ExecuteAsync();
        });

        Assert.AreEqual(1, client.Calls);
        Assert.AreEqual(2, queries.PriceSettingCalls);
        Assert.IsTrue(viewModel.PriceOperationIsError);
        StringAssert.Contains(viewModel.PriceOperationMessage, "未能确认");
        Assert.IsTrue(viewModel.SelectedPriceModel?.HasCustomPrice);
        Assert.AreEqual("2", viewModel.InputRateText);
        Assert.AreEqual("8", viewModel.OutputRateText);
        Assert.IsFalse(viewModel.HasUnsavedPriceChanges);
    }

    [TestMethod]
    public async Task UnsavedDraft_SurvivesFilteringAndBackgroundRefresh()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            PriceSettings =
            [
                BuiltInRow("gpt-5.3-codex"),
                new PriceSettingRow("private-model", null, null, 7)
            ]
        };
        SettingsViewModel viewModel = await CreateAsync(
            host,
            queries,
            new FakePriceCommandClient(),
            new FakeRestoreConfirmation());
        await viewModel.RefreshAsync(CancellationToken.None);

        await host.InvokeAsync(() =>
        {
            Assert.AreEqual(
                "private-model",
                viewModel.SelectedPriceModel?.NormalizedModel);
            viewModel.InputRateText = "2";
            viewModel.OutputRateText = "8";
            Assert.IsTrue(viewModel.HasUnsavedPriceChanges);
            Assert.IsTrue(viewModel.SavePriceCommand.CanExecute(null));

            Assert.AreEqual("2", viewModel.InputRateText);
            Assert.AreEqual("8", viewModel.OutputRateText);
            Assert.IsTrue(viewModel.HasUnsavedPriceChanges);

            viewModel.PriceSearchText = "gpt";
            Assert.IsTrue(viewModel.PriceModels.Any(row =>
                row.NormalizedModel == "private-model"));
            Assert.AreEqual(
                "private-model",
                viewModel.SelectedPriceModel?.NormalizedModel);
        });

        queries.PriceSettings =
        [
            BuiltInRow("gpt-5.3-codex"),
            new PriceSettingRow("private-model", null, null, 9)
        ];
        await viewModel.RefreshInBackgroundAsync(CancellationToken.None);

        await host.InvokeAsync(() =>
        {
            Assert.AreEqual("2", viewModel.InputRateText);
            Assert.AreEqual("8", viewModel.OutputRateText);
            Assert.IsTrue(viewModel.HasUnsavedPriceChanges);

            PriceSettingPresentation gpt = viewModel.PriceModels.Single(row =>
                row.NormalizedModel == "gpt-5.3-codex");
            viewModel.SelectedPriceModel = gpt;
            Assert.AreEqual(
                "private-model",
                viewModel.SelectedPriceModel?.NormalizedModel);
            StringAssert.Contains(
                viewModel.PriceValidationMessage,
                "未保存修改");

            viewModel.DiscardPriceChangesCommand.Execute(null);
            Assert.IsFalse(viewModel.PriceModels.Any(row =>
                row.NormalizedModel == "private-model"));
            viewModel.SelectedPriceModel = gpt;
            Assert.AreEqual(
                "gpt-5.3-codex",
                viewModel.SelectedPriceModel?.NormalizedModel);
            Assert.IsFalse(viewModel.HasUnsavedPriceChanges);
            Assert.IsTrue(viewModel.IsInheritedPriceEditor);

            viewModel.InputRateText = "9";
            Assert.IsFalse(viewModel.IsInheritedPriceEditor);
            viewModel.DiscardPriceChangesCommand.Execute(null);
            Assert.AreEqual("1.75", viewModel.InputRateText);
            Assert.AreEqual("14", viewModel.OutputRateText);
            Assert.IsTrue(viewModel.IsInheritedPriceEditor);
        });
    }

    private static async Task<SettingsViewModel> CreateAsync(
        StaDispatcherTestHost host,
        FakeUsageQueryService queries,
        IPriceCommandClient client,
        IPriceRestoreConfirmation confirmation,
        string? databasePath = null,
        IDataManagementStateStore? dataManagementState = null,
        TimeZoneInfo? localTimeZone = null,
        IStartupRegistrationStore? startupRegistration = null,
        AgenTallyChannel? channel = null) =>
        await host.InvokeAsync(() => new SettingsViewModel(
            queries,
            client,
            confirmation,
            host.Dispatcher,
            databasePath ?? Path.Combine("data", "agentally.db"),
            channel,
            preferencesStore: new UnavailableUiPreferencesStore(),
            dataManagementState: dataManagementState,
            startupRegistration: startupRegistration,
            localTimeZone: localTimeZone));

    private sealed class FakeStartupRegistrationStore :
        IStartupRegistrationStore
    {
        private StartupRegistrationStatus _status;

        public FakeStartupRegistrationStore(
            StartupRegistrationStatus status)
        {
            _status = status;
        }

        public List<bool> Requests { get; } = [];

        public StartupRegistrationStatus Read() => _status;

        public StartupRegistrationStatus SetEnabled(bool enabled)
        {
            Requests.Add(enabled);
            _status = new StartupRegistrationStatus(enabled
                ? StartupRegistrationState.Enabled
                : StartupRegistrationState.Disabled);
            return _status;
        }
    }

    private static PriceSettingRow BuiltInRow(string model) => new(
        model,
        BuiltInRate(model),
        null,
        1);

    private static ModelPriceRate BuiltInRate(string model) => new(
        model,
        1.75m,
        0.175m,
        null,
        14m);

    private static ModelPriceRate Rate(string model) => new(
        model,
        2m,
        0.2m,
        2.5m,
        8m,
        100_000,
        2m,
        1.5m);

    private static PriceCommandResponse Success(PriceCommandRequest request) =>
        new(
            PriceCommandProtocol.CurrentVersion,
            request.RequestId,
            PriceCommandResultCode.Success,
            request.Command == PriceCommandKind.RestoreAllPriceDefaults
                ? PriceCommandMessageCodes.AllPriceDefaultsRestored
                : PriceCommandMessageCodes.PriceDefaultRestored,
            0);

    private sealed class FakePriceCommandClient : IPriceCommandClient
    {
        public bool IsAvailable { get; init; } = true;

        public int Calls { get; private set; }

        public PriceCommandRequest? LastRequest { get; private set; }

        public Func<PriceCommandRequest, Task<PriceCommandResponse>>? Handler
        {
            get;
            set;
        }

        public Task<PriceCommandResponse> SendAsync(
            PriceCommandRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Handler?.Invoke(request) ??
                Task.FromResult(new PriceCommandResponse(
                    PriceCommandProtocol.CurrentVersion,
                    request.RequestId,
                    PriceCommandResultCode.Success,
                    PriceCommandMessageCodes.PriceUpdated,
                    0));
        }
    }

    private sealed class FakeRestoreConfirmation : IPriceRestoreConfirmation
    {
        public bool AllowModel { get; set; }

        public bool AllowAll { get; set; }

        public int ModelCalls { get; private set; }

        public int AllCalls { get; private set; }

        public bool ConfirmModelRestore(
            string normalizedModel,
            bool hasBuiltInDefault)
        {
            ModelCalls++;
            return AllowModel;
        }

        public bool ConfirmAllRestore(int customPriceCount)
        {
            AllCalls++;
            return AllowAll;
        }
    }

    private sealed class FakeDataManagementStateStore :
        IDataManagementStateStore
    {
        public DateTimeOffset? LastSuccessfulBackupUtc { get; set; }

        public DateTimeOffset? ReadLastSuccessfulBackupUtc() =>
            LastSuccessfulBackupUtc;

        public bool TryWriteLastSuccessfulBackupUtc(DateTimeOffset value)
        {
            LastSuccessfulBackupUtc = value;
            return true;
        }
    }
}
