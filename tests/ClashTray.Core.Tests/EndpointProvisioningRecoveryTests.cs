using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointProvisioningRecoveryTests
{
    [TestMethod]
    public async Task ProvisionRollbackDeletesSecretAndCertificateWhenMetadataSaveFails()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor existing = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("existing"),
            "Existing",
            new Uri("https://existing.example.test"));
        EndpointDescriptor candidate = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("office"),
                "Office",
                new Uri("https://office.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        using X509Certificate2 ca = CreateCaCertificate();

        try
        {
            await coordinator.ProvisionAsync(existing, null, null, null);
            using (FileStream blocker = new(paths.EndpointStoreFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => coordinator.ProvisionAsync(
                    candidate,
                    "controller-secret",
                    ca.Export(X509ContentType.Cert),
                    null));
            }

            Assert.IsNull(await secretStore.GetAsync("office-secret"));
            Assert.IsNull(await certificateStore.GetAsync("office-ca"));
            EndpointStoreLoadResult loaded = await endpointStore.LoadAsync();
            Assert.AreEqual(existing.Id, loaded.Endpoints.Single().Descriptor.Id);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdateRollbackRestoresPreviousSecretWhenMetadataSaveFails()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await coordinator.ProvisionAsync(descriptor, "old-secret", null, null);
            using (FileStream blocker = new(paths.EndpointStoreFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => coordinator.UpdateAsync(
                    descriptor.Id,
                    descriptor,
                    "new-secret",
                    customCaCertificate: null,
                    insecureHttpAcknowledgedAtUtc: null));
            }

            Assert.AreEqual("old-secret", await secretStore.GetAsync("office-secret"));
            EndpointRecord persisted = (await endpointStore.LoadAsync()).Endpoints.Single();
            Assert.AreEqual("office-secret", persisted.SecretReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdateRollbackRestoresPreviousCertificateWhenMetadataSaveFails()
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
        using X509Certificate2 originalCa = CreateCaCertificate("CN=ClashTray Recovery CA A");
        using X509Certificate2 rotatedCa = CreateCaCertificate("CN=ClashTray Recovery CA B");
        byte[] originalBytes = originalCa.Export(X509ContentType.Cert);

        try
        {
            await coordinator.ProvisionAsync(custom, null, originalBytes, null);
            using (FileStream blocker = new(paths.EndpointStoreFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => coordinator.UpdateAsync(
                    custom.Id,
                    custom,
                    secret: null,
                    customCaCertificate: rotatedCa.Export(X509ContentType.Cert),
                    insecureHttpAcknowledgedAtUtc: null));
            }

            CollectionAssert.AreEqual(originalBytes, await certificateStore.GetAsync("office-ca"));
            EndpointRecord persisted = (await endpointStore.LoadAsync()).Endpoints.Single();
            Assert.AreEqual("office-ca", persisted.CertificateReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdateRollbackDeletesNewlyAddedSecretWhenMetadataSaveFails()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await coordinator.ProvisionAsync(descriptor, null, null, null);
            using (FileStream blocker = new(paths.EndpointStoreFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => coordinator.UpdateAsync(
                    descriptor.Id,
                    descriptor,
                    "new-secret",
                    customCaCertificate: null,
                    insecureHttpAcknowledgedAtUtc: null));
            }

            Assert.IsNull(await secretStore.GetAsync("office-secret"));
            EndpointRecord persisted = (await endpointStore.LoadAsync()).Endpoints.Single();
            Assert.IsNull(persisted.SecretReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdateRollbackDeletesNewlyAddedCertificateWhenMetadataSaveFails()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));
        using X509Certificate2 ca = CreateCaCertificate();

        try
        {
            await coordinator.ProvisionAsync(descriptor, null, null, null);
            using (FileStream blocker = new(paths.EndpointStoreFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => coordinator.UpdateAsync(
                    descriptor.Id,
                    descriptor with { Security = EndpointTransportSecurity.HttpsCustomCertificate },
                    secret: null,
                    customCaCertificate: ca.Export(X509ContentType.Cert),
                    insecureHttpAcknowledgedAtUtc: null));
            }

            Assert.IsNull(await certificateStore.GetAsync("office-ca"));
            EndpointRecord persisted = (await endpointStore.LoadAsync()).Endpoints.Single();
            Assert.IsNull(persisted.CertificateReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdateClearingSecretSucceedsWhenOrphanedSecretCannotBeDeleted()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointProvisioningCoordinator coordinator = new(endpointStore, secretStore, certificateStore);
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await coordinator.ProvisionAsync(descriptor, "secret-value", null, null);
            using (FileStream blocker = new(paths.EndpointSecretsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                EndpointRecord updated = await coordinator.UpdateAsync(
                    descriptor.Id,
                    descriptor,
                    string.Empty,
                    customCaCertificate: null,
                    insecureHttpAcknowledgedAtUtc: null);

                Assert.IsNull(updated.SecretReference);
                Assert.AreEqual(
                    "secret-value",
                    await secretStore.GetAsync("office-secret"));
            }

            EndpointRecord persisted = (await endpointStore.LoadAsync()).Endpoints.Single();
            Assert.IsNull(persisted.SecretReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UpdateRemovingCustomCaSucceedsWhenOrphanedCertificateCannotBeDeleted()
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
        EndpointDescriptor systemTrust = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));
        using X509Certificate2 ca = CreateCaCertificate();

        try
        {
            await coordinator.ProvisionAsync(custom, null, ca.Export(X509ContentType.Cert), null);
            using (FileStream blocker = new(paths.EndpointCertificatesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                EndpointRecord updated = await coordinator.UpdateAsync(
                    custom.Id,
                    systemTrust,
                    secret: null,
                    customCaCertificate: null,
                    insecureHttpAcknowledgedAtUtc: null);

                Assert.IsNull(updated.CertificateReference);
                Assert.IsNotNull(await certificateStore.GetAsync("office-ca"));
            }

            EndpointRecord persisted = (await endpointStore.LoadAsync()).Endpoints.Single();
            Assert.IsNull(persisted.CertificateReference);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreLoadReturnsFirstRunWhenFileIsMissing()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.FirstRun, loaded.Status);
            Assert.IsFalse(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
            Assert.IsNull(loaded.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CorruptCertificateStoreJsonIsQuarantined()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.EndpointCertificatesFile, "{not-json");
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
            Assert.IsFalse(File.Exists(paths.EndpointCertificatesFile));
            Assert.AreEqual(
                1,
                Directory.GetFiles(paths.LocalRoot, "endpoint-certificates.json.corrupt-*").Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreQuarantinesInvalidSchemaVersion()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.EndpointCertificatesFile,
            "{\"schemaVersion\":999,\"certificates\":{}}");
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreQuarantinesInvalidBase64Entry()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.EndpointCertificatesFile,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                certificates = new Dictionary<string, string> { ["office"] = "not-base64!" }
            }));
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreQuarantinesUnparseableCertificateBytes()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.EndpointCertificatesFile,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                certificates = new Dictionary<string, string>
                {
                    ["office"] = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 })
                }
            }));
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreQuarantinesNonCaCertificateEntry()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        using X509Certificate2 leaf = CreateLeafCertificate();
        await File.WriteAllTextAsync(
            paths.EndpointCertificatesFile,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                certificates = new Dictionary<string, string>
                {
                    ["office"] = Convert.ToBase64String(leaf.Export(X509ContentType.Cert))
                }
            }));
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreQuarantinesDuplicateReferences()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        using X509Certificate2 ca = CreateCaCertificate();
        string encoded = Convert.ToBase64String(ca.Export(X509ContentType.Cert));
        await File.WriteAllTextAsync(
            paths.EndpointCertificatesFile,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                certificates = new Dictionary<string, string>
                {
                    ["Office"] = encoded,
                    ["office"] = encoded
                }
            }));
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreQuarantinesOversizedFile()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllBytesAsync(paths.EndpointCertificatesFile, new byte[(512 * 1024) + 1]);
        EndpointCertificateStore store = new(paths);

        try
        {
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
            Assert.IsFalse(File.Exists(paths.EndpointCertificatesFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CertificateStoreLoadReportsReadFailedWhenFileIsLocked()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.EndpointCertificatesFile,
            "{\"schemaVersion\":1,\"certificates\":{}}");
        EndpointCertificateStore store = new(paths);

        try
        {
            using FileStream blocker = new(
                paths.EndpointCertificatesFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            EndpointCertificateStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointCertificateStoreLoadStatus.ReadFailed, loaded.Status);
            Assert.IsFalse(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Certificates.Count);
            Assert.IsNotNull(loaded.Message);
            Assert.IsTrue(File.Exists(paths.EndpointCertificatesFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SecretStoreLoadReturnsFirstRunWhenFileIsMissing()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointSecretStore store = new(paths);

        try
        {
            EndpointSecretStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointSecretStoreLoadStatus.FirstRun, loaded.Status);
            Assert.IsFalse(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Secrets.Count);
            Assert.IsNull(loaded.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SecretStoreLoadReportsReadFailedWhenFileIsLocked()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.EndpointSecretsFile,
            "{\"schemaVersion\":1,\"secrets\":{}}");
        EndpointSecretStore store = new(paths);

        try
        {
            using FileStream blocker = new(
                paths.EndpointSecretsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            EndpointSecretStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(EndpointSecretStoreLoadStatus.ReadFailed, loaded.Status);
            Assert.IsFalse(loaded.WasQuarantined);
            Assert.AreEqual(0, loaded.Secrets.Count);
            Assert.IsNotNull(loaded.Message);
            Assert.IsTrue(File.Exists(paths.EndpointSecretsFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static X509Certificate2 CreateCaCertificate(string subject = "CN=ClashTray Recovery CA")
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            subject,
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

    private static X509Certificate2 CreateLeafCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=ClashTray Recovery Leaf",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
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
