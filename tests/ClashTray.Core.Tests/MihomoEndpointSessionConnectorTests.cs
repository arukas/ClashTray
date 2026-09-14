using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoEndpointSessionConnectorTests
{
    [TestMethod]
    public async Task ConnectsOnlyAfterReadOnlyVersionHandshakeAndCarriesProtectedSecret()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        await secretStore.SetAsync("office-secret", "redacted-secret");
        EndpointDescriptor endpoint = CreateEndpoint("office");
        EndpointRecord record = new(endpoint, SecretReference: "office-secret");
        using VersionHandler handler = new("{\"version\":\"v1.19.30\"}");
        EndpointTransportOptionsResolver optionsResolver = new(secretStore, certificateStore);
        MihomoEndpointSessionConnector connector = CreateConnector(
            record,
            optionsResolver,
            handler);

        try
        {
            await using EndpointSession session = await connector.ConnectAsync(
                endpoint,
                generation: 7,
                selectionRevision: 3,
                CancellationToken.None);

            Assert.AreEqual(EndpointSessionState.Connected, session.Handshake.State);
            Assert.AreEqual("v1.19.30", session.Handshake.Version);
            Assert.AreEqual(EndpointCapabilityDefaults.Remote, session.Capabilities);
            Assert.AreEqual("Bearer redacted-secret", handler.Authorization);
            Assert.AreEqual("/version", handler.Path);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InvalidVersionResponseIsRejectedAsIncompatibleWithoutRetryClassification()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointDescriptor endpoint = CreateEndpoint("office");
        EndpointRecord record = new(endpoint);
        using VersionHandler handler = new("{\"name\":\"not-mihomo\"}");
        EndpointTransportOptionsResolver optionsResolver = new(secretStore, certificateStore);
        MihomoEndpointSessionConnector connector = CreateConnector(
            record,
            optionsResolver,
            handler);

        try
        {
            EndpointSessionConnectException exception = await Assert.ThrowsExactlyAsync<EndpointSessionConnectException>(
                () => connector.ConnectAsync(endpoint, 1, 1, CancellationToken.None));

            Assert.AreEqual(EndpointSessionState.Incompatible, exception.FailureState);
            Assert.IsFalse(exception.IsTransient);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task MissingEndpointRecordFailsBeforeCreatingTransport()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        EndpointTransportOptionsResolver optionsResolver = new(
            new EndpointSecretStore(paths),
            new EndpointCertificateStore(paths));
        EndpointDescriptor endpoint = CreateEndpoint("office");
        int transportCalls = 0;
        MihomoEndpointSessionConnector connector = new(
            (_, _) => Task.FromResult<EndpointRecord?>(null),
            optionsResolver,
            (target, options) =>
            {
                Interlocked.Increment(ref transportCalls);
                throw new InvalidOperationException("transport must not be created");
            });

        try
        {
            EndpointSessionConnectException exception = await Assert.ThrowsExactlyAsync<EndpointSessionConnectException>(
                () => connector.ConnectAsync(endpoint, 1, 1, CancellationToken.None));

            Assert.AreEqual(EndpointSessionState.Failed, exception.FailureState);
            Assert.AreEqual(0, Volatile.Read(ref transportCalls));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static MihomoEndpointSessionConnector CreateConnector(
        EndpointRecord record,
        EndpointTransportOptionsResolver optionsResolver,
        VersionHandler handler) =>
        new(
            (id, _) => Task.FromResult<EndpointRecord?>(id == record.Descriptor.Id ? record : null),
            optionsResolver,
            (target, options) => CreateTransport(target, options, handler));

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "EndpointSession owns the transport returned by the connector until the test session is disposed.")]
    private static EndpointTransport CreateTransport(
        EndpointDescriptor endpoint,
        EndpointTransportOptions options,
        VersionHandler handler)
    {
        HttpClient client = new(handler, disposeHandler: true)
        {
            BaseAddress = endpoint.BaseUri
        };
        if (!string.IsNullOrEmpty(options.Secret))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Secret);
        }

        Uri webSocketUri = new UriBuilder(endpoint.BaseUri)
        {
            Scheme = endpoint.BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        }.Uri;
        return new EndpointTransport(
            endpoint,
            endpoint.BaseUri,
            webSocketUri,
            client,
            options.Secret.Length == 0 ? null : $"Bearer {options.Secret}",
            customCaCertificate: null,
            bypassesSystemProxy: true);
    }

    private static EndpointDescriptor CreateEndpoint(string id) =>
        EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId(id),
            id,
            new Uri($"https://{id}.example.test"));

    private static string CreateRoot() => Path.Combine(
        Path.GetTempPath(),
        "ClashTrayTests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class VersionHandler : HttpMessageHandler
    {
        private readonly string _body;

        public VersionHandler(string body) => _body = body;

        public string? Authorization { get; private set; }

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Authorization = request.Headers.Authorization?.ToString();
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
