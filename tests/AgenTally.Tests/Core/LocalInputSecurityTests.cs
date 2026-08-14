using System.IO;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.GeminiCli;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Core.Collectors.KimiCode;
using AgenTally.Core.Collectors.OpenCode;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
[DoNotParallelize]
public sealed class LocalInputSecurityTests
{
    [TestMethod]
    public async Task BoundedFileReader_AcceptsLimitAndRejectsOverflow()
    {
        using var directory = new TestTempDirectory();
        string acceptedPath = directory.File("accepted.bin");
        string rejectedPath = directory.File("rejected.bin");
        byte[] accepted = Enumerable.Range(0, 32)
            .Select(static value => checked((byte)value))
            .ToArray();
        await File.WriteAllBytesAsync(acceptedPath, accepted);
        await File.WriteAllBytesAsync(rejectedPath, new byte[33]);

        CollectionAssert.AreEqual(
            accepted,
            BoundedFileReader.ReadAllBytes(acceptedPath, 32));
        CollectionAssert.AreEqual(
            accepted,
            await BoundedFileReader.ReadAllBytesAsync(
                acceptedPath,
                32,
                CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            BoundedFileReader.ReadAllBytes(rejectedPath, 32));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            BoundedFileReader.ReadAllBytesAsync(
                rejectedPath,
                32,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task BoundedUtf8LineReader_DiscardsOversizedLineAndContinues()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("source.jsonl");
        await File.WriteAllTextAsync(
            path,
            $"first\r\n{new string('x', 17)}\nlast",
            new UTF8Encoding(false));

        IReadOnlyList<BoundedTextLine> lines = await ReadLinesAsync(
            path,
            maximumLineCharacters: 16,
            maximumSourceBytes: 1024);

        Assert.HasCount(3, lines);
        Assert.AreEqual(new BoundedTextLine("first", false), lines[0]);
        Assert.AreEqual(new BoundedTextLine(string.Empty, true), lines[1]);
        Assert.AreEqual(new BoundedTextLine("last", false), lines[2]);
    }

    [TestMethod]
    public async Task RuntimeJsonStores_RejectOversizedPayloadsBeforeDeserialization()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateStableProfile(directory);
        Directory.CreateDirectory(profile.DataRoot);
        Directory.CreateDirectory(profile.RuntimeRoot);
        await File.WriteAllBytesAsync(
            profile.DataManagementStatePath,
            new byte[(4 * 1024) + 1]);
        await File.WriteAllBytesAsync(
            profile.StatusPath,
            new byte[(16 * 1024) + 1]);
        await File.WriteAllBytesAsync(
            profile.DataMaintenanceRequestPath,
            new byte[(64 * 1024) + 1]);

        Assert.IsNull(
            new JsonDataManagementStateStore(profile)
                .ReadLastSuccessfulBackupUtc());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new CoreRuntimeStatusStore(profile).ReadAsync(
                CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new DataMaintenanceRequestStore(profile).ReadAsync(
                DataMaintenanceOperation.CreateBackup,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ShutdownSignal_OversizedMarkerCannotAuthorizeProfileExit()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateStableProfile(directory);
        Directory.CreateDirectory(profile.RuntimeRoot);
        using var signal = new ApplicationShutdownSignal(profile);
        using (var rejectedTimeout = new CancellationTokenSource(
                   TimeSpan.FromMilliseconds(350)))
        {
            Task rejectedWait = signal.WaitAsync(rejectedTimeout.Token);
            await File.WriteAllBytesAsync(
                profile.ShutdownRequestPath,
                new byte[(4 * 1024) + 1]);
            Assert.IsTrue(ApplicationShutdownSignal.TryRequest(
                profile.ShutdownEventName));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                await rejectedWait);
        }

        using var acceptedTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        Task acceptedWait = signal.WaitAsync(acceptedTimeout.Token);
        ApplicationShutdownRequestResult result =
            ApplicationShutdownSignal.Request(profile);

        Assert.IsTrue(result.RequestAccepted);
        await acceptedWait;
    }

    [TestMethod]
    public async Task KimiCode_OversizedStateFileFailsClosed()
    {
        using var directory = new TestTempDirectory();
        string kimiHome = directory.File("kimi-home");
        string sessionDirectory = Path.Combine(
            kimiHome,
            "sessions",
            "workspace",
            "session_root");
        string wirePath = Path.Combine(
            sessionDirectory,
            "agents",
            "main",
            "wire.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(wirePath)!);
        await File.WriteAllBytesAsync(
            Path.Combine(sessionDirectory, "state.json"),
            new byte[(2 * 1024 * 1024) + 1]);

        KimiCodeEntityMetadataResult result =
            await new KimiCodeEntityMetadataReader().ReadAsync(
                kimiHome,
                wirePath,
                sourceEntityId: "source",
                CancellationToken.None);

        Assert.IsNull(result.Metadata);
        Assert.AreEqual(
            "kimi_code.invalid_session_state",
            result.Diagnostic?.Code);
    }

    [TestMethod]
    public async Task GeminiCli_OversizedJsonlLineIsSkippedWithoutLosingLaterRecord()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("session.jsonl");
        string valid = JsonSerializer.Serialize(new
        {
            type = "gemini",
            id = "valid",
            session_id = "session",
            timestamp = "2026-08-12T00:00:00Z",
            model = "gemini-test",
            tokens = new
            {
                promptTokenCount = 10,
                candidatesTokenCount = 2,
                totalTokenCount = 12
            }
        });
        await File.WriteAllTextAsync(
            path,
            new string('x', (4 * 1024 * 1024) + 1) + "\n" + valid,
            new UTF8Encoding(false));

        GeminiCliParseResult result = await GeminiCliParser.ParseAsync(
            path,
            CancellationToken.None);

        Assert.ContainsSingle(result.Records);
        Assert.ContainsSingle(result.Diagnostics);
        Assert.AreEqual("valid", result.Records[0].StableId);
    }

    [TestMethod]
    public async Task CodexSessionIndex_OversizedLineCannotHideLaterValidName()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File("codex-home");
        Directory.CreateDirectory(codexHome);
        string valid = JsonSerializer.Serialize(new
        {
            id = "session-valid",
            thread_name = "安全名称",
            updated_at = "2026-08-12T00:00:00Z"
        });
        await File.WriteAllTextAsync(
            Path.Combine(codexHome, "session_index.jsonl"),
            new string('x', (64 * 1024) + 1) + "\n" + valid,
            new UTF8Encoding(false));

        using var source = new CodexSessionNameSource(codexHome);
        IReadOnlyList<UsageSessionNameMetadata> names =
            await source.ReadSessionNamesAsync(CancellationToken.None);

        UsageSessionNameMetadata name = Assert.ContainsSingle(names);
        Assert.AreEqual("session-valid", name.SessionId);
        Assert.AreEqual("安全名称", name.SessionName);
    }

    [TestMethod]
    public async Task OpenCode_OversizedLegacyFileAndDatabaseCellFailClosed()
    {
        using var directory = new TestTempDirectory();
        string legacy = directory.File("message.json");
        await File.WriteAllBytesAsync(
            legacy,
            new byte[OpenCodeParser.MaxLegacyFileBytes + 1]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await OpenCodeParser.ParseAsync(
                legacy,
                offset: 0,
                limit: 100,
                CancellationToken.None));

        string database = directory.File("opencode.db");
        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = database,
                             Pooling = false
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE message (id TEXT, session_id TEXT, data TEXT);
                INSERT INTO message (id, session_id, data)
                VALUES ('row', 'session', $payload);
                """;
            command.Parameters.AddWithValue(
                "$payload",
                new string('x', OpenCodeParser.MaxPayloadCharacters + 1));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await OpenCodeParser.ParseAsync(
                database,
                offset: 0,
                limit: 100,
                CancellationToken.None));
    }

    private static async Task<IReadOnlyList<BoundedTextLine>> ReadLinesAsync(
        string path,
        int maximumLineCharacters,
        long maximumSourceBytes)
    {
        var lines = new List<BoundedTextLine>();
        await foreach (BoundedTextLine line in
            BoundedUtf8LineReader.ReadLinesAsync(
                path,
                maximumLineCharacters,
                maximumSourceBytes,
                CancellationToken.None))
        {
            lines.Add(line);
        }
        return lines;
    }

    private static AgenTallyRuntimeProfile CreateStableProfile(
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
