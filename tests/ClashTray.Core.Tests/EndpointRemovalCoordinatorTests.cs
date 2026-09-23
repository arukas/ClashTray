using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointRemovalCoordinatorTests
{
    [TestMethod]
    public async Task MetadataDeleteFailureKeepsEndpointSecretAndCertificate()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator provisioning =
            new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("edge"),
                "Edge",
                new Uri("https://controller.invalid"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        using X509Certificate2 ca = CreateCaCertificate();
        byte[] caBytes = ca.Export(X509ContentType.Cert);
        EndpointRecord record = await provisioning.ProvisionAsync(
            descriptor,
            "synthetic-review-secret",
            caBytes,
            insecureHttpAcknowledgedAtUtc: null);
        EndpointRemovalCoordinator removal =
            new(endpointStore, secretStore, certificateStore);

        try
        {
            await using FileStream blocker = new(
                paths.EndpointStoreFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            Exception? failure = null;
            try
            {
                await removal.RemoveAsync(descriptor.Id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure, "The locked endpoint store should reject metadata deletion.");
            Assert.IsTrue((await endpointStore.LoadAsync()).Endpoints.Any(
                endpoint => endpoint.Descriptor.Id == descriptor.Id));
            Assert.AreEqual("synthetic-review-secret", await secretStore.GetAsync(record.SecretReference!));
            CollectionAssert.AreEqual(caBytes, await certificateStore.GetAsync(record.CertificateReference!));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static X509Certificate2 CreateCaCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=ClashTray Removal Test CA",
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
}