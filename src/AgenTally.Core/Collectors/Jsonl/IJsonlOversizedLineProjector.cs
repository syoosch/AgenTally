namespace AgenTally.Core.Collectors.Jsonl;

public interface IJsonlOversizedLineProjector
{
    // A projection must validate the complete range and restore the stream position.
    // Null keeps the reader's ordinary oversized-line rejection.
    byte[]? TryProject(
        Stream source,
        long byteOffset,
        long byteLength,
        CancellationToken cancellationToken);
}
