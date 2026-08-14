using System.Buffers;
using System.Security.Cryptography;

namespace AgenTally.Core.Collectors.Jsonl;

public sealed class IncrementalJsonlReader
{
    private const int BufferBytes = 64 * 1024;
    private const int MaxBatchPayloadBytes = 8 * 1024 * 1024;

    public async Task<JsonlReadBatch> ReadBatchAsync(
        string path,
        JsonlCursor cursor,
        int maxLines,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cursor);

        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines), "每批行数必须大于零。");
        }

        await using var stream = OpenRead(path);
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(BufferBytes);

        try
        {
            FingerprintResult fingerprint = await ReadFingerprintAsync(
                stream,
                readBuffer,
                cancellationToken);

            if (fingerprint.Status is FingerprintStatus.TooLong)
            {
                return new JsonlReadBatch(
                    [],
                    JsonlCursor.Start,
                    true,
                    BufferBytes,
                    FirstLineTooLongDiagnostic());
            }

            bool validCursor = cursor.TryGetPendingBytes(out byte[] pendingBytes);
            CollectorDiagnostic? diagnostic = validCursor
                ? null
                : JsonlCursor.InvalidCursorDiagnostic();
            if (!validCursor)
            {
                cursor = JsonlCursor.Start;
                pendingBytes = [];
            }

            long snapshotLength = stream.Length;
            if (fingerprint.Status is FingerprintStatus.NoCompleteLine)
            {
                if (cursor != JsonlCursor.Start)
                {
                    diagnostic = ResetDiagnostic();
                }

                return new JsonlReadBatch(
                    [],
                    JsonlCursor.Start,
                    true,
                    BufferBytes,
                    diagnostic);
            }

            string sourceFingerprint = fingerprint.Value!;
            if (snapshotLength < cursor.ByteOffset ||
                (!string.IsNullOrEmpty(cursor.SourceFingerprint) &&
                 !string.Equals(
                     cursor.SourceFingerprint,
                     sourceFingerprint,
                     StringComparison.Ordinal)))
            {
                cursor = JsonlCursor.Start;
                pendingBytes = [];
                diagnostic = ResetDiagnostic();
            }

            JsonlCursor batchStartCursor = string.IsNullOrEmpty(cursor.SourceFingerprint)
                ? cursor with { SourceFingerprint = sourceFingerprint }
                : cursor;

            stream.Seek(cursor.ByteOffset, SeekOrigin.Begin);
            var lines = new List<JsonlLine>(Math.Min(maxLines, 200));
            var lineBuffer = new BoundedLineBuffer();
            int batchPayloadBytes = 0;
            bool pendingCarriageReturn = false;
            bool discardingOversizedLine = false;

            foreach (byte value in pendingBytes)
            {
                AppendContentByte(
                    value,
                    lineBuffer,
                    ref pendingCarriageReturn,
                    ref discardingOversizedLine);
            }

            long lineNumber = cursor.LineNumber;
            long lineStartOffset = cursor.ByteOffset - pendingBytes.LongLength;
            int processedLines = 0;

            while (stream.Position < snapshotLength)
            {
                int requestedBytes = (int)Math.Min(
                    BufferBytes,
                    snapshotLength - stream.Position);
                int bytesRead = await stream.ReadAsync(
                    readBuffer.AsMemory(0, requestedBytes),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                long chunkStartOffset = stream.Position - bytesRead;
                for (int index = 0; index < bytesRead; index++)
                {
                    byte value = readBuffer[index];
                    if (value != (byte)'\n')
                    {
                        AppendContentByte(
                            value,
                            lineBuffer,
                            ref pendingCarriageReturn,
                            ref discardingOversizedLine);
                        continue;
                    }

                    long consumedOffset = chunkStartOffset + index + 1;
                    lineNumber++;
                    processedLines++;

                    if (discardingOversizedLine)
                    {
                        diagnostic ??= CompleteLineTooLongDiagnostic(lineStartOffset);
                    }
                    else
                    {
                        if (lines.Count > 0 &&
                            batchPayloadBytes + lineBuffer.Length > MaxBatchPayloadBytes)
                        {
                            var boundedCursor = new JsonlCursor(
                                lineStartOffset,
                                string.Empty,
                                lineNumber - 1,
                                sourceFingerprint);
                            var boundedCandidate = new JsonlReadBatch(
                                lines,
                                boundedCursor,
                                false,
                                BufferBytes,
                                diagnostic);
                            return await RevalidateSnapshotAsync(
                                path,
                                snapshotLength,
                                sourceFingerprint,
                                boundedCandidate,
                                readBuffer,
                                cancellationToken);
                        }

                        byte[] utf8 = lineBuffer.ToArray();
                        lines.Add(new JsonlLine(lineNumber, lineStartOffset, utf8));
                        batchPayloadBytes += utf8.Length;
                    }

                    lineBuffer.Clear();
                    pendingCarriageReturn = false;
                    discardingOversizedLine = false;
                    lineStartOffset = consumedOffset;

                    if (processedLines == maxLines)
                    {
                        var nextCursor = new JsonlCursor(
                            consumedOffset,
                            string.Empty,
                            lineNumber,
                            sourceFingerprint);
                        var candidate = new JsonlReadBatch(
                            lines,
                            nextCursor,
                            consumedOffset >= snapshotLength,
                            BufferBytes,
                            diagnostic);
                        return await RevalidateSnapshotAsync(
                            path,
                            snapshotLength,
                            sourceFingerprint,
                            candidate,
                            readBuffer,
                            cancellationToken);
                    }
                }
            }

            JsonlReadBatch completedBatch;
            if (discardingOversizedLine)
            {
                completedBatch = new JsonlReadBatch(
                    [],
                    batchStartCursor,
                    true,
                    BufferBytes,
                    IncompleteLineTooLongDiagnostic(lineStartOffset));
            }
            else
            {
                int pendingLength = lineBuffer.Length + (pendingCarriageReturn ? 1 : 0);
                byte[] pending = GC.AllocateUninitializedArray<byte>(pendingLength);
                lineBuffer.WrittenSpan.CopyTo(pending);
                if (pendingCarriageReturn)
                {
                    pending[^1] = (byte)'\r';
                }

                var nextCursor = new JsonlCursor(
                    stream.Position,
                    Convert.ToBase64String(pending),
                    lineNumber,
                    sourceFingerprint);
                completedBatch = new JsonlReadBatch(
                    lines,
                    nextCursor,
                    true,
                    BufferBytes,
                    diagnostic);
            }

            return await RevalidateSnapshotAsync(
                path,
                snapshotLength,
                sourceFingerprint,
                completedBatch,
                readBuffer,
                cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 1,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void AppendContentByte(
        byte value,
        BoundedLineBuffer lineBuffer,
        ref bool pendingCarriageReturn,
        ref bool discardingOversizedLine)
    {
        if (discardingOversizedLine)
        {
            return;
        }

        if (value == (byte)'\r')
        {
            if (pendingCarriageReturn && !lineBuffer.TryAppend((byte)'\r'))
            {
                discardingOversizedLine = true;
                pendingCarriageReturn = false;
                return;
            }

            pendingCarriageReturn = true;
            return;
        }

        if (pendingCarriageReturn)
        {
            if (!lineBuffer.TryAppend((byte)'\r'))
            {
                discardingOversizedLine = true;
                pendingCarriageReturn = false;
                return;
            }

            pendingCarriageReturn = false;
        }

        if (!lineBuffer.TryAppend(value))
        {
            discardingOversizedLine = true;
        }
    }

    private static async Task<JsonlReadBatch> RevalidateSnapshotAsync(
        string path,
        long snapshotLength,
        string expectedFingerprint,
        JsonlReadBatch candidate,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream validationStream = OpenRead(path);
            long currentLength = validationStream.Length;
            FingerprintResult currentFingerprint = await ReadFingerprintAsync(
                validationStream,
                buffer,
                cancellationToken);

            bool sourceChanged = currentLength < snapshotLength ||
                currentFingerprint.Status is not FingerprintStatus.Complete ||
                !string.Equals(
                    currentFingerprint.Value,
                    expectedFingerprint,
                    StringComparison.Ordinal);
            if (!sourceChanged)
            {
                return candidate;
            }

            JsonlCursor resetCursor = currentFingerprint.Status is FingerprintStatus.Complete
                ? JsonlCursor.Start with { SourceFingerprint = currentFingerprint.Value! }
                : JsonlCursor.Start;
            return new JsonlReadBatch(
                [],
                resetCursor,
                currentFingerprint.Status is not FingerprintStatus.Complete,
                BufferBytes,
                ResetDiagnostic());
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new JsonlReadBatch(
                [],
                JsonlCursor.Start,
                true,
                BufferBytes,
                ResetDiagnostic());
        }
    }

    private static async Task<FingerprintResult> ReadFingerprintAsync(
        FileStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        int buffered = 0;

        while (buffered < BufferBytes)
        {
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(buffered, BufferBytes - buffered),
                cancellationToken);
            if (bytesRead == 0)
            {
                return FingerprintResult.NoCompleteLine;
            }

            int newline = buffer.AsSpan(buffered, bytesRead).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                int lineLength = buffered + newline;
                return FingerprintResult.Complete(HashLine(buffer.AsSpan(0, lineLength)));
            }

            buffered += bytesRead;
        }

        byte[] terminator = new byte[2];
        int nextByte = await stream.ReadAsync(terminator.AsMemory(0, 1), cancellationToken);
        if (nextByte == 0)
        {
            return FingerprintResult.NoCompleteLine;
        }

        if (terminator[0] == (byte)'\n')
        {
            return FingerprintResult.Complete(HashLine(buffer.AsSpan(0, BufferBytes)));
        }

        if (terminator[0] != (byte)'\r')
        {
            return FingerprintResult.TooLong;
        }

        int followingByte = await stream.ReadAsync(terminator.AsMemory(1, 1), cancellationToken);
        if (followingByte == 0)
        {
            return FingerprintResult.NoCompleteLine;
        }

        return terminator[1] == (byte)'\n'
            ? FingerprintResult.Complete(HashLine(buffer.AsSpan(0, BufferBytes)))
            : FingerprintResult.TooLong;
    }

    private static string HashLine(ReadOnlySpan<byte> line)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        return Convert.ToHexString(SHA256.HashData(line)).ToLowerInvariant();
    }

    private static CollectorDiagnostic FirstLineTooLongDiagnostic() => new(
        "jsonl.first_line_too_long",
        "JSONL 首行超过 64 KiB 指纹上限，未提交读取游标。");

    private static CollectorDiagnostic CompleteLineTooLongDiagnostic(long byteOffset) => new(
        "jsonl.line_too_long",
        "JSONL 行超过 8 MiB 上限，已跳过该完整行。",
        ByteOffset: byteOffset);

    private static CollectorDiagnostic IncompleteLineTooLongDiagnostic(long byteOffset) => new(
        "jsonl.incomplete_line_too_long",
        "JSONL 未完成行超过 8 MiB 上限，读取游标未推进。",
        ByteOffset: byteOffset);

    private static CollectorDiagnostic ResetDiagnostic() => new(
        "jsonl.source_reset",
        "JSONL 文件已截断或替换，读取游标已从头重置。");

    private enum FingerprintStatus
    {
        Complete,
        NoCompleteLine,
        TooLong
    }

    private sealed record FingerprintResult(
        FingerprintStatus Status,
        string? Value)
    {
        public static FingerprintResult NoCompleteLine { get; } = new(
            FingerprintStatus.NoCompleteLine,
            null);

        public static FingerprintResult TooLong { get; } = new(
            FingerprintStatus.TooLong,
            null);

        public static FingerprintResult Complete(string value) => new(
            FingerprintStatus.Complete,
            value);
    }

    private sealed class BoundedLineBuffer
    {
        private const int InitialCapacity = 4 * 1024;

        private byte[] _buffer = GC.AllocateUninitializedArray<byte>(InitialCapacity);

        public int Length { get; private set; }

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, Length);

        public bool TryAppend(byte value)
        {
            if (Length == JsonlCursor.MaxLogicalLineBytes)
            {
                return false;
            }

            if (Length == _buffer.Length)
            {
                int nextCapacity = Math.Min(
                    JsonlCursor.MaxLogicalLineBytes,
                    checked(_buffer.Length * 2));
                Array.Resize(ref _buffer, nextCapacity);
            }

            _buffer[Length] = value;
            Length++;
            return true;
        }

        public byte[] ToArray()
        {
            byte[] value = GC.AllocateUninitializedArray<byte>(Length);
            WrittenSpan.CopyTo(value);
            return value;
        }

        public void Clear() => Length = 0;
    }
}
