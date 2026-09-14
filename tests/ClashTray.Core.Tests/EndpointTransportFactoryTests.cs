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
            new Uri("HTTPS://Mihomo.Example.Test:443/controller"));

        using EndpointTransport transport = EndpointTransportFactory.Create(
            endpoint,
            new EndpointTransportOptions("test-secret"));

        Assert.AreEqual("https://mihomo.example.test/controller/", transport.BaseUri.AbsoluteUri);
        Assert.AreEqual("wss://mihomo.example.test/controller/", transport.WebSocketUri.AbsoluteUri);
        Assert.AreEqual("Bearer", transport.HttpClient.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.AreEqual("test-secret", transport.HttpClient.DefaultRequestHeaders.Authorization?.Parameter);
        Assert.IsTrue(transport.BypassesSystemProxy);
        Assert.AreEqual(
            "wss://mihomo.example.test/controller/logs?level=debug",
            transport.BuildWebSocketUri("/logs?level=debug").AbsoluteUri);

        using ClientWebSocket socket = transport.CreateWebSocket();
        Assert.AreEqual(WebSocketState.None, socket.State);
    }

    [TestMethod]
    public void ExplicitHttpTransportUsesWsAndPreservesBasePath()
    {
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("lab"),
            "Lab",
            new Uri("http://mihomo.example.test/controller"),
            allowExplicitHttp: true);

        using EndpointTransport transport = EndpointTransportFactory.Create(endpoint);

        Assert.AreEqual(EndpointTransportSecurity.HttpExplicitlyConfirmed, endpoint.Security);
        Assert.AreEqual("http://mihomo.example.test/controller/", transport.BaseUri.AbsoluteUri);
        Assert.AreEqual("ws://mihomo.example.test/controller/", transport.WebSocketUri.AbsoluteUri);
        Assert.IsTrue(transport.BypassesSystemProxy);
        Assert.AreEqual(
            "ws://mihomo.example.test/controller/logs",
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

        Assert.AreEqual("https://controller.example.test/", transport.BaseUri.AbsoluteUri);
        Assert.AreEqual("wss://controller.example.test/", transport.WebSocketUri.AbsoluteUri);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => EndpointTransportFactory.Create(endpoint));
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
