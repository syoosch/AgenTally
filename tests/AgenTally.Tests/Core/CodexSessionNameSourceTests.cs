using System.IO;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Domain.Usage;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CodexSessionNameSourceTests
{
    [TestMethod]
    public async Task ReadSessionNamesAsync_ReadsOnlyTitleMetadataAndNormalizesIt()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File("codex-home");
        Directory.CreateDirectory(codexHome);
        string databasePath = Path.Combine(codexHome, "state_5.sqlite");
        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = databasePath,
                             Pooling = false
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE threads (
                    id TEXT NOT NULL PRIMARY KEY,
                    title TEXT NULL,
                    updated_at_ms INTEGER NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                INSERT INTO threads (id, title, updated_at_ms)
                VALUES
                    ('session-a', $title, 1785499200000),
                    ('session-b', NULL, 1785499260000);
                """;
            command.Parameters.AddWithValue(
                "$title",
                $"  设计{Environment.NewLine}会话{'\u202E'}  ");
            await command.ExecuteNonQueryAsync();
        }

        using var source = new CodexSessionNameSource(codexHome);
        IReadOnlyList<UsageSessionNameMetadata> names =
            await source.ReadSessionNamesAsync(CancellationToken.None);

        Assert.HasCount(2, names);
        UsageSessionNameMetadata first = names[0];
        Assert.AreEqual("session-a", first.SessionId);
        Assert.AreEqual("设计 会话", first.SessionName);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(1785499200000),
            first.UpdatedAtUtc);
        Assert.IsNull(names[1].SessionName);
    }

    [TestMethod]
    public async Task ReadSessionNamesAsync_PrefersLatestIndexedNameThenDatabaseFallbacks()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File("codex-home");
        Directory.CreateDirectory(codexHome);
        string databasePath = Path.Combine(codexHome, "state_5.sqlite");
        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = databasePath,
                             Pooling = false
                         }.ToString()))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE threads (
                    id TEXT NOT NULL PRIMARY KEY,
                    title TEXT NULL,
                    name TEXT NULL,
                    updated_at_ms INTEGER NULL
                );
                INSERT INTO threads (id, title, name, updated_at_ms)
                VALUES
                    ('session-a', '首轮输入开头', 'SQLite 显式名称', 1785499200000),
                    ('session-b', '标题 B', 'SQLite 名称 B', 1785499260000),
                    ('session-c', '标题 C', NULL, 1785499320000);
                """;
            await command.ExecuteNonQueryAsync();
        }

        string sessionIndexPath = Path.Combine(
            codexHome,
            "session_index.jsonl");
        await File.WriteAllTextAsync(
            sessionIndexPath,
            string.Join(
                Environment.NewLine,
                """
                {"id":"session-a","thread_name":"旧名称","updated_at":"2026-07-31T08:00:00Z"}
                """,
                "{not-json",
                """
                {"id":"session-a","thread_name":"客户端\n概括名称","updated_at":"2026-07-31T08:05:00Z"}
                """,
                """
                {"id":"session-index-only","thread_name":"仅索引名称","updated_at":"2026-07-31T08:06:00Z"}
                """) + Environment.NewLine);

        using var source = new CodexSessionNameSource(codexHome);
        IReadOnlyList<UsageSessionNameMetadata> firstRead =
            await source.ReadSessionNamesAsync(CancellationToken.None);

        Assert.HasCount(4, firstRead);
        Assert.AreEqual(
            "客户端 概括名称",
            firstRead.Single(value => value.SessionId == "session-a").SessionName);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(1785499200000),
            firstRead.Single(value => value.SessionId == "session-a").UpdatedAtUtc);
        Assert.AreEqual(
            "SQLite 名称 B",
            firstRead.Single(value => value.SessionId == "session-b").SessionName);
        Assert.AreEqual(
            "标题 C",
            firstRead.Single(value => value.SessionId == "session-c").SessionName);
        Assert.AreEqual(
            "仅索引名称",
            firstRead.Single(
                value => value.SessionId == "session-index-only").SessionName);

        await File.AppendAllTextAsync(
            sessionIndexPath,
            """
            {"id":"session-a","thread_name":"用户手动改名","updated_at":"2026-07-31T08:10:00Z"}

            """);

        IReadOnlyList<UsageSessionNameMetadata> secondRead =
            await source.ReadSessionNamesAsync(CancellationToken.None);
        UsageSessionNameMetadata renamed =
            secondRead.Single(value => value.SessionId == "session-a");
        Assert.AreEqual("用户手动改名", renamed.SessionName);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(1785499200000),
            renamed.UpdatedAtUtc);
    }

    [TestMethod]
    public void NormalizeName_LimitsUnicodeScalarsWithoutSplittingSurrogates()
    {
        string value = string.Concat(Enumerable.Repeat("😀", 125));
        string spaced = string.Join(
            ' ',
            Enumerable.Repeat("单词", 125));

        string normalized = CodexSessionNameSource.NormalizeName(value)!;
        string normalizedSpaced =
            CodexSessionNameSource.NormalizeName(spaced)!;

        Assert.AreEqual(
            CodexSessionNameSource.MaximumNameLength,
            normalized.EnumerateRunes().Count());
        Assert.IsTrue(normalized.EndsWith("😀", StringComparison.Ordinal));
        Assert.IsLessThanOrEqualTo(
            CodexSessionNameSource.MaximumNameLength,
            normalizedSpaced.EnumerateRunes().Count());
        Assert.IsFalse(
            normalizedSpaced.EndsWith(' '),
            "The scalar limit must not leave a collapsed trailing space.");
    }
}
