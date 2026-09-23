using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointCredentialRevisionTests
{
    [TestMethod]
    public async Task SecretRotationDisconnectsTheActiveSession()
    {
        await VerifyAuthenticationUpdateInvalidatesSessionAsync(
            EndpointTransportSecurity.HttpsSystemTrust,
            initialSecret: "secret-before",
            initialCertificate: null,
            updatedSecret: "secret-after",
            updatedCertificate: null);
    }

    [TestMethod]
    public async Task ClearingSecretDisconnectsTheActiveSession()
    {
        await VerifyAuthenticationUpdateInvalidatesSessionAsync(
            EndpointTransportSecurity.HttpsSystemTrust,
            initialSecret: "secret-before",
            initialCertificate: null,
            updatedSecret: string.Empty,
            updatedCertificate: null);
    }

    [TestMethod]
    public async Task CustomCaRotationDisconnectsTheActiveSession()
    {
        using X509Certificate2 initialCa = CreateCaCertificate("ClashTray R7 CA A");
        using X509Certificate2 updatedCa = CreateCaCertificate("ClashTray R7 CA B");
        await VerifyAuthenticationUpdateInvalidatesSessionAsync(
            EndpointTransportSecurity.HttpsCustomCertificate,
            initialSecret: null,
            initialCertificate: initialCa.Export(X509ContentType.Cert),
            updatedSecret: null,
            updatedCertificate: updatedCa.Export(X509ContentType.Cert));
    }

    [TestMethod]
    public async Task DisplayNameEditFollowsExistingDescriptorChangePolicyAndDisconnects()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        StaticConnector connector = new();
        ClashTrayRuntime runtime = CreateRuntime(paths, connector);
        EndpointDescriptor descriptor = CreateDescriptor(EndpointTransportSecurity.HttpsSystemTrust);

        try
        {
            await runtime.ProvisionRemoteEndpointAsync(descriptor, null, null, null);
            EndpointSession? session = await runtime.SelectEndpointAsync(descriptor.Id);
            Assert.IsNotNull(session);

            await runtime.UpdateRemoteEndpointAsync(
                descriptor.Id,
                descriptor with { DisplayName = "Office renamed" },
                secret: null,
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null);

            Assert.AreEqual(EndpointId.Local, runtime.EndpointSessionStatus.Endpoint.Id);
            Assert.AreEqual(EndpointSessionState.Disconnected, runtime.EndpointSessionStatus.State);
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.BuildWebSocketUri("/logs"));
        }
        finally
        {
            await runtime.DisposeAsync();
            DeleteRoot(root);
        }
    }

    private static async Task VerifyAuthenticationUpdateInvalidatesSessionAsync(
        EndpointTransportSecurity security,
        string? initialSecret,
        byte[]? initialCertificate,
        string? updatedSecret,
        byte[]? updatedCertificate)
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        StaticConnector connector = new();
        ClashTrayRuntime runtime = CreateRuntime(paths, connector);
        EndpointDescriptor descriptor = CreateDescriptor(security);
        ReadOnlyMemory<byte>? initialCa = null;
        if (initialCertificate is not null)
        {
            initialCa = new ReadOnlyMemory<byte>(initialCertificate);
        }

        ReadOnlyMemory<byte>? updatedCa = null;
        if (updatedCertificate is not null)
        {
            updatedCa = new ReadOnlyMemory<byte>(updatedCertificate);
        }

        try
        {
            await runtime.ProvisionRemoteEndpointAsync(descriptor, initialSecret, initialCa, null);
            EndpointSession? session = await runtime.SelectEndpointAsync(descriptor.Id);
            Assert.IsNotNull(session);
            long generationBeforeUpdate = runtime.EndpointSessionStatus.Generation;

            await runtime.UpdateRemoteEndpointAsync(
                descriptor.Id,
                descriptor,
                updatedSecret,
                updatedCa,
                insecureHttpAcknowledgedAtUtc: null);

            Assert.AreEqual(EndpointId.Local, runtime.EndpointSessionStatus.Endpoint.Id);
            Assert.AreEqual(EndpointSessionState.Disconnected, runtime.EndpointSessionStatus.State);
            Assert.AreEqual(generationBeforeUpdate + 1, runtime.EndpointSessionStatus.Generation);
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.BuildWebSocketUri("/logs"));
            Assert.AreEqual(1, connector.CallCount, "Authentication changes invalidate the session without silently reconnecting.");
        }
        finally
        {
            await runtime.DisposeAsync();
            DeleteRoot(root);
        }
    }

    private static ClashTrayRuntime CreateRuntime(AppPaths paths, StaticConnector connector) => new(
        paths,
        null,
        null,
        null,
        null,
        null,
        null,
        connector,
        (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
        remoteLogStreamRunner: (_, _, _) => Task.CompletedTask);

    private static EndpointDescriptor CreateDescriptor(EndpointTransportSecurity security) =>
        EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("office"),
                "Office",
                new Uri("https://office.example.test"))
            with { Security = security };

    private static X509Certificate2 CreateCaCertificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={subject}",
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

    private sealed class StaticConnector : IEndpointSessionConnector
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "EndpointSession takes ownership of the transport and disposes it asynchronously.")]
        public Task<EndpointSession> ConnectAsync(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            HttpClient client = new(new EmptyHandler()) { BaseAddress = endpoint.BaseUri };
            Uri webSocketUri = new UriBuilder(endpoint.BaseUri)
            {
                Scheme = endpoint.BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? "wss"
                    : "ws"
            }.Uri;
            EndpointTransport transport = new(
                endpoint,
                endpoint.BaseUri,
                webSocketUri,
                client,
                authorizationValue: null,
                customCaCertificate: null,
                bypassesSystemProxy: true);
            return Task.FromResult(new EndpointSession(
                transport,
                EndpointCapabilityDefaults.Remote,
                generation,
                selectionRevision));
        }
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
    }
}