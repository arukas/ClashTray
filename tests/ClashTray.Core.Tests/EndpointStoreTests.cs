using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointStoreTests
{
    [TestMethod]
    public async Task EndpointStoreRoundTripsNormalizedMetadataWithoutSecretValue()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore store = new(paths);
        EndpointRecord expected = new(
            EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("office"),
                "Office",
                new Uri("HTTPS://Mihomo.Example.Test")),
            "office-secret");

        try
        {
            await store.SaveAsync([expected]);
            string persisted = await File.ReadAllTextAsync(paths.EndpointStoreFile);
            EndpointStoreLoadResult loaded = await store.LoadAsync();

            Assert.IsFalse(persisted.Contains("super-secret", StringComparison.Ordinal));
            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, loaded.Status);
            Assert.AreEqual(1, loaded.Endpoints.Count);
            EndpointRecord actual = loaded.Endpoints[0];
            Assert.AreEqual(expected.Descriptor, actual.Descriptor);
            Assert.AreEqual("office-secret", actual.SecretReference);
            Assert.IsNotNull(actual.CreatedAtUtc);
            Assert.IsNotNull(actual.UpdatedAtUtc);
            Assert.IsTrue(actual.UpdatedAtUtc >= actual.CreatedAtUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EndpointStoreSupportsCustomCertificateSecurityAndRejectsLocalDuplicates()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore store = new(paths);
        EndpointRecord custom = new(
            EndpointUriNormalizer.CreateRemoteDescriptor(
                    new EndpointId("custom"),
                    "Custom",
                    new Uri("https://custom.example.test"))
                with { Security = EndpointTransportSecurity.HttpsCustomCertificate },
            CertificateReference: "custom-ca");

        try
        {
            await store.SaveAsync([custom]);
            EndpointStoreLoadResult loaded = await store.LoadAsync();
            Assert.AreEqual(EndpointTransportSecurity.HttpsCustomCertificate, loaded.Endpoints[0].Descriptor.Security);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync([
                custom,
                custom with { Descriptor = custom.Descriptor with { Id = new EndpointId("CUSTOM") } }
            ]));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync([
                custom with { Descriptor = EndpointDescriptorForLocal() }
            ]));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EndpointStoreRejectsCertificateReferencesForSystemTrustEndpoints()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore store = new(paths);
        EndpointRecord endpoint = new(
            EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("secure"),
                "Secure",
                new Uri("https://secure.example.test")),
            CertificateReference: "unused-ca");

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync([endpoint]));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ExplicitHttpEndpointRequiresAndPersistsRiskAcknowledgement()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore store = new(paths);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("lab"),
            "Lab",
            new Uri("http://lab.example.test"),
            allowExplicitHttp: true);
        EndpointRecord withoutAcknowledgement = new(descriptor);
        DateTimeOffset acknowledgedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        EndpointRecord acknowledged = withoutAcknowledgement with
        {
            InsecureHttpAcknowledgedAtUtc = acknowledgedAt
        };

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync([withoutAcknowledgement]));
            await store.SaveAsync([acknowledged]);

            EndpointStoreLoadResult loaded = await store.LoadAsync();
            Assert.AreEqual(acknowledgedAt, loaded.Endpoints.Single().InsecureHttpAcknowledgedAtUtc);
            Assert.IsNotNull(loaded.Endpoints.Single().CreatedAtUtc);
            Assert.IsNotNull(loaded.Endpoints.Single().UpdatedAtUtc);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task LegacyEndpointMetadataLoadsAndReceivesTimestampsOnNextSave()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.EndpointStoreFile,
            "{\"schemaVersion\":1,\"endpoints\":[{\"id\":\"legacy\",\"displayName\":\"Legacy\",\"baseUri\":\"https://legacy.example.test/\",\"security\":1,\"isEnabled\":true}]}" );
        EndpointStore store = new(paths);

        try
        {
            EndpointStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, loaded.Status);
            Assert.IsNotNull(loaded.Endpoints.Single().CreatedAtUtc);
            Assert.IsNotNull(loaded.Endpoints.Single().UpdatedAtUtc);

            await store.SaveAsync(loaded.Endpoints);
            string persisted = await File.ReadAllTextAsync(paths.EndpointStoreFile);
            StringAssert.Contains(persisted, "createdAtUtc", StringComparison.Ordinal);
            StringAssert.Contains(persisted, "updatedAtUtc", StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CorruptEndpointMetadataIsQuarantined()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.EndpointStoreFile, "{not-json");
        EndpointStore store = new(paths);

        try
        {
            EndpointStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.IsFalse(File.Exists(paths.EndpointStoreFile));
            Assert.AreEqual(1, Directory.GetFiles(paths.LocalRoot, "endpoints.json.corrupt-*").Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SecretStoreProtectsRoundTripAndSupportsDelete()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointSecretStore store = new(paths);

        try
        {
            await store.SetAsync("office-secret", "super-secret");
            string persisted = await File.ReadAllTextAsync(paths.EndpointSecretsFile);

            Assert.IsFalse(persisted.Contains("super-secret", StringComparison.Ordinal));
            Assert.AreEqual("super-secret", await store.GetAsync("office-secret"));
            Assert.IsTrue(await store.DeleteAsync("office-secret"));
            Assert.IsNull(await store.GetAsync("office-secret"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CorruptSecretStoreIsQuarantinedWithoutReturningSecretData()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.EndpointSecretsFile,
            "{\"schemaVersion\":1,\"secrets\":{\"office\":\"not-base64\"}}");
        EndpointSecretStore store = new(paths);

        try
        {
            EndpointSecretStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointSecretStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Secrets.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreRoundTripsDerAndPemCertificate()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointCertificateStore store = new(paths);
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=ClashTray Test CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        byte[] der = certificate.Export(X509ContentType.Cert);
        string pem = certificate.ExportCertificatePem();

        try
        {
            await store.SetAsync("office-ca", der);
            byte[] storedDer = (await store.GetAsync("office-ca"))!;
            using X509Certificate2 roundTrip = X509CertificateLoader.LoadCertificate(storedDer);
            Assert.AreEqual(certificate.Thumbprint, roundTrip.Thumbprint);
            await store.SetAsync("pem-ca", System.Text.Encoding.UTF8.GetBytes(pem));
            Assert.IsNotNull(await store.GetAsync("pem-ca"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static EndpointDescriptor EndpointDescriptorForLocal() => new(
        EndpointId.Local,
        EndpointKind.Local,
        "local",
        new Uri("http://127.0.0.1:9090/"),
        EndpointTransportSecurity.Loopback);

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
