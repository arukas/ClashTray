using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointDescriptorValidatorTests
{
    [TestMethod]
    public void AcceptsLoopbackSystemTrustAndCustomHttpsDescriptors()
    {
        EndpointDescriptor local = ControllerEndpointFactory.CreateLocal(9090);
        EndpointDescriptor https = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("secure"),
                "Secure",
                new Uri("https://controller.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };

        EndpointDescriptorValidator.ValidateForActiveSession(local);
        EndpointDescriptorValidator.ValidateForActiveSession(https);

        Assert.AreEqual(EndpointKind.Local, local.Kind);
        Assert.AreEqual(EndpointTransportSecurity.HttpsCustomCertificate, https.Security);
    }

    [TestMethod]
    public void AcceptsExplicitHttpOnlyWhenSecurityMetadataConfirmsIt()
    {
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("lab"),
            "Lab",
            new Uri("http://controller.example.test"),
            allowExplicitHttp: true);

        EndpointDescriptorValidator.ValidateForActiveSession(endpoint);
    }

    [TestMethod]
    public void RejectsRemoteSecurityMetadataThatDoesNotMatchUri()
    {
        EndpointDescriptor httpsMarkedHttp = new(
            new EndpointId("secure"),
            EndpointKind.Remote,
            "Secure",
            new Uri("https://controller.example.test/"),
            EndpointTransportSecurity.HttpExplicitlyConfirmed);
        EndpointDescriptor httpMarkedHttps = new(
            new EndpointId("insecure"),
            EndpointKind.Remote,
            "Insecure",
            new Uri("http://controller.example.test/"),
            EndpointTransportSecurity.HttpsSystemTrust);
        EndpointDescriptor remoteMarkedLoopback = httpsMarkedHttp with
        {
            Security = EndpointTransportSecurity.Loopback
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => EndpointDescriptorValidator.ValidateForActiveSession(httpsMarkedHttp));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => EndpointDescriptorValidator.ValidateForActiveSession(httpMarkedHttps));
        Assert.ThrowsExactly<ArgumentException>(
            () => EndpointDescriptorValidator.ValidateForActiveSession(remoteMarkedLoopback));
    }

    [TestMethod]
    public void RejectsNonLoopbackLocalDescriptorsAndDisabledTargets()
    {
        EndpointDescriptor localHttps = new(
            EndpointId.Local,
            EndpointKind.Local,
            "Local",
            new Uri("https://controller.example.test/"),
            EndpointTransportSecurity.HttpsSystemTrust);
        EndpointDescriptor disabledRemote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("disabled"),
            "Disabled",
            new Uri("https://controller.example.test"),
            isEnabled: false);

        Assert.ThrowsExactly<ArgumentException>(
            () => EndpointDescriptorValidator.ValidateForActiveSession(localHttps));
        Assert.ThrowsExactly<ArgumentException>(
            () => EndpointDescriptorValidator.ValidateForActiveSession(disabledRemote));
    }
}
