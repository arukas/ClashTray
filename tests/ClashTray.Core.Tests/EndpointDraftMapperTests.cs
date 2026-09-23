using ClashTray.Contracts;
using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointDraftMapperTests
{
    private static readonly EndpointId TestEndpointId = new("draft-endpoint");
    private static readonly DateTimeOffset AcknowledgedAt = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CustomHttpsChoiceMapsToCustomTrustForCreateAndEdit()
    {
        EndpointDraftSubmission created = EndpointDraftMapper.CreateSubmission(
            TestEndpointId,
            "Private",
            new Uri("https://private.example.test"),
            EndpointDraftMapper.ParseTransportChoice("https-custom"),
            httpRiskConfirmed: false,
            secret: null,
            customCaPem: "-----BEGIN CERTIFICATE-----\nsynthetic-ca\n-----END CERTIFICATE-----",
            isEditing: false,
            AcknowledgedAt);
        EndpointDraftSubmission edited = EndpointDraftMapper.CreateSubmission(
            TestEndpointId,
            "Private updated",
            new Uri("https://private.example.test"),
            EndpointDraftMapper.ParseTransportChoice("https-custom"),
            httpRiskConfirmed: false,
            secret: null,
            customCaPem: null,
            isEditing: true,
            AcknowledgedAt);

        Assert.AreEqual(EndpointTransportSecurity.HttpsCustomCertificate, created.Descriptor.Security);
        Assert.IsNotNull(created.CustomCaCertificate);
        Assert.AreEqual(EndpointTransportSecurity.HttpsCustomCertificate, edited.Descriptor.Security);
        Assert.IsFalse(edited.CustomCaCertificate.HasValue, "An empty edit CA means retain the existing CA.");
    }

    [TestMethod]
    public void SystemHttpsAndExplicitHttpChoicesKeepTheirSecurityAndAcknowledgement()
    {
        foreach (bool isEditing in new[] { false, true })
        {
            EndpointDraftSubmission systemHttps = EndpointDraftMapper.CreateSubmission(
                TestEndpointId,
                "Public",
                new Uri("https://public.example.test"),
                EndpointDraftMapper.ParseTransportChoice("https-system"),
                httpRiskConfirmed: false,
                secret: null,
                customCaPem: null,
                isEditing,
                AcknowledgedAt);
            EndpointDraftSubmission explicitHttp = EndpointDraftMapper.CreateSubmission(
                TestEndpointId,
                "HTTP endpoint",
                new Uri("http://controller.example.test"),
                EndpointDraftMapper.ParseTransportChoice("http-explicit"),
                httpRiskConfirmed: true,
                secret: null,
                customCaPem: null,
                isEditing,
                AcknowledgedAt);

            Assert.AreEqual(EndpointTransportSecurity.HttpsSystemTrust, systemHttps.Descriptor.Security);
            Assert.IsNull(systemHttps.InsecureHttpAcknowledgedAtUtc);
            Assert.AreEqual(EndpointTransportSecurity.HttpExplicitlyConfirmed, explicitHttp.Descriptor.Security);
            Assert.AreEqual(AcknowledgedAt, explicitHttp.InsecureHttpAcknowledgedAtUtc);
        }
    }

    [TestMethod]
    public void TransportChoiceRequiresMatchingUriAndHttpConfirmation()
    {
        Assert.ThrowsExactly<ArgumentException>(() => EndpointDraftMapper.CreateSubmission(
            TestEndpointId,
            "Private",
            new Uri("https://private.example.test"),
            EndpointDraftMapper.ParseTransportChoice("http-explicit"),
            httpRiskConfirmed: false,
            secret: null,
            customCaPem: null,
            isEditing: false,
            AcknowledgedAt));
        Assert.ThrowsExactly<ArgumentException>(() => EndpointDraftMapper.CreateSubmission(
            TestEndpointId,
            "HTTP endpoint",
            new Uri("http://controller.example.test"),
            EndpointDraftMapper.ParseTransportChoice("https-system"),
            httpRiskConfirmed: false,
            secret: null,
            customCaPem: null,
            isEditing: false,
            AcknowledgedAt));
    }
}