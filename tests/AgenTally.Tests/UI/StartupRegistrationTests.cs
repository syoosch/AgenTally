using System.IO;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class StartupRegistrationTests
{
    [TestMethod]
    public void DefaultState_IsDisabledAndPerformsNoWrite()
    {
        var backend = new FakeBackend();
        var store = CreateStore(backend);

        StartupRegistrationStatus status = store.Read();

        Assert.AreEqual(StartupRegistrationState.Disabled, status.State);
        Assert.AreEqual(0, backend.WriteCount);
        Assert.AreEqual(0, backend.DeleteCount);
    }

    [TestMethod]
    public void FirstEnable_WritesOneExactQuotedBackgroundCommand()
    {
        var backend = new FakeBackend();
        var store = CreateStore(backend);

        StartupRegistrationStatus status = store.SetEnabled(enabled: true);

        Assert.AreEqual(StartupRegistrationState.Enabled, status.State);
        Assert.AreEqual(1, backend.WriteCount);
        Assert.AreEqual(ExpectedCommand, backend.Entry.Command);
        Assert.IsTrue(backend.Entry.Exists);
    }

    [TestMethod]
    public void RepeatedEnableAndReopen_PreserveOneOwnedEntry()
    {
        var backend = new FakeBackend();
        var first = CreateStore(backend);
        Assert.AreEqual(
            StartupRegistrationState.Enabled,
            first.SetEnabled(enabled: true).State);

        var reopenedAfterUpgrade = CreateStore(backend);
        Assert.AreEqual(
            StartupRegistrationState.Enabled,
            reopenedAfterUpgrade.Read().State);
        Assert.AreEqual(
            StartupRegistrationState.Enabled,
            reopenedAfterUpgrade.SetEnabled(enabled: true).State);
        Assert.AreEqual(1, backend.WriteCount);
        Assert.AreEqual(0, backend.DeleteCount);
    }

    [TestMethod]
    public void Disable_IsIdempotentAndDeletesOnlyTheExactOwnedEntry()
    {
        var backend = new FakeBackend
        {
            Entry = new StartupRegistrationEntry(true, ExpectedCommand)
        };
        var store = CreateStore(backend);

        Assert.AreEqual(
            StartupRegistrationState.Disabled,
            store.SetEnabled(enabled: false).State);
        Assert.AreEqual(
            StartupRegistrationState.Disabled,
            store.SetEnabled(enabled: false).State);
        Assert.AreEqual(0, backend.WriteCount);
        Assert.AreEqual(1, backend.DeleteCount);
        Assert.IsFalse(backend.Entry.Exists);
    }

    [TestMethod]
    public void ConflictingSameNameEntry_IsNeverOverwrittenOrDeleted()
    {
        var backend = new FakeBackend
        {
            Entry = new StartupRegistrationEntry(
                true,
                "\"C:\\Other\\Other.exe\" --background")
        };
        var store = CreateStore(backend);

        StartupRegistrationStatus read = store.Read();
        StartupRegistrationStatus enable = store.SetEnabled(enabled: true);
        StartupRegistrationStatus disable = store.SetEnabled(enabled: false);

        Assert.AreEqual(StartupRegistrationState.Conflict, read.State);
        Assert.AreEqual(StartupRegistrationState.Conflict, enable.State);
        Assert.AreEqual(StartupRegistrationState.Conflict, disable.State);
        StringAssert.Contains(read.Message, "不会覆盖或删除");
        Assert.AreEqual(0, backend.WriteCount);
        Assert.AreEqual(0, backend.DeleteCount);
    }

    [TestMethod]
    public void BackendExceptions_AreReportedWithoutClaimingSuccess()
    {
        var readFailure = new FakeBackend { ThrowOnRead = true };
        StartupRegistrationStatus read = CreateStore(readFailure).Read();
        Assert.AreEqual(StartupRegistrationState.Unavailable, read.State);
        Assert.IsFalse(read.IsEnabled);

        var writeFailure = new FakeBackend { ThrowOnWrite = true };
        StartupRegistrationStatus write = CreateStore(writeFailure)
            .SetEnabled(enabled: true);
        Assert.AreEqual(StartupRegistrationState.Unavailable, write.State);
        Assert.IsFalse(write.IsEnabled);
        Assert.AreEqual(1, writeFailure.WriteCount);
        Assert.IsFalse(writeFailure.Entry.Exists);
    }

    [TestMethod]
    public void DevelopmentComposition_PersistsOnlyInOwnedSimulationFile()
    {
        using var directory = new TestTempDirectory();
        string repository = directory.File("repo");
        Directory.CreateDirectory(repository);
        File.WriteAllText(Path.Combine(repository, "AgenTally.sln"), string.Empty);
        File.WriteAllText(Path.Combine(repository, ".agentally-root"), string.Empty);
        string codexHome = Path.Combine(repository, "readonly-codex");
        Directory.CreateDirectory(codexHome);
        AgenTallyRuntimeProfile profile =
            AgenTallyRuntimeProfile.CreateDevelopment(repository, codexHome);
        IStartupRegistrationStore first =
            StartupRegistrationProductionComposition.Create(profile);

        Assert.AreEqual(
            StartupRegistrationState.Disabled,
            first.Read().State);
        Assert.AreEqual(
            StartupRegistrationState.Enabled,
            first.SetEnabled(enabled: true).State);
        Assert.IsTrue(File.Exists(profile.StartupRegistrationStatePath));
        Assert.IsTrue(profile.IsDevelopmentOwnedPath(
            profile.StartupRegistrationStatePath));

        IStartupRegistrationStore reopened =
            StartupRegistrationProductionComposition.Create(profile);
        Assert.AreEqual(
            StartupRegistrationState.Enabled,
            reopened.Read().State);
        Assert.AreEqual(
            StartupRegistrationState.Disabled,
            reopened.SetEnabled(enabled: false).State);
        Assert.IsFalse(File.Exists(profile.StartupRegistrationStatePath));
    }

    private static readonly string ExecutablePath = Path.GetFullPath(
        @"C:\Program Files\AgenTally\AgenTally.UI.exe");

    private static readonly string ExpectedCommand =
        StartupRegistrationCommand.Create(ExecutablePath);

    private static ExactStartupRegistrationStore CreateStore(
        FakeBackend backend) => new(ExpectedCommand, backend);

    private sealed class FakeBackend : IStartupRegistrationBackend
    {
        public StartupRegistrationEntry Entry { get; set; }

        public bool ThrowOnRead { get; set; }

        public bool ThrowOnWrite { get; set; }

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public StartupRegistrationEntry Read()
        {
            if (ThrowOnRead)
            {
                throw new IOException("Synthetic read failure.");
            }

            return Entry;
        }

        public void Write(string command)
        {
            WriteCount++;
            if (ThrowOnWrite)
            {
                throw new UnauthorizedAccessException(
                    "Synthetic write failure.");
            }

            Entry = new StartupRegistrationEntry(true, command);
        }

        public void Delete()
        {
            DeleteCount++;
            Entry = new StartupRegistrationEntry(false, null);
        }
    }
}
