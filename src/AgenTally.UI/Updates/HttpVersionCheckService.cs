using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgenTally.UI.Updates;

internal sealed class HttpVersionCheckService : IVersionCheckService
{
    internal const int MaximumResponseBytes = 16 * 1024;
    private const int CurrentSchemaVersion = 1;
    private const string ExpectedProduct = "AgenTally";
    private const string ExpectedChannel = "Stable";
    private readonly HttpClient _httpClient;
    private readonly VersionCheckConfiguration _configuration;

    public HttpVersionCheckService(
        HttpClient httpClient,
        VersionCheckConfiguration configuration)
    {
        _httpClient = httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        if (_httpClient.DefaultRequestHeaders.Any())
        {
            throw new ArgumentException(
                "The version-check HTTP client must not define default request headers.",
                nameof(httpClient));
        }
    }

    public async Task<VersionCheckResult> CheckAsync(
        ReleaseVersion currentVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_configuration.Timeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                _configuration.ManifestUri);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failure(
                    VersionCheckOutcome.NetworkFailure,
                    currentVersion);
            }

            if (!HasJsonContentType(response.Content.Headers.ContentType) ||
                response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return Failure(
                    VersionCheckOutcome.InvalidResponse,
                    currentVersion);
            }

            byte[]? payload = await ReadBoundedAsync(
                response.Content,
                timeout.Token).ConfigureAwait(false);
            if (payload is null ||
                !TryParseManifest(payload, out ReleaseVersion latestVersion))
            {
                return Failure(
                    VersionCheckOutcome.InvalidResponse,
                    currentVersion);
            }

            VersionCheckOutcome outcome =
                latestVersion.CompareTo(currentVersion) > 0
                    ? VersionCheckOutcome.UpdateAvailable
                    : VersionCheckOutcome.UpToDate;
            return new VersionCheckResult(
                outcome,
                currentVersion,
                latestVersion,
                outcome == VersionCheckOutcome.UpdateAvailable
                    ? _configuration.ReleasePageUri
                    : null);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                VersionCheckOutcome.NetworkFailure,
                currentVersion);
        }
        catch (Exception exception)
            when (exception is HttpRequestException
                or IOException
                or InvalidOperationException)
        {
            return Failure(
                VersionCheckOutcome.NetworkFailure,
                currentVersion);
        }
    }

    private static VersionCheckResult Failure(
        VersionCheckOutcome outcome,
        ReleaseVersion currentVersion) =>
        new(outcome, currentVersion, null, null);

    private static bool HasJsonContentType(MediaTypeHeaderValue? contentType)
    {
        string? mediaType = contentType?.MediaType;
        return string.Equals(
                mediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase) ||
            (mediaType is not null &&
             mediaType.StartsWith(
                 "application/",
                 StringComparison.OrdinalIgnoreCase) &&
             mediaType.EndsWith(
                 "+json",
                 StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream stream =
            await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        byte[] rented = ArrayPool<byte>.Shared.Rent(
            MaximumResponseBytes + 1);
        try
        {
            int length = 0;
            while (length <= MaximumResponseBytes)
            {
                int read = await stream.ReadAsync(
                    rented.AsMemory(
                        length,
                        MaximumResponseBytes + 1 - length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return rented.AsSpan(0, length).ToArray();
                }

                length += read;
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(
                rented,
                clearArray: true);
        }
    }

    private static bool TryParseManifest(
        byte[] payload,
        out ReleaseVersion version)
    {
        version = default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            int? schemaVersion = null;
            string? product = null;
            string? channel = null;
            string? versionText = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        if (schemaVersion is not null ||
                            property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out int schema))
                        {
                            return false;
                        }

                        schemaVersion = schema;
                        break;

                    case "product":
                        if (product is not null ||
                            property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        product = property.Value.GetString();
                        break;

                    case "channel":
                        if (channel is not null ||
                            property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        channel = property.Value.GetString();
                        break;

                    case "version":
                        if (versionText is not null ||
                            property.Value.ValueKind != JsonValueKind.String)
                        {
                            return false;
                        }

                        versionText = property.Value.GetString();
                        break;
                }
            }

            return schemaVersion == CurrentSchemaVersion &&
                string.Equals(
                    product,
                    ExpectedProduct,
                    StringComparison.Ordinal) &&
                string.Equals(
                    channel,
                    ExpectedChannel,
                    StringComparison.Ordinal) &&
                ReleaseVersion.TryParse(versionText, out version);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
