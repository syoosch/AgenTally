using System.Net.Http;

namespace AgenTally.UI.Updates;

internal static class VersionCheckProductionServiceFactory
{
    public static IVersionCheckService Create(
        VersionCheckRuntimeConfiguration runtimeConfiguration)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfiguration);
        VersionCheckConfiguration configuration =
            runtimeConfiguration.ServiceConfiguration ??
            throw new InvalidOperationException(
                "Version checking is not configured.");
        HttpClient client = PrivacySafeVersionCheckHttpClientFactory.Create();
        try
        {
            return new OwnedHttpVersionCheckService(
                client,
                configuration);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class OwnedHttpVersionCheckService :
        IVersionCheckService,
        IDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpVersionCheckService _service;

        public OwnedHttpVersionCheckService(
            HttpClient client,
            VersionCheckConfiguration configuration)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _service = new HttpVersionCheckService(client, configuration);
        }

        public Task<VersionCheckResult> CheckAsync(
            ReleaseVersion currentVersion,
            CancellationToken cancellationToken) =>
            _service.CheckAsync(currentVersion, cancellationToken);

        public void Dispose() => _client.Dispose();
    }
}
