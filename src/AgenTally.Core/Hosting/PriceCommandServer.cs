using System.IO.Pipes;
using System.Text.Json;
using AgenTally.Storage.Pricing;

namespace AgenTally.Core.Hosting;

public sealed class PriceCommandServer
{
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    private readonly string _pipeName;
    private readonly PriceCommandHandler _handler;

    public PriceCommandServer(
        string pipeName,
        PriceCommandHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            await HandleConnectionAsync(pipe, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken shutdownToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownToken);
        timeout.CancelAfter(OperationTimeout);
        PriceCommandRequest? request = null;
        PriceCommandResponse response;
        try
        {
            request = await PriceCommandProtocol.ReadAsync<PriceCommandRequest>(
                pipe,
                timeout.Token);
            response = await _handler.HandleAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (
            !shutdownToken.IsCancellationRequested &&
            timeout.IsCancellationRequested)
        {
            response = FailureResponse(
                request,
                PriceCommandMessageCodes.OperationTimedOut);
        }
        catch (Exception exception)
            when (exception is InvalidDataException or
                EndOfStreamException or
                JsonException)
        {
            response = FailureResponse(
                request,
                PriceCommandMessageCodes.InvalidRequest,
                PriceCommandResultCode.InvalidRequest);
        }

        if (!pipe.IsConnected)
        {
            return;
        }

        try
        {
            await PriceCommandProtocol.WriteAsync(
                pipe,
                response,
                shutdownToken);
        }
        catch (IOException)
        {
            // The database operation, if any, is already complete. A disconnected
            // UI confirms the result by refreshing its read-only snapshot.
        }
    }

    private static PriceCommandResponse FailureResponse(
        PriceCommandRequest? request,
        string messageCode,
        PriceCommandResultCode result = PriceCommandResultCode.Failed) => new(
            PriceCommandProtocol.CurrentVersion,
            request?.RequestId ?? string.Empty,
            result,
            messageCode,
            0);
}
