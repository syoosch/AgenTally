using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace AgenTally.UI.Updates;

internal static class PrivacySafeVersionCheckHttpClientFactory
{
    internal static HttpClient Create()
    {
        SocketsHttpHandler handler = CreateHandler(HttpClient.DefaultProxy);
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static SocketsHttpHandler CreateHandler(IWebProxy systemProxy)
    {
        ArgumentNullException.ThrowIfNull(systemProxy);
        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new CredentiallessReadOnlyProxy(systemProxy),
            Credentials = null,
            DefaultProxyCredentials = null,
            UseCookies = false,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            ConnectTimeout = VersionCheckConfiguration.DefaultTimeout,
            MaxResponseHeadersLength = 16,
            ActivityHeadersPropagator =
                DistributedContextPropagator.CreateNoOutputPropagator()
        };
    }
}

internal sealed class CredentiallessReadOnlyProxy : IWebProxy
{
    private readonly IWebProxy _systemProxy;

    public CredentiallessReadOnlyProxy(IWebProxy systemProxy)
    {
        _systemProxy = systemProxy ??
            throw new ArgumentNullException(nameof(systemProxy));
    }

    public ICredentials? Credentials
    {
        get => null;
        set
        {
            if (value is not null)
            {
                throw new InvalidOperationException(
                    "Version checking does not accept proxy credentials.");
            }
        }
    }

    public Uri GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Uri proxy = _systemProxy.GetProxy(destination) ??
            throw new InvalidOperationException(
                "The system proxy returned an invalid route.");
        if (!string.IsNullOrEmpty(proxy.UserInfo))
        {
            throw new InvalidOperationException(
                "Credential-bearing proxy routes are not permitted.");
        }

        return proxy;
    }

    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _systemProxy.IsBypassed(host);
    }
}
