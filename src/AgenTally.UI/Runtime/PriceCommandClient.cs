using System.IO;
using System.IO.Pipes;
using AgenTally.Storage.Pricing;

namespace AgenTally.UI.Runtime;

public interface IPriceCommandClient
{
    bool IsAvailable { get; }

    Task<PriceCommandResponse> SendAsync(
        PriceCommandRequest request,
        CancellationToken cancellationToken);
}

public sealed class NamedPipePriceCommandClient : IPriceCommandClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    private readonly string _pipeName;

    public NamedPipePriceCommandClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public bool IsAvailable => true;

    public async Task<PriceCommandResponse> SendAsync(
        PriceCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken))
        {
            connect.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.ConnectAsync(connect.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                connect.IsCancellationRequested)
            {
                throw new PriceCommandUnavailableException(
                    "The matching AgenTally Core command pipe is unavailable.");
            }
            catch (IOException exception)
            {
                throw new PriceCommandUnavailableException(
                    "The matching AgenTally Core command pipe is unavailable.",
                    exception);
            }
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        operation.CancelAfter(OperationTimeout);
        try
        {
            await PriceCommandProtocol.WriteAsync(
                pipe,
                request,
                operation.Token);
            PriceCommandResponse response =
                await PriceCommandProtocol.ReadAsync<PriceCommandResponse>(
                    pipe,
                    operation.Token);
            if (response.ProtocolVersion != PriceCommandProtocol.CurrentVersion ||
                !string.Equals(
                    response.RequestId,
                    request.RequestId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Pricing command response identity did not match the request.");
            }

            return response;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            operation.IsCancellationRequested)
        {
            throw new PriceCommandResultUnconfirmedException(
                "The pricing command result could not be confirmed before timeout.");
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException)
        {
            throw new PriceCommandResultUnconfirmedException(
                "The pricing command result could not be confirmed.",
                exception);
        }
    }
}

public sealed class UnavailablePriceCommandClient : IPriceCommandClient
{
    public bool IsAvailable => false;

    public Task<PriceCommandResponse> SendAsync(
        PriceCommandRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<PriceCommandResponse>(
            new PriceCommandUnavailableException(
                "This UI is not attached to a managed AgenTally Core."));
}

public sealed class PriceCommandUnavailableException : IOException
{
    public PriceCommandUnavailableException(string message)
        : base(message)
    {
    }

    public PriceCommandUnavailableException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class PriceCommandResultUnconfirmedException : TimeoutException
{
    public PriceCommandResultUnconfirmedException(string message)
        : base(message)
    {
    }

    public PriceCommandResultUnconfirmedException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
