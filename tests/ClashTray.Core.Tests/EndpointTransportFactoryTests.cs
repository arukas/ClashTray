using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointTransportFactoryTests
{
    [TestMethod]
    public void HttpsTransportSharesNormalizedOriginAndBearerAuthorization()
    {
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("HTTPS://Mihomo.Example.Test:443"));

        using EndpointTransport transport = EndpointTransportFactory.Create(
            endpoint,
            new EndpointTransportOptions("test-secret"));

        Assert.AreEqual("https://mihomo.example.test/", transport.BaseUri.AbsoluteUri);
        Assert.AreEqual("wss://mihomo.example.test/", transport.WebSocketUri.AbsoluteUri);
        Assert.AreEqual("Bearer", transport.HttpClient.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.AreEqual("test-secret", transport.HttpClient.DefaultRequestHeaders.Authorization?.Parameter);
        Assert.IsTrue(transport.BypassesSystemProxy);
        Assert.AreEqual(
            "wss://mihomo.example.test/logs?level=debug",
            transport.BuildWebSocketUri("/logs?level=debug").AbsoluteUri);

        using ClientWebSocket socket = transport.CreateWebSocket();
        Assert.AreEqual(WebSocketState.None, socket.State);
        Assert.IsNull(socket.Options.Proxy);
    }

    [TestMethod]
    public void ApiClientUsesEndpointTransportForWebSocketPolicy()
    {
        using X509Certificate2 ca = CreateCaCertificate("ClashTray WebSocket CA");
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("private"),
                "Private",
                new Uri("https://controller.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };

        using EndpointTransport transport = EndpointTransportFactory.Create(
            endpoint,
            new EndpointTransportOptions("websocket-secret", ca));
        MihomoApiClient api = new(
            transport.HttpClient,
            transport.BaseUri,
            string.Empty,
            webSocketFactory: transport.CreateWebSocket,
            webSocketUriBuilder: transport.BuildWebSocketUri);

        using ClientWebSocket socket = api.CreateWebSocket();

        Assert.IsNull(socket.Options.Proxy);
        Assert.IsNotNull(socket.Options.RemoteCertificateValidationCallback);
        Assert.AreEqual(
            "wss://controller.example.test/logs",
            api.BuildWebSocketUri("/logs").AbsoluteUri);
    }

    [TestMethod]
    public void ExplicitHttpTransportUsesWsAtControllerOrigin()
    {
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("lab"),
            "Lab",
            new Uri("http://mihomo.example.test"),
            allowExplicitHttp: true);

        using EndpointTransport transport = EndpointTransportFactory.Create(endpoint);

        Assert.AreEqual(EndpointTransportSecurity.HttpExplicitlyConfirmed, endpoint.Security);
        Assert.AreEqual("http://mihomo.example.test/", transport.BaseUri.AbsoluteUri);
        Assert.AreEqual("ws://mihomo.example.test/", transport.WebSocketUri.AbsoluteUri);
        Assert.IsTrue(transport.BypassesSystemProxy);
        Assert.AreEqual(
            "ws://mihomo.example.test/logs",
            transport.BuildWebSocketUri("logs").AbsoluteUri);
    }

    [TestMethod]
    public void CustomCaTransportRequiresCaAndConfiguresBothTransports()
    {
        using X509Certificate2 ca = CreateCaCertificate("ClashTray Test CA");
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("private"),
                "Private",
                new Uri("https://controller.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };

        using EndpointTransport transport = EndpointTransportFactory.Create(
            endpoint,
            new EndpointTransportOptions(CustomCaCertificate: ca));
        using ClientWebSocket socket = transport.CreateWebSocket();
        using X509Certificate2 invalidUsageCa = CreateCertificateAuthority(
            "ClashTray Invalid Usage CA",
            X509KeyUsageFlags.DigitalSignature);

        Assert.AreEqual("https://controller.example.test/", transport.BaseUri.AbsoluteUri);
        Assert.AreEqual("wss://controller.example.test/", transport.WebSocketUri.AbsoluteUri);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => EndpointTransportFactory.Create(endpoint));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => EndpointTransportFactory.Create(
                endpoint,
                new EndpointTransportOptions(CustomCaCertificate: invalidUsageCa)));
        Assert.AreEqual(WebSocketState.None, socket.State);
    }

    [TestMethod]
    public void FactoryRejectsLocalAndMismatchedTransportSecurity()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => EndpointTransportFactory.Create(ControllerEndpointFactory.CreateLocal(9090)));

        EndpointDescriptor httpsEndpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("secure"),
            "Secure",
            new Uri("https://controller.example.test"));
        using X509Certificate2 ca = CreateCaCertificate("ClashTray Test CA");
        Assert.ThrowsExactly<InvalidOperationException>(() => EndpointTransportFactory.Create(
            httpsEndpoint,
            new EndpointTransportOptions(CustomCaCertificate: ca)));

        EndpointDescriptor httpWithWrongSecurity = httpsEndpoint with
        {
            BaseUri = new Uri("http://controller.example.test/"),
            Security = EndpointTransportSecurity.HttpsSystemTrust
        };
        Assert.ThrowsExactly<InvalidOperationException>(
            () => EndpointTransportFactory.Create(httpWithWrongSecurity));
    }

    [TestMethod]
    public void CustomCaValidatorAcceptsTrustedChainAndRejectsWrongOrMismatchedCertificates()
    {
        using X509Certificate2 ca = CreateCaCertificate("ClashTray Test CA");
        using X509Certificate2 server = CreateServerCertificate(ca, "controller.example.test");
        using X509Certificate2 wrongCa = CreateCaCertificate("Wrong CA");

        Assert.IsTrue(EndpointCertificateValidator.Validate(
            server,
            chain: null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            ca));
        Assert.IsFalse(EndpointCertificateValidator.Validate(
            server,
            chain: null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            wrongCa));
        Assert.IsFalse(EndpointCertificateValidator.Validate(
            server,
            chain: null,
            SslPolicyErrors.None,
            wrongCa));
        Assert.IsTrue(EndpointCertificateValidator.Validate(
            server,
            chain: null,
            SslPolicyErrors.None,
            ca));
        Assert.IsFalse(EndpointCertificateValidator.Validate(
            server,
            chain: null,
            SslPolicyErrors.RemoteCertificateNameMismatch,
            ca));
    }

    [TestMethod]
    public void WebSocketPathMustBeRelative()
    {
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://controller.example.test"));
        using EndpointTransport transport = EndpointTransportFactory.Create(endpoint);

        Assert.ThrowsExactly<ArgumentException>(
            () => transport.BuildWebSocketUri("https://other.example.test/logs"));
    }

    [TestMethod]
    public async Task OptionsResolverLoadsSecretAndCustomCaWithoutConnecting()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointTransportOptionsResolver resolver = new(secretStore, certificateStore);
        using X509Certificate2 ca = CreateCaCertificate("ClashTray Resolver CA");
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("private"),
                "Private",
                new Uri("https://controller.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };

        try
        {
            await secretStore.SetAsync("private-secret", "controller-secret");
            await certificateStore.SetAsync(
                "private-ca",
                ca.Export(X509ContentType.Cert));
            EndpointRecord record = new(
                descriptor,
                SecretReference: "private-secret",
                CertificateReference: "private-ca");

            using EndpointTransportOptionsLease lease = await resolver.ResolveAsync(record);

            Assert.AreEqual("controller-secret", lease.Options.Secret);
            Assert.IsNotNull(lease.Options.CustomCaCertificate);
            Assert.AreEqual(ca.Thumbprint, lease.Options.CustomCaCertificate!.Thumbprint);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task OptionsResolverUsesSystemTrustWithoutLoadingACustomCa()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointTransportOptionsResolver resolver = new(secretStore, certificateStore);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("public"),
            "Public",
            new Uri("https://controller.example.test"));

        try
        {
            await secretStore.SetAsync("public-secret", "controller-secret");
            EndpointTransportOptionsLease lease = await resolver.ResolveAsync(new EndpointRecord(
                descriptor,
                SecretReference: "public-secret"));
            using (lease)
            {
                Assert.AreEqual("controller-secret", lease.Options.Secret);
                Assert.IsNull(lease.Options.CustomCaCertificate);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task OptionsResolverRejectsUnacknowledgedExplicitHttp()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointTransportOptionsResolver resolver = new(
            new EndpointSecretStore(paths),
            new EndpointCertificateStore(paths));
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("lab"),
            "Lab",
            new Uri("http://controller.example.test"),
            allowExplicitHttp: true);

        try
        {
            EndpointSessionConnectException exception = await Assert.ThrowsExactlyAsync<EndpointSessionConnectException>(
                () => resolver.ResolveAsync(new EndpointRecord(descriptor)));

            Assert.AreEqual(EndpointSessionState.Failed, exception.FailureState);
            StringAssert.Contains(exception.Message, "明文", StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
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

    private static X509Certificate2 CreateCaCertificate(string commonName)
        => CreateCertificateAuthority(
            commonName,
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign);

    private static X509Certificate2 CreateCertificateAuthority(
        string commonName,
        X509KeyUsageFlags keyUsage)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            keyUsage,
            critical: true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private static X509Certificate2 CreateServerCertificate(
        X509Certificate2 ca,
        string dnsName)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={dnsName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        SubjectAlternativeNameBuilder subjectAlternativeName = new();
        subjectAlternativeName.AddDnsName(dnsName);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());
        DateTimeOffset issuerNotBefore = new DateTimeOffset(ca.NotBefore).ToUniversalTime();
        DateTimeOffset issuerNotAfter = new DateTimeOffset(ca.NotAfter).ToUniversalTime();
        return request.Create(
            ca,
            issuerNotBefore.AddSeconds(1),
            issuerNotAfter.AddSeconds(-1),
            [1, 2, 3, 4]);
    }
}
