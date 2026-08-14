namespace AgenTally.Domain.Sources;

public sealed record SourceInstanceDescriptor(
    string SourceInstanceId,
    string AgentId,
    SourceKind SourceKind,
    string DisplayName,
    string RootPath);

public sealed record SourceEntityDescriptor(
    string SourceInstanceId,
    string SourceEntityId,
    string SourcePath);
