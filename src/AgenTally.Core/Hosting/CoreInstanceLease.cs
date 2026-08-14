using AgenTally.Storage.Runtime;

namespace AgenTally.Core.Hosting;

public sealed class CoreInstanceLease : IDisposable
{
    private readonly IReadOnlyList<Semaphore> _leases;
    private int _disposed;

    private CoreInstanceLease(IReadOnlyList<Semaphore> leases)
    {
        _leases = leases;
    }

    public static CoreInstanceLease? TryAcquire(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return TryAcquire(profile.SourceLeaseName, profile.DatabaseLeaseName);
    }

    public static CoreInstanceLease? TryAcquire(
        string sourceLeaseName,
        string databaseLeaseName) =>
        TryAcquire([sourceLeaseName], databaseLeaseName);

    public static CoreInstanceLease? TryAcquireDatabase(string databaseLeaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLeaseName);
        return TryAcquireNames([databaseLeaseName]);
    }

    public static CoreInstanceLease? TryAcquire(
        IReadOnlyCollection<string> sourceLeaseNames,
        string databaseLeaseName)
    {
        ArgumentNullException.ThrowIfNull(sourceLeaseNames);
        if (sourceLeaseNames.Count == 0)
        {
            throw new ArgumentException(
                "At least one source lease is required.",
                nameof(sourceLeaseNames));
        }

        foreach (string sourceLeaseName in sourceLeaseNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceLeaseName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLeaseName);
        string[] names = [.. sourceLeaseNames.Append(databaseLeaseName).Distinct(StringComparer.Ordinal)];
        return TryAcquireNames(names);
    }

    private static CoreInstanceLease? TryAcquireNames(
        IReadOnlyCollection<string> leaseNames)
    {
        string[] names = [.. leaseNames.Distinct(StringComparer.Ordinal)];
        Array.Sort(names, StringComparer.Ordinal);
        var acquired = new List<Semaphore>(names.Length);
        try
        {
            foreach (string name in names)
            {
                var lease = new Semaphore(1, 1, name);
                bool ownsLease;
                try
                {
                    ownsLease = lease.WaitOne(0);
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }

                if (!ownsLease)
                {
                    lease.Dispose();
                    Release(acquired);
                    return null;
                }

                acquired.Add(lease);
            }

            return new CoreInstanceLease(acquired);
        }
        catch
        {
            Release(acquired);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release(_leases);
        }
    }

    private static void Release(IEnumerable<Semaphore> leases)
    {
        foreach (Semaphore lease in leases.Reverse())
        {
            try
            {
                lease.Release();
            }
            finally
            {
                lease.Dispose();
            }
        }
    }
}
