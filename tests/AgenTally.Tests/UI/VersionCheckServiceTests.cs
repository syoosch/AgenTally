using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using AgenTally.UI.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class VersionCheckServiceTests
{
    private static readonly Uri ManifestUri =
        new("https://updates.invalid/agentally/stable.json");
    private static readonly Uri ReleasePageUri =
        new("https://releases.invalid/agentally");

    [TestMethod]
    [DataRow("0.0.0", 0, 0, 0)]
    [DataRow("1.2.3", 1, 2, 3)]
    [DataRow("01.002.0003", 1, 2, 3)]
    [DataRow("2147483647.0.1", int.MaxValue, 0, 1)]
    public void ReleaseVersion_ParsesStrictNumericTriples(
        string text,
        int major,
        int minor,
        int patch)
    {
        Assert.IsTrue(ReleaseVersion.TryParse(text, out ReleaseVersion version));
        Assert.AreEqual(new ReleaseVersion(major, minor, patch), version);
        Assert.AreEqual($"{major}.{minor}.{patch}", version.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1")]
    [DataRow("1.2")]
    [DataRow("1.2.3.4")]
    [DataRow("1..3")]
    [DataRow("-1.2.3")]
    [DataRow("1.2.3-preview")]
    [DataRow("1.2.3+build")]
    [DataRow(" 1.2.3")]
    [DataRow("1.2.3 ")]
    [DataRow("2147483648.0.0")]
    public void ReleaseVersion_RejectsInvalidShapes(string text)
    {
        Assert.IsFalse(ReleaseVersion.TryParse(text, out _));
    }

    [TestMethod]
    public void ReleaseVersion_ComparesEachNumericComponent()
    {
        Assert.IsGreaterThan(
            0,
            new ReleaseVersion(2, 0, 0).CompareTo(
                new ReleaseVersion(1, 99, 99)));
        Assert.IsGreaterThan(
            0,
            new ReleaseVersion(1, 3, 0).CompareTo(
                new ReleaseVersion(1, 2, 99)));
        Assert.IsGreaterThan(
            0,
            new ReleaseVersion(1, 2, 4).CompareTo(
                new ReleaseVersion(1, 2, 3)));
        Assert.AreEqual(
            0,
            new ReleaseVersion(1, 2, 3).CompareTo(
                new ReleaseVersion(1, 2, 3)));
    }

    [TestMethod]
    public void ReleaseVersion_RejectsNegativeComponents()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReleaseVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReleaseVersion(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReleaseVersion(0, 0, -1));
    }

    [TestMethod]
    public void Configuration_UsesFiveSecondDefaultAndTrustedHttpsUris()
    {
        var configuration = new VersionCheckConfiguration(
            ManifestUri,
            ReleasePageUri);

        Assert.AreEqual(ManifestUri, configuration.ManifestUri);
        Assert.AreEqual(ReleasePageUri, configuration.ReleasePageUri);
        Assert.AreEqual(TimeSpan.FromSeconds(5), configuration.Timeout);
    }

    [TestMethod]
    public void Configuration_RejectsUntrustedUrisAndTimeouts()
    {
        Assert.Throws<ArgumentException>(
            () => new VersionCheckConfiguration(
                new Uri("http://updates.invalid/manifest.json"),
                ReleasePageUri));
        Assert.Throws<ArgumentException>(
            () => new VersionCheckConfiguration(
                new Uri("/manifest.json", UriKind.Relative),
                ReleasePageUri));
        Assert.Throws<ArgumentException>(
            () => new VersionCheckConfiguration(
                new Uri("https://user:secret@updates.invalid/manifest.json"),
                ReleasePageUri));
        Assert.Throws<ArgumentException>(
            () => new VersionCheckConfiguration(
                new Uri("https://updates.invalid/manifest.json#fragment"),
                ReleasePageUri));
        Assert.Throws<ArgumentException>(
            () => new VersionCheckConfiguration(
                ManifestUri,
                new Uri("http://releases.invalid/agentally")));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VersionCheckConfiguration(
                ManifestUri,
                ReleasePageUri,
                TimeSpan.FromMilliseconds(999)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VersionCheckConfiguration(
                ManifestUri,
                ReleasePageUri,
                TimeSpan.FromSeconds(31)));
    }

    [TestMethod]
    public async Task CheckAsync_UpdateAvailableUsesOnlyTrustedReleasePage()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual(ManifestUri, request.RequestUri);
            Assert.IsNull(request.Content);
            Assert.IsNull(request.Headers.Authorization);
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            Assert.IsFalse(request.Headers.Contains("X-AgenTally-Version"));
            Assert.IsEmpty(request.Headers.UserAgent);
            CollectionAssert.AreEqual(
                new[] { "application/json" },
                request.Headers.Accept
                    .Select(value => value.MediaType)
                    .ToArray());
            return Task.FromResult(JsonResponse(Manifest("2.0.0")));
        });
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        VersionCheckResult result = await service.CheckAsync(
            new ReleaseVersion(1, 9, 9),
            CancellationToken.None);

        Assert.AreEqual(VersionCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.AreEqual(new ReleaseVersion(1, 9, 9), result.CurrentVersion);
        Assert.AreEqual(new ReleaseVersion(2, 0, 0), result.LatestVersion);
        Assert.AreEqual(ReleasePageUri, result.ReleasePageUri);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow("1.2.3")]
    [DataRow("1.2.2")]
    public async Task CheckAsync_EqualOrOlderReleaseIsUpToDate(
        string remoteVersion)
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(Manifest(remoteVersion))));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        VersionCheckResult result = await service.CheckAsync(
            new ReleaseVersion(1, 2, 3),
            CancellationToken.None);

        Assert.AreEqual(VersionCheckOutcome.UpToDate, result.Outcome);
        Assert.IsNotNull(result.LatestVersion);
        Assert.IsNull(result.ReleasePageUri);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CheckAsync_AcceptsStructuredJsonMediaTypeAndUnknownFields()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "product": "AgenTally",
              "channel": "Stable",
              "version": "1.0.1",
              "publishedAt": "2026-07-29T00:00:00Z",
              "nested": { "ignored": true }
            }
            """;
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(
                json,
                mediaType: "application/vnd.agentally+json")));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        VersionCheckResult result = await service.CheckAsync(
            new ReleaseVersion(1, 0, 0),
            CancellationToken.None);

        Assert.AreEqual(VersionCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.AreEqual(new ReleaseVersion(1, 0, 1), result.LatestVersion);
    }

    [TestMethod]
    [DataRow("""[]""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","channel":"Stable"}""")]
    [DataRow("""{"schemaVersion":2,"product":"AgenTally","channel":"Stable","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"Other","channel":"Stable","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","channel":"Development","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","channel":"Stable","version":"1.0"}""")]
    [DataRow("""{"schemaVersion":"1","product":"AgenTally","channel":"Stable","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","channel":"Stable","version":101}""")]
    [DataRow("""{"schemaVersion":1,"schemaVersion":1,"product":"AgenTally","channel":"Stable","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","product":"AgenTally","channel":"Stable","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","channel":"Stable","channel":"Stable","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","channel":"Stable","version":"1.0.1","version":"1.0.1"}""")]
    [DataRow("""{"schemaVersion":1,"product":"AgenTally","channel":"Stable","version":"1.0.1",}""")]
    [DataRow("""not-json""")]
    public async Task CheckAsync_RejectsInvalidManifest(string json)
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(json)));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        VersionCheckResult result = await service.CheckAsync(
            new ReleaseVersion(1, 0, 0),
            CancellationToken.None);

        Assert.AreEqual(VersionCheckOutcome.InvalidResponse, result.Outcome);
        Assert.IsNull(result.LatestVersion);
        Assert.IsNull(result.ReleasePageUri);
    }

    [TestMethod]
    public async Task CheckAsync_RejectsWrongOrMissingContentType()
    {
        foreach (string? mediaType in new[] { "text/plain", null })
        {
            var handler = new RecordingHandler((_, _) =>
                Task.FromResult(JsonResponse(
                    Manifest("1.0.1"),
                    mediaType)));
            using var client = new HttpClient(handler);
            var service = CreateService(client);

            VersionCheckResult result = await service.CheckAsync(
                new ReleaseVersion(1, 0, 0),
                CancellationToken.None);

            Assert.AreEqual(
                VersionCheckOutcome.InvalidResponse,
                result.Outcome);
        }
    }

    [TestMethod]
    public async Task CheckAsync_RejectsDeclaredAndActualOversizedResponses()
    {
        var declaredHandler = new RecordingHandler((_, _) =>
        {
            HttpResponseMessage response = JsonResponse(Manifest("1.0.1"));
            response.Content.Headers.ContentLength =
                HttpVersionCheckService.MaximumResponseBytes + 1;
            return Task.FromResult(response);
        });
        using (var client = new HttpClient(declaredHandler))
        {
            VersionCheckResult result = await CreateService(client).CheckAsync(
                new ReleaseVersion(1, 0, 0),
                CancellationToken.None);
            Assert.AreEqual(
                VersionCheckOutcome.InvalidResponse,
                result.Outcome);
        }

        string oversized = new(
            ' ',
            HttpVersionCheckService.MaximumResponseBytes + 1);
        var actualHandler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(oversized)));
        using (var client = new HttpClient(actualHandler))
        {
            VersionCheckResult result = await CreateService(client).CheckAsync(
                new ReleaseVersion(1, 0, 0),
                CancellationToken.None);
            Assert.AreEqual(
                VersionCheckOutcome.InvalidResponse,
                result.Outcome);
        }
    }

    [TestMethod]
    [DataRow(300)]
    [DataRow(301)]
    [DataRow(400)]
    [DataRow(404)]
    [DataRow(429)]
    [DataRow(500)]
    [DataRow(503)]
    public async Task CheckAsync_NonSuccessStatusIsNetworkFailure(int status)
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        VersionCheckResult result = await service.CheckAsync(
            new ReleaseVersion(1, 0, 0),
            CancellationToken.None);

        Assert.AreEqual(VersionCheckOutcome.NetworkFailure, result.Outcome);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CheckAsync_RequestFailureDoesNotRetryOrFallBack()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new HttpRequestException("synthetic proxy failure"));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        VersionCheckResult result = await service.CheckAsync(
            new ReleaseVersion(1, 0, 0),
            CancellationToken.None);

        Assert.AreEqual(VersionCheckOutcome.NetworkFailure, result.Outcome);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CheckAsync_InternalTimeoutIsNetworkFailure()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            throw new AssertFailedException("The timeout should cancel the handler.");
        });
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var service = CreateService(
            client,
            timeout: TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        VersionCheckResult result = await service.CheckAsync(
            new ReleaseVersion(1, 0, 0),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.AreEqual(VersionCheckOutcome.NetworkFailure, result.Outcome);
        Assert.AreEqual(1, handler.Calls);
        Assert.IsLessThan(
            TimeSpan.FromSeconds(5),
            stopwatch.Elapsed,
            "The version check did not enforce its own timeout.");
    }

    [TestMethod]
    public async Task CheckAsync_CallerCancellationPropagatesWithoutRequest()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(Manifest("1.0.1"))));
        using var client = new HttpClient(handler);
        var service = CreateService(client);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.CheckAsync(
                new ReleaseVersion(1, 0, 0),
                cancellation.Token));
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task CheckAsync_CallerCancellationPropagatesDuringRequest()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            throw new AssertFailedException(
                "Caller cancellation should stop the handler.");
        });
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var service = CreateService(client);
        using var cancellation = new CancellationTokenSource();

        Task<VersionCheckResult> pending = service.CheckAsync(
            new ReleaseVersion(1, 0, 0),
            cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await pending);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public void Service_RejectsClientDefaultHeaders()
    {
        using var client = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(Manifest("1.0.1")))));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "synthetic");

        Assert.Throws<ArgumentException>(
            () => new HttpVersionCheckService(
                client,
                new VersionCheckConfiguration(
                    ManifestUri,
                    ReleasePageUri)));
    }

    [TestMethod]
    public void HttpFactory_DisablesCredentialsCookiesRedirectsAndTracing()
    {
        var proxy = new FakeProxy(ManifestUri);
        using SocketsHttpHandler handler =
            PrivacySafeVersionCheckHttpClientFactory.CreateHandler(proxy);

        Assert.IsTrue(handler.UseProxy);
        Assert.IsInstanceOfType<CredentiallessReadOnlyProxy>(handler.Proxy);
        Assert.IsNull(handler.Credentials);
        Assert.IsNull(handler.DefaultProxyCredentials);
        Assert.IsFalse(handler.UseCookies);
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.IsFalse(handler.PreAuthenticate);
        Assert.IsNotNull(handler.ActivityHeadersPropagator);
        Assert.AreEqual(
            VersionCheckConfiguration.DefaultTimeout,
            handler.ConnectTimeout);
    }

    [TestMethod]
    public void HttpFactory_DoesNotReplaceGlobalDefaultProxy()
    {
        IWebProxy original = HttpClient.DefaultProxy;

        using HttpClient client =
            PrivacySafeVersionCheckHttpClientFactory.Create();

        Assert.AreSame(original, HttpClient.DefaultProxy);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.IsEmpty(client.DefaultRequestHeaders);
    }

    [TestMethod]
    public void CredentiallessProxy_DelegatesRoutesWithoutReadingCredentials()
    {
        var systemProxy = new FakeProxy(
            new Uri("http://127.0.0.1:7890"),
            credentialsGetterThrows: true);
        var proxy = new CredentiallessReadOnlyProxy(systemProxy);

        Uri resolved = proxy.GetProxy(ManifestUri);
        bool bypassed = proxy.IsBypassed(ManifestUri);

        Assert.AreEqual(new Uri("http://127.0.0.1:7890"), resolved);
        Assert.IsFalse(bypassed);
        Assert.AreEqual(0, systemProxy.CredentialsReads);
        Assert.IsNull(proxy.Credentials);
        proxy.Credentials = null;
        Assert.Throws<InvalidOperationException>(
            () => proxy.Credentials =
                new NetworkCredential("synthetic", "secret"));
    }

    [TestMethod]
    public void CredentiallessProxy_RejectsCredentialBearingRoute()
    {
        var systemProxy = new FakeProxy(
            new Uri("http://user:secret@127.0.0.1:7890"),
            credentialsGetterThrows: true);
        var proxy = new CredentiallessReadOnlyProxy(systemProxy);

        Assert.Throws<InvalidOperationException>(
            () => proxy.GetProxy(ManifestUri));
        Assert.AreEqual(0, systemProxy.CredentialsReads);
    }

    [TestMethod]
    public void CredentiallessProxy_AllowsSystemDirectRoute()
    {
        var systemProxy = new FakeProxy(
            ManifestUri,
            bypassed: true,
            credentialsGetterThrows: true);
        var proxy = new CredentiallessReadOnlyProxy(systemProxy);

        Assert.AreEqual(ManifestUri, proxy.GetProxy(ManifestUri));
        Assert.IsTrue(proxy.IsBypassed(ManifestUri));
        Assert.AreEqual(0, systemProxy.CredentialsReads);
    }

    private static HttpVersionCheckService CreateService(
        HttpClient client,
        TimeSpan? timeout = null) =>
        new(
            client,
            new VersionCheckConfiguration(
                ManifestUri,
                ReleasePageUri,
                timeout));

    private static string Manifest(string version) =>
        $$"""
          {
            "schemaVersion": 1,
            "product": "AgenTally",
            "channel": "Stable",
            "version": "{{version}}"
          }
          """;

    private static HttpResponseMessage JsonResponse(
        string json,
        string? mediaType = "application/json")
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        if (mediaType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType)
            {
                CharSet = "utf-8"
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            responseFactory)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class FakeProxy : IWebProxy
    {
        private readonly Uri _route;
        private readonly bool _bypassed;
        private readonly bool _credentialsGetterThrows;
        private int _credentialsReads;

        public FakeProxy(
            Uri route,
            bool bypassed = false,
            bool credentialsGetterThrows = false)
        {
            _route = route;
            _bypassed = bypassed;
            _credentialsGetterThrows = credentialsGetterThrows;
        }

        public int CredentialsReads => Volatile.Read(ref _credentialsReads);

        public ICredentials? Credentials
        {
            get
            {
                Interlocked.Increment(ref _credentialsReads);
                return _credentialsGetterThrows
                    ? throw new AssertFailedException(
                        "Proxy credentials must not be read.")
                    : null;
            }
            set => throw new AssertFailedException(
                "Proxy credentials must not be written.");
        }

        public Uri GetProxy(Uri destination) => _route;

        public bool IsBypassed(Uri host) => _bypassed;
    }
}
