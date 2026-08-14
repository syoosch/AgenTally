using System.IO;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Runtime;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class UiPreferencesStoreTests
{
    [TestMethod]
    public void WindowSizeAndRefreshInterval_PersistWithoutOverwritingEachOther()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory, "user-one");
        var first = new JsonUiPreferencesStore(profile);

        Assert.IsTrue(first.TryWriteRefreshIntervalSeconds(10));
        Assert.IsTrue(first.TryWriteWindowSize(new UiWindowSize(1120d, 720d)));

        var reopened = new JsonUiPreferencesStore(profile);
        Assert.AreEqual(10, reopened.ReadRefreshIntervalSeconds());
        Assert.AreEqual(
            new UiWindowSize(1120d, 720d),
            reopened.ReadWindowSize());

        Assert.IsTrue(reopened.TryWriteRefreshIntervalSeconds(30));
        var updated = new JsonUiPreferencesStore(profile);
        Assert.AreEqual(30, updated.ReadRefreshIntervalSeconds());
        Assert.AreEqual(
            new UiWindowSize(1120d, 720d),
            updated.ReadWindowSize());
    }

    [TestMethod]
    public async Task RefreshInterval_PersistsAcrossUiProcessesWithinOneProfile()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory, "user-one");
        await using var firstHost = new StaDispatcherTestHost();
        SettingsViewModel first = await CreateSettingsAsync(firstHost, profile);

        Assert.AreEqual(3, first.RefreshIntervalSeconds);
        await firstHost.InvokeAsync(() => first.RefreshIntervalSeconds = 10);
        Assert.IsTrue(File.Exists(profile.UiPreferencesPath));

        await using var secondHost = new StaDispatcherTestHost();
        SettingsViewModel reopened = await CreateSettingsAsync(secondHost, profile);
        Assert.AreEqual(10, reopened.RefreshIntervalSeconds);
    }

    [TestMethod]
    public async Task RefreshInterval_IsProfileIsolatedAndInvalidValueFallsBackToDefault()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile firstProfile = CreateProfile(directory, "user-one");
        AgenTallyRuntimeProfile secondProfile = CreateProfile(directory, "user-two");
        Assert.AreNotEqual(
            firstProfile.UiPreferencesPath,
            secondProfile.UiPreferencesPath);

        await using var firstHost = new StaDispatcherTestHost();
        SettingsViewModel first = await CreateSettingsAsync(firstHost, firstProfile);
        await firstHost.InvokeAsync(() => first.RefreshIntervalSeconds = 30);

        await using var secondHost = new StaDispatcherTestHost();
        SettingsViewModel isolated = await CreateSettingsAsync(
            secondHost,
            secondProfile);
        Assert.AreEqual(3, isolated.RefreshIntervalSeconds);

        Directory.CreateDirectory(secondProfile.DataRoot);
        File.WriteAllText(
            secondProfile.UiPreferencesPath,
            "{\"schemaVersion\":1,\"refreshIntervalSeconds\":4}");
        await using var thirdHost = new StaDispatcherTestHost();
        SettingsViewModel invalid = await CreateSettingsAsync(
            thirdHost,
            secondProfile);
        Assert.AreEqual(3, invalid.RefreshIntervalSeconds);
    }

    private static AgenTallyRuntimeProfile CreateProfile(
        TestTempDirectory directory,
        string userName) =>
        AgenTallyRuntimeProfile.CreateStable(
            directory.File("app"),
            directory.File("local-app-data"),
            directory.File(userName));

    private static Task<SettingsViewModel> CreateSettingsAsync(
        StaDispatcherTestHost host,
        AgenTallyRuntimeProfile profile) =>
        host.InvokeAsync(() => new SettingsViewModel(
            queries: null,
            new UnavailablePriceCommandClient(),
            new RejectingPriceRestoreConfirmation(),
            host.Dispatcher,
            profile.DatabasePath,
            profile.Channel,
            new JsonUiPreferencesStore(profile)));
}
