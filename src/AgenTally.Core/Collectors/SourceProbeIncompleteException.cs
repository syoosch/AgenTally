namespace AgenTally.Core.Collectors;

public sealed class SourceProbeIncompleteException : InvalidOperationException
{
    public SourceProbeIncompleteException(int diagnosticCount)
        : base("Source probing was incomplete.")
    {
        if (diagnosticCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticCount));
        }

        DiagnosticCount = diagnosticCount;
    }

    public int DiagnosticCount { get; }
}
