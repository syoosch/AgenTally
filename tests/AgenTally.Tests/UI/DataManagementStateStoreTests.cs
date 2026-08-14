using System.IO;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class DataManagementStateStoreTests
{
    [TestMethod]
    public void LastSuccessfulBackup_RoundTripsInProfileScopedDataFile()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        var store = new JsonDataManagementStateStore(profile);
        DateTimeOffset value =
            new(2026, 8, 11, 5, 6, 7, TimeSpan.Zero);

        bool written = store.TryWriteLastSuccessfulBackupUtc(value);

        Assert.IsTrue(written);
        Assert.AreEqual(value, store.ReadLastSuccessfulBackupUtc());
        Assert.IsTrue(File.Exists(profile.DataManagementStatePath));
        Assert.AreNotEqual(
            profile.UiPreferencesPath,
            profile.DataManagementStatePath);
    }

    [TestMethod]
    public async Task InvalidState_FailsClosedAsUnknownInsteadOfInventingBackupTime()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        Directory.CreateDirectory(profile.DataRoot);
        await File.WriteAllTextAsync(
            profile.DataManagementStatePath,
            "{\"schemaVersion\":999,\"lastSuccessfulBackupUtc\":\"2030-01-01T00:00:00Z\"}");
        var store = new JsonDataManagementStateStore(profile);

        Assert.IsNull(store.ReadLastSuccessfulBackupUtc());
    }

    private static AgenTallyRuntimeProfile CreateProfile(
        TestTempDirectory directory)
    {
        string app = directory.File("app");
        string local = directory.File("local");
        string user = directory.File("user");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(user);
        return AgenTallyRuntimeProfile.CreateStable(app, local, user);
    }
}
