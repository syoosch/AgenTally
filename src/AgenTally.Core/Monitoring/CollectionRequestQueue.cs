using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using AgenTally.Core.Collectors;

namespace AgenTally.Core.Monitoring;

public sealed class CollectionRequestQueue
{
    public const int Capacity = 256;

    private readonly Channel<CollectionRequest> _channel;
    private readonly ConcurrentDictionary<RequestKey, byte> _pending = new();

    public CollectionRequestQueue()
    {
        _channel = Channel.CreateBounded<CollectionRequest>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    }

    public async ValueTask EnqueueAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var key = RequestKey.From(request);
        if (!_pending.TryAdd(key, 0))
        {
            return;
        }

        try
        {
            await _channel.Writer.WriteAsync(request, cancellationToken);
        }
        catch
        {
            _pending.TryRemove(key, out _);
            throw;
        }
    }

    public async ValueTask<CollectionRequest> DequeueAsync(
        CancellationToken cancellationToken)
    {
        CollectionRequest request = await _channel.Reader.ReadAsync(cancellationToken);
        _pending.TryRemove(RequestKey.From(request), out _);
        return request;
    }

    public bool TryDequeue(
        [NotNullWhen(true)] out CollectionRequest? request)
    {
        if (!_channel.Reader.TryRead(out request))
        {
            return false;
        }

        _pending.TryRemove(RequestKey.From(request), out _);
        return true;
    }

    public void Complete() => _channel.Writer.TryComplete();

    private static void ValidateRequest(CollectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Instance);
        ArgumentNullException.ThrowIfNull(request.Entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Instance.SourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Entity.SourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Entity.SourceEntityId);

        if (!string.Equals(
                request.Instance.SourceInstanceId,
                request.Entity.SourceInstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Collection entity must belong to its source instance.",
                nameof(request));
        }
    }

    private readonly record struct RequestKey(
        string SourceInstanceId,
        string SourceEntityId)
    {
        public static RequestKey From(CollectionRequest request) => new(
            request.Instance.SourceInstanceId,
            request.Entity.SourceEntityId);
    }
}
