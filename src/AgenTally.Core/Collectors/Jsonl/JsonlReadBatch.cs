namespace AgenTally.Core.Collectors.Jsonl;

public sealed record JsonlLine(
    long LineNumber,
    long ByteOffset,
    byte[] Utf8);

public sealed record JsonlReadBatch(
    IReadOnlyList<JsonlLine> Lines,
    JsonlCursor NextCursor,
    bool EndOfFile,
    int MaxBufferBytes,
    CollectorDiagnostic? Diagnostic);
