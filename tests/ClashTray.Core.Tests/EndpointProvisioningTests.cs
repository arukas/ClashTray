using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointProvisioningTests
{
    [TestMethod]
    public async Task ProvisioningProtectsSecretAndStoresCustomCaForPrivateHttps()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("private"),
                "Private",
                new Uri("https://private.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        using X509Certificate2 ca = CreateCaCertificate();

        try
        {
            EndpointRecord record = await coordinator.ProvisionAsync(
                descriptor,
                "controller-secret",
                ca.Export(X509ContentType.Cert),
                null);

            string secretFile = await File.ReadAllTextAsync(paths.EndpointSecretsFile);
            Assert.IsFalse(secretFile.Contains("controller-secret", StringComparison.Ordinal));
            Assert.AreEqual("controller-secret", await secretStore.GetAsync(record.SecretReference!));
            Assert.IsNotNull(await certificateStore.GetAsync(record.CertificateReference!));
            EndpointRecord persisted = (await endpointStore.LoadAsync()).Endpoints.Single();
            Assert.AreEqual(record.SecretReference, persisted.SecretReference);
            Assert.AreEqual(record.CertificateReference, persisted.CertificateReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ProvisioningAllowsPublicHttpsWithoutCustomCa()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointProvisioningCoordinator coordinator = new(
            new EndpointStore(paths),
            new EndpointSecretStore(paths),
            new EndpointCertificateStore(paths));
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("public"),
            "Public",
            new Uri("https://public.example.test"));

        try
        {
            EndpointRecord record = await coordinator.ProvisionAsync(
                descriptor,
                "controller-secret",
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null);

            Assert.IsNotNull(record.SecretReference);
            Assert.IsNull(record.CertificateReference);
            Assert.IsNull(record.InsecureHttpAcknowledgedAtUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ProvisioningRejectsUnacknowledgedHttpAndMissingCustomCa()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointProvisioningCoordinator coordinator = new(
            new EndpointStore(paths),
            new EndpointSecretStore(paths),
            new EndpointCertificateStore(paths));
        EndpointDescriptor http = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("http"),
            "HTTP",
            new Uri("http://controller.example.test"),
            allowExplicitHttp: true);
        EndpointDescriptor custom = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("custom"),
                "Custom",
                new Uri("https://controller.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => coordinator.ProvisionAsync(
                http,
                null,
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => coordinator.ProvisionAsync(
                custom,
                null,
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdatingEndpointPreservesSecretAndRemovesCaWhenTrustModeChanges()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor custom = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("office"),
                "Office",
                new Uri("https://office.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        using X509Certificate2 ca = CreateCaCertificate();

        try
        {
            EndpointRecord created = await coordinator.ProvisionAsync(
                custom,
                "controller-secret",
                ca.Export(X509ContentType.Cert),
                null);
            EndpointRecord updated = await coordinator.UpdateAsync(
                new EndpointId("office"),
                EndpointUriNormalizer.CreateRemoteDescriptor(
                    new EndpointId("office"),
                    "Office renamed",
                    new Uri("https://new-office.example.test")),
                secret: null,
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null);

            Assert.AreEqual("Office renamed", updated.Descriptor.DisplayName);
            Assert.AreEqual("https://new-office.example.test/", updated.Descriptor.BaseUri.AbsoluteUri);
            Assert.AreEqual(created.SecretReference, updated.SecretReference);
            Assert.IsNull(updated.CertificateReference);
            Assert.AreEqual("controller-secret", await secretStore.GetAsync(updated.SecretReference!));
            Assert.IsNull(await certificateStore.GetAsync(created.CertificateReference!));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdatingCustomHttpsWithoutNewCaPreservesExistingCa()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor custom = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("office"),
                "Office",
                new Uri("https://office.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        using X509Certificate2 ca = CreateCaCertificate();
        byte[] certificateBytes = ca.Export(X509ContentType.Cert);

        try
        {
            EndpointRecord created = await coordinator.ProvisionAsync(
                custom,
                null,
                certificateBytes,
                null);
            EndpointRecord updated = await coordinator.UpdateAsync(
                created.Descriptor.Id,
                custom with { DisplayName = "Office updated" },
                secret: null,
                customCaCertificate: null,
                insecureHttpAcknowledgedAtUtc: null);

            Assert.AreEqual(created.CertificateReference, updated.CertificateReference);
            CollectionAssert.AreEqual(
                certificateBytes,
                await certificateStore.GetAsync(updated.CertificateReference!));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static X509Certificate2 CreateCaCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=ClashTray Provisioning CA",
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
}
