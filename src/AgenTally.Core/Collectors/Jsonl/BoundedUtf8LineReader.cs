using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgenTally.Core.Collectors.Jsonl;

internal readonly record struct BoundedTextLine(
    string Text,
    bool IsTooLong);

internal static class BoundedUtf8LineReader
{
    private const int BufferCharacters = 16 * 1024;

    internal static async IAsyncEnumerable<BoundedTextLine> ReadLinesAsync(
        string path,
        int maximumLineCharacters,
        long maximumSourceBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumLineCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLineCharacters));
        }
        if (maximumSourceBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));
        }

        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        info.Refresh();
        if (!info.Exists ||
            info.Length > maximumSourceBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw UnsafeSource();
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumSourceBytes ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw UnsafeSource();
        }

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        char[] rented = ArrayPool<char>.Shared.Rent(BufferCharacters);
        var line = new StringBuilder(
            capacity: Math.Min(maximumLineCharacters, 1024),
            maxCapacity: maximumLineCharacters);
        bool tooLong = false;
        try
        {
            while (true)
            {
                int read = await reader.ReadAsync(
                    rented.AsMemory(0, BufferCharacters),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (stream.Position > maximumSourceBytes)
                {
                    throw UnsafeSource();
                }

                int segmentStart = 0;
                for (int index = 0; index < read; index++)
                {
                    if (rented[index] != '\n')
                    {
                        continue;
                    }

                    AppendBounded(
                        line,
                        rented.AsSpan(segmentStart, index - segmentStart),
                        maximumLineCharacters,
                        ref tooLong);
                    yield return Complete(line, tooLong);
                    line.Clear();
                    tooLong = false;
                    segmentStart = index + 1;
                }

                AppendBounded(
                    line,
                    rented.AsSpan(segmentStart, read - segmentStart),
                    maximumLineCharacters,
                    ref tooLong);
            }

            if (line.Length > 0 || tooLong)
            {
                yield return Complete(line, tooLong);
            }

            info.Refresh();
            if (!info.Exists ||
                info.Length > maximumSourceBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw UnsafeSource();
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void AppendBounded(
        StringBuilder line,
        ReadOnlySpan<char> value,
        int maximumLineCharacters,
        ref bool tooLong)
    {
        if (tooLong || value.Length == 0)
        {
            return;
        }

        int remaining = maximumLineCharacters - line.Length;
        if (value.Length <= remaining)
        {
            line.Append(value);
            return;
        }

        if (remaining > 0)
        {
            line.Append(value[..remaining]);
        }
        tooLong = true;
    }

    private static BoundedTextLine Complete(
        StringBuilder line,
        bool tooLong)
    {
        if (tooLong)
        {
            return new BoundedTextLine(string.Empty, true);
        }

        int length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
        {
            length--;
        }
        return new BoundedTextLine(line.ToString(0, length), false);
    }

    private static InvalidDataException UnsafeSource() => new(
        "The JSONL source is missing, unsafe, or outside its supported size limit.");
}
