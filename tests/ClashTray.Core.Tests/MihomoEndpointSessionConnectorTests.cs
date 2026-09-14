using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public async Task ConnectorWiresEndpointTransportIntoApiWebSocketFactory()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        using X509Certificate2 ca = CreateCaCertificate("ClashTray Connector CA");
        await certificateStore.SetAsync("office-ca", ca.Export(X509ContentType.Cert));

        EndpointDescriptor endpoint = CreateEndpoint("office") with
        {
            Security = EndpointTransportSecurity.HttpsCustomCertificate
        };
        EndpointRecord record = new(endpoint, CertificateReference: "office-ca");
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
                generation: 11,
                selectionRevision: 5,
                CancellationToken.None);

            using ClientWebSocket socket = session.Api.CreateWebSocket();

            Assert.IsNull(socket.Options.Proxy);
            Assert.IsNotNull(socket.Options.RemoteCertificateValidationCallback);
            Assert.AreEqual(
                "wss://office.example.test/logs",
                session.Api.BuildWebSocketUri("/logs").AbsoluteUri);
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
            customCaCertificate: options.CustomCaCertificate is { } ca
                ? X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert))
                : null,
            bypassesSystemProxy: true);
    }

    private static X509Certificate2 CreateCaCertificate(string commonName)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
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
