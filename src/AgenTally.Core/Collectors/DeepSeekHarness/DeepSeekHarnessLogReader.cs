using System.Buffers.Binary;
using ZstdSharp;

namespace AgenTally.Core.Collectors.DeepSeekHarness;

// The container is documented by DeepSeek Harness at revision
// 687f282e8625c6b8d11ff58211a90524be2e4317, session-persistence-jsonl/src/zstd.ts
// (MIT; see THIRD_PARTY_NOTICES.md). Frame fields follow RFC 8878.
// No Harness code, command, plug-in or external executable runs in production.
internal sealed class DeepSeekHarnessLogReader : IDisposable
{
    internal const int MaxFrameBytes = 32 * 1024 * 1024;
    internal const int MaxLineBytes = 8 * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly bool _compressed;
    private readonly long _length;
    private readonly Decompressor _decompressor = new();

    internal DeepSeekHarnessLogReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        _length = _stream.Length; // One immutable read boundary; later appends wait for the next pass.
        _compressed = path.EndsWith(".zstd", StringComparison.Ordinal);
        _decompressor.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 25);
    }

    internal long Length => _length;

    internal DeepSeekHarnessFrame? Read(long start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (start == _length) return null;
        if (start < 0 || start > _length) throw new InvalidDataException("Invalid Harness read offset.");
        _stream.Position = start;
        if (!_compressed) return ReadPlain(start, cancellationToken);
        if (_length - start < 5) return new(start, start, [], true);
        Span<byte> prefix = stackalloc byte[5];
        _stream.ReadExactly(prefix);
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != 0xFD2FB528 || (prefix[4] & 0x18) != 0)
            throw new InvalidDataException("Invalid Harness Zstandard frame header.");
        byte descriptor = prefix[4];
        int sizeFlag = descriptor >> 6;
        int dictionaryFlag = descriptor & 3;
        int remainingHeader = ((descriptor & 0x20) == 0 ? 1 : 0) +
            (dictionaryFlag == 3 ? 4 : dictionaryFlag) +
            (sizeFlag == 0 ? ((descriptor & 0x20) != 0 ? 1 : 0) : 1 << sizeFlag);
        if (!Advance(remainingHeader, start)) return new(start, start, [], true);
        Span<byte> block = stackalloc byte[3];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_length - _stream.Position < 3) return new(start, start, [], true);
            _stream.ReadExactly(block);
            int header = block[0] | block[1] << 8 | block[2] << 16;
            int kind = (header >> 1) & 3;
            int size = header >> 3;
            if (kind == 3 || size > 128 * 1024)
                throw new InvalidDataException("Invalid Harness Zstandard block.");
            if (!Advance(kind == 1 ? 1 : size, start)) return new(start, start, [], true);
            if ((header & 1) != 0) break;
        }
        if ((descriptor & 4) != 0 && !Advance(4, start)) return new(start, start, [], true);
        long end = _stream.Position;
        byte[] encoded = new byte[checked((int)(end - start))];
        _stream.Position = start;
        _stream.ReadExactly(encoded);
        try
        {
            byte[] decoded = _decompressor.Unwrap(encoded, MaxFrameBytes).ToArray();
            return new(start, end, decoded, false);
        }
        catch (ZstdException)
        {
            throw new InvalidDataException("Harness Zstandard validation failed.");
        }
    }

    private bool Advance(int count, long start)
    {
        if (_stream.Position - start + count > MaxFrameBytes)
            throw new InvalidDataException("The Harness compressed frame exceeds its bound.");
        if (_length - _stream.Position < count) return false;
        _stream.Position += count;
        return true;
    }

    private DeepSeekHarnessFrame ReadPlain(long start, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        byte[] buffer = new byte[65536];
        while (_stream.Position < _length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, _length - _stream.Position));
            if (count == 0) break;
            int newline = buffer.AsSpan(0, count).IndexOf((byte)'\n');
            int take = newline >= 0 ? newline + 1 : count;
            if (line.Length + take > MaxLineBytes)
                throw new InvalidDataException("The Harness JSONL record exceeds its bound.");
            line.Write(buffer, 0, take);
            if (newline >= 0) return new(start, start + line.Length, line.ToArray(), false);
        }
        return new(start, start, [], true);
    }

    public void Dispose()
    {
        _decompressor.Dispose();
        _stream.Dispose();
    }
}

internal sealed record DeepSeekHarnessFrame(long Start, long End, byte[] Bytes, bool Incomplete)
{
    internal IEnumerable<ReadOnlyMemory<byte>> Lines()
    {
        int start = 0;
        int rows = 0;
        while (start < Bytes.Length)
        {
            int newline = Bytes.AsSpan(start).IndexOf((byte)'\n');
            if (newline < 0) throw new InvalidDataException("A committed Harness frame ends inside a JSONL record.");
            if (newline > DeepSeekHarnessLogReader.MaxLineBytes || ++rows > 20000)
                throw new InvalidDataException("Harness JSONL record bounds exceeded.");
            int count = newline > 0 && Bytes[start + newline - 1] == '\r' ? newline - 1 : newline;
            if (count == 0) throw new InvalidDataException("A Harness JSONL record is empty.");
            yield return Bytes.AsMemory(start, count);
            start += newline + 1;
        }
    }
}
