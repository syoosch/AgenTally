using System.IO;
using System.Text;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class IncrementalJsonlReaderTests
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    [TestMethod]
    public async Task ReadBatchAsync_ReadsFiveHundredLinesInBoundedBatches()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("large.jsonl");
        string contents = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"{{\"id\":{i}}}")) + "\n";
        await File.WriteAllTextAsync(path, contents, Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlCursor cursor = JsonlCursor.Start;
        var batchSizes = new List<int>();
        var lineNumbers = new List<long>();
        bool endOfFile;

        do
        {
            JsonlReadBatch batch = await reader.ReadBatchAsync(
                path,
                cursor,
                maxLines: 200,
                CancellationToken.None);

            batchSizes.Add(batch.Lines.Count);
            lineNumbers.AddRange(batch.Lines.Select(line => line.LineNumber));
            Assert.IsTrue(batch.Lines.Count <= 200);
            Assert.IsTrue(batch.MaxBufferBytes <= 64 * 1024);
            cursor = batch.NextCursor;
            endOfFile = batch.EndOfFile;
        }
        while (!endOfFile);

        CollectionAssert.AreEqual(new[] { 200, 200, 100 }, batchSizes);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 500).Select(number => (long)number).ToArray(),
            lineNumbers);
        Assert.AreEqual(500L, cursor.LineNumber);
        Assert.IsFalse(string.IsNullOrWhiteSpace(cursor.SourceFingerprint));
    }

    [TestMethod]
    public async Task ReadBatchAsync_PreservesUtf8CharacterSplitAcrossReadBuffers()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("utf8.jsonl");
        string longLine = new string('a', 65_532) + "中-end";
        await File.WriteAllTextAsync(path, $"{{}}\n{longLine}\n", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 10,
            CancellationToken.None);

        Assert.HasCount(2, batch.Lines);
        Assert.AreEqual(longLine, Encoding.UTF8.GetString(batch.Lines[1].Utf8));
        Assert.IsTrue(batch.EndOfFile);
    }

    [TestMethod]
    public async Task ReadBatchAsync_RetainsIncompleteTailUntilNewlineArrives()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("partial.jsonl");
        await File.WriteAllTextAsync(path, "first\npart", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch first = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.HasCount(1, first.Lines);
        Assert.AreEqual("first", Encoding.UTF8.GetString(first.Lines[0].Utf8));
        Assert.AreEqual("part", Encoding.UTF8.GetString(first.NextCursor.PendingBytes));
        Assert.IsTrue(first.EndOfFile);

        await File.AppendAllTextAsync(path, "ial\n", Utf8WithoutBom);

        JsonlReadBatch completed = await reader.ReadBatchAsync(
            path,
            first.NextCursor,
            maxLines: 200,
            CancellationToken.None);

        JsonlLine line = Assert.ContainsSingle(completed.Lines);
        Assert.AreEqual("partial", Encoding.UTF8.GetString(line.Utf8));
        Assert.AreEqual(2L, line.LineNumber);
        Assert.IsEmpty(completed.NextCursor.PendingBytes);
    }

    [TestMethod]
    public async Task ReadBatchAsync_CompletesMaximumLengthBodyWithTrailingCarriageReturn()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("maximum-body-with-cr.jsonl");
        string maximumBody = new('x', JsonlCursor.MaxLogicalLineBytes);
        await File.WriteAllTextAsync(
            path,
            $"fingerprint\n{maximumBody}\r",
            Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch partial = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.AreEqual("fingerprint", Encoding.UTF8.GetString(Assert.ContainsSingle(partial.Lines).Utf8));
        Assert.AreEqual(JsonlCursor.MaxPendingBytes, partial.NextCursor.PendingBytes.Length);
        Assert.AreEqual((byte)'\r', partial.NextCursor.PendingBytes[^1]);

        await File.AppendAllTextAsync(path, "\n", Utf8WithoutBom);

        JsonlReadBatch completed = await reader.ReadBatchAsync(
            path,
            partial.NextCursor,
            maxLines: 200,
            CancellationToken.None);

        JsonlLine completedLine = Assert.ContainsSingle(completed.Lines);
        Assert.AreEqual(JsonlCursor.MaxLogicalLineBytes, completedLine.Utf8.Length);
        Assert.AreEqual((byte)'x', completedLine.Utf8[0]);
        Assert.AreEqual((byte)'x', completedLine.Utf8[^1]);
        Assert.AreEqual(2L, completedLine.LineNumber);
        Assert.IsEmpty(completed.NextCursor.PendingBytes);
        Assert.IsNull(completed.Diagnostic);
        Assert.IsTrue(completed.EndOfFile);
    }

    [TestMethod]
    public async Task ReadBatchAsync_DoesNotCommitCursorWithoutACompleteFirstLine()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("only-partial.jsonl");
        await File.WriteAllTextAsync(path, "not-complete", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.IsEmpty(batch.Lines);
        Assert.AreEqual(JsonlCursor.Start, batch.NextCursor);
        Assert.IsNull(batch.Diagnostic);
        Assert.IsTrue(batch.EndOfFile);
    }

    [TestMethod]
    public async Task ReadBatchAsync_EmptyFileReturnsStartAtEndOfFile()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("empty.jsonl");
        await File.WriteAllBytesAsync(path, []);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.IsEmpty(batch.Lines);
        Assert.AreEqual(JsonlCursor.Start, batch.NextCursor);
        Assert.IsNull(batch.Diagnostic);
        Assert.IsTrue(batch.EndOfFile);
    }

    [TestMethod]
    public async Task ReadBatchAsync_RejectsFirstLineOverFingerprintLimitWithoutCommittingCursor()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("oversized-first-line.jsonl");
        await File.WriteAllTextAsync(path, new string('x', (64 * 1024) + 1) + "\n", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.IsEmpty(batch.Lines);
        Assert.AreEqual(JsonlCursor.Start, batch.NextCursor);
        Assert.IsNotNull(batch.Diagnostic);
        Assert.AreEqual("jsonl.first_line_too_long", batch.Diagnostic.Code);
        Assert.AreEqual("JSONL 首行超过 64 KiB 指纹上限，未提交读取游标。", batch.Diagnostic.Message);
        Assert.IsTrue(batch.MaxBufferBytes <= 64 * 1024);
    }

    [TestMethod]
    public async Task ReadBatchAsync_AcceptsExactlySixtyFourKiBFirstLineWithCrLf()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("exact-first-line.jsonl");
        await File.WriteAllTextAsync(
            path,
            new string('x', 64 * 1024) + "\r\n",
            Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        JsonlLine line = Assert.ContainsSingle(batch.Lines);
        Assert.AreEqual(64 * 1024, line.Utf8.Length);
        Assert.IsTrue(batch.EndOfFile);
        Assert.IsNull(batch.Diagnostic);
    }

    [TestMethod]
    public async Task ReadBatchAsync_SkipsCompleteOversizedLaterLineAndAdvancesCursor()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("oversized-complete-line.jsonl");
        await File.WriteAllTextAsync(
            path,
            "first\n" + new string('x', JsonlCursor.MaxLogicalLineBytes + 1) + "\nlast\n",
            Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "first", "last" },
            batch.Lines.Select(line => Encoding.UTF8.GetString(line.Utf8)).ToArray());
        Assert.AreEqual("jsonl.line_too_long", batch.Diagnostic?.Code);
        Assert.AreEqual("JSONL 行超过 8 MiB 上限，已跳过该完整行。", batch.Diagnostic?.Message);
        Assert.AreEqual(3L, batch.NextCursor.LineNumber);
        Assert.IsEmpty(batch.NextCursor.PendingBytes);
        Assert.IsTrue(batch.EndOfFile);
    }

    [TestMethod]
    public async Task ReadBatchAsync_BoundsReturnedPayloadAcrossLargeLines()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("bounded-payload.jsonl");
        string largeLine = new('x', 5 * 1024 * 1024);
        await File.WriteAllTextAsync(
            path,
            $"first\n{largeLine}\n{largeLine}\n",
            Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch first = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.HasCount(2, first.Lines);
        Assert.IsLessThanOrEqualTo(
            8 * 1024 * 1024,
            first.Lines.Sum(line => line.Utf8.Length));
        Assert.IsFalse(first.EndOfFile);

        JsonlReadBatch second = await reader.ReadBatchAsync(
            path,
            first.NextCursor,
            maxLines: 200,
            CancellationToken.None);

        Assert.AreEqual(5 * 1024 * 1024, Assert.ContainsSingle(second.Lines).Utf8.Length);
        Assert.IsTrue(second.EndOfFile);
    }

    [TestMethod]
    public async Task ReadBatchAsync_DoesNotAdvanceCursorForOversizedIncompleteTail()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("oversized-incomplete-line.jsonl");
        await File.WriteAllTextAsync(
            path,
            "first\n" + new string('x', JsonlCursor.MaxLogicalLineBytes + 1),
            Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.IsEmpty(batch.Lines);
        Assert.AreEqual(0L, batch.NextCursor.ByteOffset);
        Assert.AreEqual(0L, batch.NextCursor.LineNumber);
        Assert.IsFalse(string.IsNullOrWhiteSpace(batch.NextCursor.SourceFingerprint));
        Assert.AreEqual("jsonl.incomplete_line_too_long", batch.Diagnostic?.Code);
        Assert.AreEqual(
            "JSONL 未完成行超过 8 MiB 上限，读取游标未推进。",
            batch.Diagnostic?.Message);
        Assert.IsTrue(batch.EndOfFile);
    }

    [TestMethod]
    public void DeserializeOrStart_ReturnsFixedDiagnosticForMalformedCursor()
    {
        JsonlCursor cursor = JsonlCursor.DeserializeOrStart(
            "{not-json}",
            out var diagnostic);

        Assert.AreEqual(JsonlCursor.Start, cursor);
        Assert.IsNotNull(diagnostic);
        Assert.AreEqual("jsonl.invalid_cursor", diagnostic.Code);
        Assert.AreEqual("JSONL 读取游标无效，已从头重新读取。", diagnostic.Message);
        Assert.DoesNotContain("not-json", diagnostic.Message);
    }

    [TestMethod]
    public void DeserializeOrStart_RejectsOversizedCursorJsonBeforeDeserializing()
    {
        string oversizedCursorJson = new('x', JsonlCursor.MaxSerializedCursorCharacters + 1);

        JsonlCursor cursor = JsonlCursor.DeserializeOrStart(
            oversizedCursorJson,
            out CollectorDiagnostic? diagnostic);

        Assert.AreEqual(JsonlCursor.Start, cursor);
        Assert.AreEqual("jsonl.invalid_cursor", diagnostic?.Code);
    }

    [TestMethod]
    [DataRow(0L, 1L)]
    [DataRow(1L, 2L)]
    public void DeserializeOrStart_RejectsImpossibleOffsetAndLineNumber(
        long byteOffset,
        long lineNumber)
    {
        string cursorJson = $$"""
            {"byteOffset":{{byteOffset}},"pendingBase64":"","lineNumber":{{lineNumber}},"sourceFingerprint":"fixture"}
            """;

        JsonlCursor cursor = JsonlCursor.DeserializeOrStart(
            cursorJson,
            out CollectorDiagnostic? diagnostic);

        Assert.AreEqual(JsonlCursor.Start, cursor);
        Assert.AreEqual("jsonl.invalid_cursor", diagnostic?.Code);
    }

    [TestMethod]
    public async Task ReadBatchAsync_RejectsOversizedBase64BeforeDecoding()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("oversized-base64.jsonl");
        await File.WriteAllTextAsync(path, "valid\n", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();
        var oversizedCursor = new JsonlCursor(
            JsonlCursor.MaxPendingBytes + 1L,
            new string('A', JsonlCursor.MaxPendingBase64Characters + 1),
            1,
            new string('a', 64));

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            oversizedCursor,
            maxLines: 200,
            CancellationToken.None);

        Assert.AreEqual("jsonl.invalid_cursor", batch.Diagnostic?.Code);
        Assert.AreEqual("valid", Encoding.UTF8.GetString(Assert.ContainsSingle(batch.Lines).Utf8));
    }

    [TestMethod]
    public async Task ReadBatchAsync_DefensivelyResetsDirectlyConstructedMalformedCursor()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("bad-cursor.jsonl");
        await File.WriteAllTextAsync(path, "valid\n", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();
        var malformed = new JsonlCursor(
            -1,
            "not-base64",
            -1,
            string.Empty);

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            malformed,
            maxLines: 200,
            CancellationToken.None);

        Assert.AreEqual("jsonl.invalid_cursor", batch.Diagnostic?.Code);
        Assert.AreEqual("valid", Encoding.UTF8.GetString(Assert.ContainsSingle(batch.Lines).Utf8));
        Assert.AreEqual(1L, batch.NextCursor.LineNumber);
    }

    [TestMethod]
    public async Task ReadBatchAsync_ResetsZeroLineCursorPositionedAfterFirstLine()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("zero-line-after-first.jsonl");
        await File.WriteAllTextAsync(path, "first\nsecond\n", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();
        JsonlReadBatch firstLine = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 1,
            CancellationToken.None);
        JsonlCursor impossible = firstLine.NextCursor with { LineNumber = 0 };

        JsonlReadBatch reset = await reader.ReadBatchAsync(
            path,
            impossible,
            maxLines: 200,
            CancellationToken.None);

        Assert.AreEqual("jsonl.invalid_cursor", reset.Diagnostic?.Code);
        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            reset.Lines.Select(line => Encoding.UTF8.GetString(line.Utf8)).ToArray());
        Assert.AreEqual(2L, reset.NextCursor.LineNumber);
    }

    [TestMethod]
    public async Task ReadBatchAsync_ResetsWhenFileIsTruncatedOrReplaced()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("reset.jsonl");
        await File.WriteAllTextAsync(path, "original-first\noriginal-second\n", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();
        JsonlReadBatch original = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        await File.WriteAllTextAsync(path, "new\n", Utf8WithoutBom);

        JsonlReadBatch truncated = await reader.ReadBatchAsync(
            path,
            original.NextCursor,
            maxLines: 200,
            CancellationToken.None);

        Assert.AreEqual("jsonl.source_reset", truncated.Diagnostic?.Code);
        Assert.AreEqual("new", Encoding.UTF8.GetString(Assert.ContainsSingle(truncated.Lines).Utf8));
        Assert.AreEqual(1L, truncated.NextCursor.LineNumber);

        await File.WriteAllTextAsync(path, "replacement-first\nreplacement-second\n", Utf8WithoutBom);

        JsonlReadBatch replaced = await reader.ReadBatchAsync(
            path,
            truncated.NextCursor,
            maxLines: 200,
            CancellationToken.None);

        Assert.AreEqual("jsonl.source_reset", replaced.Diagnostic?.Code);
        Assert.AreEqual("replacement-first", Encoding.UTF8.GetString(replaced.Lines[0].Utf8));
        Assert.AreEqual(1L, replaced.Lines[0].LineNumber);
    }

    [TestMethod]
    public async Task ReadBatchAsync_TrimsCarriageReturnAndTracksByteOffsets()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("crlf.jsonl");
        await File.WriteAllTextAsync(path, "one\r\ntwo\r\n", Utf8WithoutBom);
        var reader = new IncrementalJsonlReader();

        JsonlReadBatch batch = await reader.ReadBatchAsync(
            path,
            JsonlCursor.Start,
            maxLines: 200,
            CancellationToken.None);

        Assert.HasCount(2, batch.Lines);
        Assert.AreEqual("one", Encoding.UTF8.GetString(batch.Lines[0].Utf8));
        Assert.AreEqual("two", Encoding.UTF8.GetString(batch.Lines[1].Utf8));
        Assert.AreEqual(0L, batch.Lines[0].ByteOffset);
        Assert.AreEqual(5L, batch.Lines[1].ByteOffset);
    }
}
