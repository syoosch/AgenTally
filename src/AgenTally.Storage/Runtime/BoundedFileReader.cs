using System.Buffers;

namespace AgenTally.Storage.Runtime;

public static class BoundedFileReader
{
    private const int BufferBytes = 64 * 1024;

    public static byte[] ReadAllBytes(
        string path,
        int maximumBytes)
    {
        using FileStream stream = Open(path, maximumBytes, asynchronous: false);
        byte[] rented = ArrayPool<byte>.Shared.Rent(
            Math.Min(BufferBytes, maximumBytes + 1));
        try
        {
            using var output = new MemoryStream(
                capacity: checked((int)Math.Min(stream.Length, maximumBytes)));
            while (true)
            {
                int read = stream.Read(rented, 0, rented.Length);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw TooLarge();
                }

                output.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public static async Task<byte[]> ReadAllBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = Open(
            path,
            maximumBytes,
            asynchronous: true);
        byte[] rented = ArrayPool<byte>.Shared.Rent(
            Math.Min(BufferBytes, maximumBytes + 1));
        try
        {
            using var output = new MemoryStream(
                capacity: checked((int)Math.Min(stream.Length, maximumBytes)));
            while (true)
            {
                int read = await stream.ReadAsync(
                    rented.AsMemory(0, rented.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw TooLarge();
                }

                await output.WriteAsync(
                    rented.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static FileStream Open(
        string path,
        int maximumBytes,
        bool asynchronous)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes <= 0 || maximumBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                "The maximum file size must leave room for one overflow byte.");
        }

        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        info.Refresh();
        if (!info.Exists ||
            info.Length > maximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw TooLarge();
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            asynchronous
                ? FileOptions.Asynchronous | FileOptions.SequentialScan
                : FileOptions.SequentialScan);
        try
        {
            if (stream.Length > maximumBytes ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw TooLarge();
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static InvalidDataException TooLarge() => new(
        "The local file is missing, unsafe, or outside its supported size limit.");
}
