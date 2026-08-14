using AgenTally.Domain.Sources;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors;

public interface IIncrementalFileCollector : ISourceFileChangeCollector
{
    bool TryGetCursorByteOffset(StoredCursor cursor, out long byteOffset);
}
