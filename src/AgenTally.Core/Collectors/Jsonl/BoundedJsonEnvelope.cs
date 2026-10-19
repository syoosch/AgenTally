using System.Text;
using System.Text.Json;

namespace AgenTally.Core.Collectors.Jsonl;

// Validate every UTF-8/string unit while retaining only bounded strings. The caller
// must validate the resulting JSON and reject omitted fields needed for semantics.
internal static class BoundedJsonEnvelope
{
    internal const string OmittedString = "\0agentally:oversized-string";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Read(
        Stream source, long byteOffset, long byteLength,
        int maxStringCharacters, int maxEnvelopeBytes,
        CancellationToken cancellationToken)
    {
        long originalPosition = source.Position;
        try
        {
            source.Seek(byteOffset, SeekOrigin.Begin);
            using var range = new RangeStream(source, byteLength, cancellationToken);
            using var reader = new StreamReader(
                range, StrictUtf8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 64 * 1024, leaveOpen: true);
            return CompactStrings(reader, maxStringCharacters, maxEnvelopeBytes);
        }
        finally
        {
            source.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static byte[] CompactStrings(StreamReader reader, int maxStringCharacters, int maxEnvelopeBytes)
    {
        using var output = new MemoryStream();
        int next;
        while ((next = reader.Read()) >= 0)
        {
            if (next == '"')
            {
                byte[] token = JsonSerializer.SerializeToUtf8Bytes(ReadString(reader, maxStringCharacters));
                if (output.Length + token.Length > maxEnvelopeBytes)
                {
                    throw new JsonException("Oversized compact envelope.");
                }

                output.Write(token);
            }
            else
            {
                if (next > 127 || output.Length == maxEnvelopeBytes)
                {
                    throw new JsonException("Invalid or oversized compact envelope.");
                }

                output.WriteByte((byte)next);
            }
        }

        return output.ToArray();
    }

    private static string ReadString(StreamReader reader, int maxStringCharacters)
    {
        var text = new StringBuilder();
        bool omitted = false;
        while (true)
        {
            int next = reader.Read();
            if (next == '"')
            {
                return omitted ? OmittedString : text.ToString();
            }

            char first = ReadStringUnit(reader, next);
            char? second = null;
            if (char.IsHighSurrogate(first))
            {
                second = ReadStringUnit(reader, reader.Read());
                if (!char.IsLowSurrogate(second.Value))
                {
                    throw new JsonException("Invalid JSON surrogate pair.");
                }
            }
            else if (char.IsLowSurrogate(first))
            {
                throw new JsonException("Invalid JSON surrogate pair.");
            }

            if (!omitted)
            {
                int units = second.HasValue ? 2 : 1;
                if (text.Length + units > maxStringCharacters)
                {
                    omitted = true;
                    text.Clear();
                }
                else
                {
                    text.Append(first);
                    if (second.HasValue) { text.Append(second.Value); }
                }
            }
        }
    }

    private static char ReadStringUnit(StreamReader reader, int next)
    {
        if (next < 0x20 || next == '"')
        {
            throw new JsonException("Incomplete or invalid JSON string.");
        }

        if (next != '\\') { return (char)next; }
        return reader.Read() switch
        {
            '"' => '"', '\\' => '\\', '/' => '/',
            'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
            'u' => ReadUnicodeEscape(reader),
            _ => throw new JsonException("Invalid JSON escape.")
        };
    }

    private static char ReadUnicodeEscape(StreamReader reader)
    {
        int value = 0;
        for (int index = 0; index < 4; index++)
        {
            int next = reader.Read();
            int digit = next switch
            {
                >= '0' and <= '9' => next - '0',
                >= 'a' and <= 'f' => next - 'a' + 10,
                >= 'A' and <= 'F' => next - 'A' + 10,
                _ => throw new JsonException("Invalid JSON Unicode escape.")
            };
            value = (value << 4) | digit;
        }

        return (char)value;
    }

    private sealed class RangeStream(
        Stream source, long remaining, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining == 0) { return 0; }
            int read = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            if (read == 0) { throw new JsonException("Incomplete source range."); }
            remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
