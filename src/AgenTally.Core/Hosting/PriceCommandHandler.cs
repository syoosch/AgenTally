using AgenTally.Storage.Pricing;

namespace AgenTally.Core.Hosting;

public sealed class PriceCommandHandler
{
    private readonly IPriceLedger _ledger;
    private readonly CoreDatabaseWriteGate _writeGate;

    public PriceCommandHandler(
        IPriceLedger ledger,
        CoreDatabaseWriteGate writeGate)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _writeGate = writeGate ??
            throw new ArgumentNullException(nameof(writeGate));
    }

    public async Task<PriceCommandResponse> HandleAsync(
        PriceCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != PriceCommandProtocol.CurrentVersion)
        {
            return Response(
                request,
                PriceCommandResultCode.UnsupportedProtocol,
                PriceCommandMessageCodes.UnsupportedProtocol);
        }

        if (!Guid.TryParseExact(request.RequestId, "D", out _))
        {
            return Response(
                request,
                PriceCommandResultCode.InvalidRequest,
                PriceCommandMessageCodes.InvalidRequest);
        }

        using IDisposable? lease =
            await _writeGate.TryEnterPricingAsync(cancellationToken);
        if (lease is null)
        {
            return Response(
                request,
                PriceCommandResultCode.Busy,
                PriceCommandMessageCodes.Busy);
        }

        try
        {
            return request.Command switch
            {
                PriceCommandKind.SetPriceOverride =>
                    await SetOverrideAsync(request, cancellationToken),
                PriceCommandKind.RestorePriceDefault =>
                    await RestoreDefaultAsync(request, cancellationToken),
                PriceCommandKind.RestoreAllPriceDefaults =>
                    await RestoreAllDefaultsAsync(request, cancellationToken),
                _ => Response(
                    request,
                    PriceCommandResultCode.InvalidRequest,
                    PriceCommandMessageCodes.InvalidRequest)
            };
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                InvalidOperationException or
                OverflowException)
        {
            return Response(
                request,
                PriceCommandResultCode.InvalidRequest,
                PriceCommandMessageCodes.InvalidRequest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Response(
                request,
                PriceCommandResultCode.Failed,
                PriceCommandMessageCodes.Failed);
        }
    }

    private async Task<PriceCommandResponse> SetOverrideAsync(
        PriceCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NormalizedModel) ||
            request.Rate is null)
        {
            return Response(
                request,
                PriceCommandResultCode.InvalidRequest,
                PriceCommandMessageCodes.InvalidRequest);
        }

        ModelPriceRate rate = request.Rate.ToRate(request.NormalizedModel);
        int priced = await _ledger.SetCustomPriceAsync(
            rate,
            cancellationToken);
        return Response(
            request,
            PriceCommandResultCode.Success,
            PriceCommandMessageCodes.PriceUpdated,
            priced);
    }

    private async Task<PriceCommandResponse> RestoreDefaultAsync(
        PriceCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NormalizedModel) ||
            request.Rate is not null)
        {
            return Response(
                request,
                PriceCommandResultCode.InvalidRequest,
                PriceCommandMessageCodes.InvalidRequest);
        }

        int priced = await _ledger.RestoreDefaultAsync(
            request.NormalizedModel,
            cancellationToken);
        return Response(
            request,
            PriceCommandResultCode.Success,
            PriceCommandMessageCodes.PriceDefaultRestored,
            priced);
    }

    private async Task<PriceCommandResponse> RestoreAllDefaultsAsync(
        PriceCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (request.NormalizedModel is not null || request.Rate is not null)
        {
            return Response(
                request,
                PriceCommandResultCode.InvalidRequest,
                PriceCommandMessageCodes.InvalidRequest);
        }

        int priced = await _ledger.RestoreAllDefaultsAsync(cancellationToken);
        return Response(
            request,
            PriceCommandResultCode.Success,
            PriceCommandMessageCodes.AllPriceDefaultsRestored,
            priced);
    }

    private static PriceCommandResponse Response(
        PriceCommandRequest request,
        PriceCommandResultCode result,
        string messageCode,
        int newlyPricedRecords = 0) => new(
            PriceCommandProtocol.CurrentVersion,
            request.RequestId ?? string.Empty,
            result,
            messageCode,
            newlyPricedRecords);
}
