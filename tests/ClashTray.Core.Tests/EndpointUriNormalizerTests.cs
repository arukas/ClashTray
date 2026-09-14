using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointUriNormalizerTests
{
    [TestMethod]
    public void NormalizesHttpsHostDefaultPortAndControllerOrigin()
    {
        Uri normalized = EndpointUriNormalizer.NormalizeBaseUri(" HTTPS://Example.COM:443/ ");

        Assert.AreEqual("https://example.com/", normalized.AbsoluteUri);
        Assert.IsTrue(normalized.IsDefaultPort);
    }

    [TestMethod]
    public void CreatesRemoteDescriptorWithSystemTrustSecurity()
    {
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office controller",
            new Uri("https://mihomo.example.test"));

        Assert.AreEqual(EndpointKind.Remote, descriptor.Kind);
        Assert.AreEqual(EndpointTransportSecurity.HttpsSystemTrust, descriptor.Security);
        Assert.AreEqual("office", descriptor.Id.Value);
        Assert.AreEqual("https://mihomo.example.test/", descriptor.BaseUri.AbsoluteUri);
    }

    [TestMethod]
    public void RejectsReverseProxyPathPrefixes()
    {
        Assert.ThrowsExactly<UriFormatException>(
            () => EndpointUriNormalizer.NormalizeBaseUri("https://mihomo.example.test/controller"));
    }

    [TestMethod]
    public void HttpRequiresExplicitConfirmationAndIsMarkedClearly()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => EndpointUriNormalizer.NormalizeBaseUri("http://mihomo.example.test"));

        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("lab"),
            "Lab",
            new Uri("http://mihomo.example.test:80"),
            allowExplicitHttp: true);

        Assert.AreEqual(EndpointTransportSecurity.HttpExplicitlyConfirmed, descriptor.Security);
        Assert.AreEqual("http://mihomo.example.test/", descriptor.BaseUri.AbsoluteUri);
    }

    [TestMethod]
    public void RejectsCredentialsQueryFragmentAndUnsupportedSchemes()
    {
        Assert.ThrowsExactly<UriFormatException>(
            () => EndpointUriNormalizer.NormalizeBaseUri("https://user:password@example.test"));
        Assert.ThrowsExactly<UriFormatException>(
            () => EndpointUriNormalizer.NormalizeBaseUri("https://example.test/?token=secret"));
        Assert.ThrowsExactly<UriFormatException>(
            () => EndpointUriNormalizer.NormalizeBaseUri("https://example.test/#secret"));
        Assert.ThrowsExactly<UriFormatException>(
            () => EndpointUriNormalizer.NormalizeBaseUri("ftp://example.test"));
    }

    [TestMethod]
    public void RejectsOversizedDisplayNamesAndInvalidEndpointIdentifiers()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId(" "),
                "name",
                new Uri("https://example.test")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("id"),
                new string('x', 129),
                new Uri("https://example.test")));
    }
}
