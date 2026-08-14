using System.IO;
using System.Text.Json;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Storage;

[TestClass]
[DoNotParallelize]
public sealed class AgenTallyRuntimeProfileTests
{
    [TestMethod]
    public void BuildChannel_DefaultsToDevelopment()
    {
        Assert.AreEqual(AgenTallyChannel.Development, AgenTallyBuild.Channel);
    }

    [TestMethod]
    public void CreateDevelopment_ContainsEveryWritablePathInRepositoryArtifacts()
    {
        using var directory = new TestTempDirectory();
        CreateRepositoryMarkers(directory.Path);
        string codexHome = directory.File("readonly-codex");
        Directory.CreateDirectory(codexHome);

        AgenTallyRuntimeProfile profile =
            AgenTallyRuntimeProfile.CreateDevelopment(directory.Path, codexHome);

        Assert.AreEqual(AgenTallyChannel.Development, profile.Channel);
        Assert.AreEqual("AgenTally Dev", profile.DisplayName);
        string developmentRoot = Path.Combine(
            directory.Path,
            "artifacts",
            "development");
        AssertPathWithin(developmentRoot, profile.ApplicationRoot);
        AssertPathWithin(developmentRoot, profile.DataRoot);
        AssertPathWithin(developmentRoot, profile.RuntimeRoot);
        AssertPathWithin(developmentRoot, profile.LogRoot);
        AssertPathWithin(developmentRoot, profile.TempRoot);
        AssertPathWithin(developmentRoot, profile.DatabasePath);
        AssertPathWithin(developmentRoot, profile.StatusPath);
        AssertPathWithin(developmentRoot, profile.ShutdownRequestPath);
        AssertPathWithin(developmentRoot, profile.CoreExecutablePath);
        AssertPathWithin(developmentRoot, profile.UiExecutablePath);
        Assert.IsFalse(profile.ProfileId.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(profile.ShutdownEventName.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            profile.CoreMaintenanceShutdownEventName.Contains(
                directory.Path,
                StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(profile.PriceCommandPipeName.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(profile.UiInstanceLeaseName.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(profile.UiActivationEventName.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(profile.VersionCheckLifecycleEventName.Contains(
            directory.Path,
            StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(
            profile.CoreMaintenanceShutdownEventName,
            profile.ProfileId);
        StringAssert.Contains(
            profile.CoreMaintenanceShutdownEventName,
            "Development");
        StringAssert.Contains(profile.PriceCommandPipeName, profile.ProfileId);
        StringAssert.Contains(profile.PriceCommandPipeName, "Development");
        StringAssert.Contains(profile.UiInstanceLeaseName, profile.ProfileId);
        StringAssert.Contains(profile.UiInstanceLeaseName, "Development");
        StringAssert.Contains(profile.UiActivationEventName, profile.ProfileId);
        StringAssert.Contains(profile.UiActivationEventName, "Development");
        StringAssert.Contains(
            profile.VersionCheckLifecycleEventName,
            profile.ProfileId);
        StringAssert.Contains(
            profile.VersionCheckLifecycleEventName,
            "Development");
        Assert.IsFalse(profile.SourceLeaseName.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(profile.DatabaseLeaseName.Contains(directory.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CreateStable_UsesStableLocalAppDataAndNeverDevelopmentRoot()
    {
        using var directory = new TestTempDirectory();
        string appRoot = directory.File("installed-app");
        string localAppData = directory.File("local-app-data");
        string userProfile = directory.File("profile");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(userProfile);

        AgenTallyRuntimeProfile profile = AgenTallyRuntimeProfile.CreateStable(
            appRoot,
            localAppData,
            userProfile);

        Assert.AreEqual(AgenTallyChannel.Stable, profile.Channel);
        Assert.AreEqual("AgenTally", profile.DisplayName);
        string stableRoot = Path.Combine(localAppData, "AgenTally", "Stable");
        AssertPathWithin(stableRoot, profile.DataRoot);
        AssertPathWithin(stableRoot, profile.RuntimeRoot);
        AssertPathWithin(stableRoot, profile.LogRoot);
        AssertPathWithin(stableRoot, profile.TempRoot);
        AssertPathWithin(stableRoot, profile.DatabasePath);
        AssertPathWithin(stableRoot, profile.StatusPath);
        AssertPathWithin(stableRoot, profile.ShutdownRequestPath);
        Assert.AreEqual(
            Path.Combine(appRoot, "AgenTally.UI.exe"),
            profile.UiExecutablePath);
        Assert.AreEqual(
            Path.Combine(appRoot, "AgenTally.Core.exe"),
            profile.CoreExecutablePath);
        StringAssert.Contains(
            profile.VersionCheckLifecycleEventName,
            profile.ProfileId);
        StringAssert.Contains(
            profile.VersionCheckLifecycleEventName,
            "Stable");
        Assert.IsFalse(
            profile.DatabasePath.Contains(
                Path.Combine("artifacts", "development"),
                StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CreateStable_DifferentWindowsUsersCannotShareRuntimeState()
    {
        using var directory = new TestTempDirectory();
        string appRootA = directory.File(Path.Combine("user-a", "app"));
        string localAppDataA = directory.File(Path.Combine(
            "user-a",
            "local-app-data"));
        string userProfileA = directory.File(Path.Combine("user-a", "profile"));
        string appRootB = directory.File(Path.Combine("user-b", "app"));
        string localAppDataB = directory.File(Path.Combine(
            "user-b",
            "local-app-data"));
        string userProfileB = directory.File(Path.Combine("user-b", "profile"));
        foreach (string path in new[]
                 {
                     appRootA,
                     localAppDataA,
                     userProfileA,
                     appRootB,
                     localAppDataB,
                     userProfileB
                 })
        {
            Directory.CreateDirectory(path);
        }

        AgenTallyRuntimeProfile userA = AgenTallyRuntimeProfile.CreateStable(
            appRootA,
            localAppDataA,
            userProfileA);
        AgenTallyRuntimeProfile userB = AgenTallyRuntimeProfile.CreateStable(
            appRootB,
            localAppDataB,
            userProfileB);

        Assert.AreNotEqual(userA.ApplicationRoot, userB.ApplicationRoot);
        Assert.AreNotEqual(userA.DataRoot, userB.DataRoot);
        Assert.AreNotEqual(userA.DatabasePath, userB.DatabasePath);
        Assert.AreNotEqual(userA.RuntimeRoot, userB.RuntimeRoot);
        Assert.AreNotEqual(userA.StatusPath, userB.StatusPath);
        Assert.AreNotEqual(userA.ProfileId, userB.ProfileId);
        Assert.AreNotEqual(userA.PriceCommandPipeName, userB.PriceCommandPipeName);
    }

    [TestMethod]
    public void CreateDevelopmentAndStable_UseIndependentRuntimeIdentities()
    {
        using var directory = new TestTempDirectory();
        string repository = directory.File("repository");
        string appRoot = directory.File("installed-app");
        string localAppData = directory.File("local-app-data");
        string userProfile = directory.File("profile");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(userProfile);
        CreateRepositoryMarkers(repository);
        string codexHome = Path.Combine(userProfile, ".codex");
        Directory.CreateDirectory(codexHome);

        AgenTallyRuntimeProfile development =
            AgenTallyRuntimeProfile.CreateDevelopment(repository, codexHome);
        AgenTallyRuntimeProfile stable = AgenTallyRuntimeProfile.CreateStable(
            appRoot,
            localAppData,
            userProfile);

        Assert.AreNotEqual(development.Channel, stable.Channel);
        Assert.AreNotEqual(development.DisplayName, stable.DisplayName);
        Assert.AreNotEqual(development.ApplicationRoot, stable.ApplicationRoot);
        Assert.AreNotEqual(development.DataRoot, stable.DataRoot);
        Assert.AreNotEqual(development.RuntimeRoot, stable.RuntimeRoot);
        Assert.AreNotEqual(development.DatabasePath, stable.DatabasePath);
        Assert.AreNotEqual(development.StatusPath, stable.StatusPath);
        Assert.AreNotEqual(
            development.ShutdownRequestPath,
            stable.ShutdownRequestPath);
        Assert.AreNotEqual(development.ProfileId, stable.ProfileId);
        Assert.AreNotEqual(
            development.ShutdownEventName,
            stable.ShutdownEventName);
        Assert.AreNotEqual(
            development.CoreMaintenanceShutdownEventName,
            stable.CoreMaintenanceShutdownEventName);
        Assert.AreNotEqual(
            development.UiInstanceLeaseName,
            stable.UiInstanceLeaseName);
        Assert.AreNotEqual(
            development.UiActivationEventName,
            stable.UiActivationEventName);
        Assert.AreNotEqual(
            development.PriceCommandPipeName,
            stable.PriceCommandPipeName);
        Assert.AreNotEqual(
            development.DatabaseLeaseName,
            stable.DatabaseLeaseName);
        Assert.AreEqual(
            development.SourceLeaseName,
            stable.SourceLeaseName,
            "Channels that inspect the same read-only source must keep the shared source lease.");
    }

    [TestMethod]
    public void CreateDevelopment_UsesProfileSpecificShutdownRequestPaths()
    {
        using var directory = new TestTempDirectory();
        CreateRepositoryMarkers(directory.Path);
        string realCodexHome = directory.File(Path.Combine("user", ".codex"));
        string syntheticCodexHome = directory.File(Path.Combine(
            "artifacts",
            "development",
            "sources",
            "codex"));
        Directory.CreateDirectory(realCodexHome);
        Directory.CreateDirectory(syntheticCodexHome);

        AgenTallyRuntimeProfile real = AgenTallyRuntimeProfile.CreateDevelopment(
            directory.Path,
            realCodexHome);
        AgenTallyRuntimeProfile synthetic = AgenTallyRuntimeProfile.CreateDevelopment(
            directory.Path,
            syntheticCodexHome);

        Assert.AreEqual(real.RuntimeRoot, synthetic.RuntimeRoot);
        Assert.AreNotEqual(real.ProfileId, synthetic.ProfileId);
        Assert.AreNotEqual(real.ShutdownRequestPath, synthetic.ShutdownRequestPath);
        Assert.AreNotEqual(
            real.PriceCommandPipeName,
            synthetic.PriceCommandPipeName);
        Assert.AreEqual(
            $"application-shutdown-request-{real.ProfileId}.json",
            Path.GetFileName(real.ShutdownRequestPath));
        Assert.AreEqual(
            $"application-shutdown-request-{synthetic.ProfileId}.json",
            Path.GetFileName(synthetic.ShutdownRequestPath));
    }

    [TestMethod]
    public void ResolveDevelopmentCodexHome_UsesProcessOverrideButDefaultsToUserProfile()
    {
        using var directory = new TestTempDirectory();
        CreateRepositoryMarkers(directory.Path);
        string userProfile = directory.File("user");
        string synthetic = directory.File(Path.Combine(
            "artifacts",
            "development",
            "sources",
            "codex"));

        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(userProfile), ".codex"),
            AgenTallyRuntimeProfile.ResolveDevelopmentCodexHome(
                directory.Path,
                userProfile,
                null));
        Assert.AreEqual(
            Path.GetFullPath(synthetic),
            AgenTallyRuntimeProfile.ResolveDevelopmentCodexHome(
                directory.Path,
                userProfile,
                synthetic));
    }

    [TestMethod]
    public void FindRepositoryRoot_RequiresBothMarkersAndFailsClosed()
    {
        using var directory = new TestTempDirectory();
        string repository = directory.File("repo");
        string nested = Path.Combine(repository, "artifacts", "development", "app");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(repository, "AgenTally.sln"), string.Empty);
        File.WriteAllText(Path.Combine(repository, ".agentally-root"), string.Empty);

        Assert.AreEqual(
            Path.GetFullPath(repository),
            AgenTallyRuntimeProfile.FindRepositoryRoot(nested));

        using var missing = new TestTempDirectory();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgenTallyRuntimeProfile.FindRepositoryRoot(missing.Path));
    }

    [TestMethod]
    public async Task StatusStore_WritesTypedPathFreeStatusAtomically()
    {
        using var directory = new TestTempDirectory();
        CreateRepositoryMarkers(directory.Path);
        string codexHome = directory.File("readonly-codex");
        Directory.CreateDirectory(codexHome);
        AgenTallyRuntimeProfile profile =
            AgenTallyRuntimeProfile.CreateDevelopment(directory.Path, codexHome);
        var store = new CoreRuntimeStatusStore(profile);
        var status = new CoreRuntimeStatus(
            CoreRuntimeStatus.CurrentProtocolVersion,
            profile.Channel,
            profile.ProfileId,
            "1.2.3-dev+abc",
            42,
            123456789,
            CoreRuntimePhase.Running,
            CoreRuntimeErrorCode.None,
            "core_running",
            new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
            null);

        await store.WriteAsync(status, CancellationToken.None);
        CoreRuntimeStatus? read = await store.ReadAsync(CancellationToken.None);

        Assert.AreEqual(status, read);
        Assert.IsTrue(File.Exists(profile.StatusPath));
        Assert.IsFalse(File.Exists(profile.StatusPath + ".tmp"));
        string json = await File.ReadAllTextAsync(profile.StatusPath);
        Assert.DoesNotContain(directory.Path, json, StringComparison.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("running", document.RootElement.GetProperty("phase").GetString());
    }

    [TestMethod]
    public async Task StatusStore_RejectsMismatchedProfileBeforeWriting()
    {
        using var directory = new TestTempDirectory();
        CreateRepositoryMarkers(directory.Path);
        string codexHome = directory.File("readonly-codex");
        Directory.CreateDirectory(codexHome);
        AgenTallyRuntimeProfile profile =
            AgenTallyRuntimeProfile.CreateDevelopment(directory.Path, codexHome);
        var store = new CoreRuntimeStatusStore(profile);
        var status = new CoreRuntimeStatus(
            CoreRuntimeStatus.CurrentProtocolVersion,
            profile.Channel,
            "wrong-profile",
            "1.0.0",
            1,
            2,
            CoreRuntimePhase.Failed,
            CoreRuntimeErrorCode.UnexpectedFailure,
            "core_failed",
            DateTimeOffset.UtcNow,
            1);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.WriteAsync(status, CancellationToken.None));
        Assert.IsFalse(File.Exists(profile.StatusPath));
    }

    [TestMethod]
    public async Task StatusStore_RetriesBoundedAtomicReplaceWhileReaderBlocksDelete()
    {
        using var directory = new TestTempDirectory();
        CreateRepositoryMarkers(directory.Path);
        string codexHome = directory.File("readonly-codex");
        Directory.CreateDirectory(codexHome);
        AgenTallyRuntimeProfile profile =
            AgenTallyRuntimeProfile.CreateDevelopment(directory.Path, codexHome);
        var store = new CoreRuntimeStatusStore(profile);
        CoreRuntimeStatus starting = CreateStatus(
            profile,
            CoreRuntimePhase.Starting,
            "core_starting");
        CoreRuntimeStatus running = CreateStatus(
            profile,
            CoreRuntimePhase.Running,
            "core_running");
        await store.WriteAsync(starting, CancellationToken.None);

        Task pendingWrite;
        using (var reader = new FileStream(
                   profile.StatusPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            pendingWrite = store.WriteAsync(running, CancellationToken.None);
            await Task.Delay(150);
            Assert.IsFalse(pendingWrite.IsCompleted);
        }

        await pendingWrite;
        Assert.AreEqual(running, await store.ReadAsync(CancellationToken.None));
    }

    private static void CreateRepositoryMarkers(string path)
    {
        File.WriteAllText(Path.Combine(path, "AgenTally.sln"), string.Empty);
        File.WriteAllText(Path.Combine(path, ".agentally-root"), string.Empty);
    }

    private static CoreRuntimeStatus CreateStatus(
        AgenTallyRuntimeProfile profile,
        CoreRuntimePhase phase,
        string messageCode) => new(
            CoreRuntimeStatus.CurrentProtocolVersion,
            profile.Channel,
            profile.ProfileId,
            "1.0.0-dev",
            42,
            123456789,
            phase,
            CoreRuntimeErrorCode.None,
            messageCode,
            new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
            null);

    private static void AssertPathWithin(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string normalizedCandidate = Path.GetFullPath(candidate);
        Assert.IsTrue(
            normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase),
            $"Expected {normalizedCandidate} below {normalizedRoot}.");
    }
}
