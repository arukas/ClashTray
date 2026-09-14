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

    [TestMethod]
    public async Task CertificateStoreRejectsNonCaCertificates()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointCertificateStore store = new(paths);
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=ClashTray Test Leaf",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using X509Certificate2 leaf = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SetAsync(
                "leaf",
                leaf.Export(X509ContentType.Cert)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EndpointRemovalDisconnectsActiveSessionAndDeletesUniqueProtectedMaterial()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointDescriptor endpoint = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("office"),
                "Office",
                new Uri("https://office.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        RecordingEndpointConnector connector = new();
        await using EndpointSessionManager sessions = new(
            ControllerEndpointFactory.CreateLocal(9090),
            connector);
        EndpointRemovalCoordinator coordinator = new(
            endpointStore,
            secretStore,
            certificateStore,
            sessions);
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

        try
        {
            await endpointStore.UpsertAsync(new EndpointRecord(endpoint, "office-secret", "office-ca"));
            await secretStore.SetAsync("office-secret", "secret-value");
            await certificateStore.SetAsync("office-ca", certificate.Export(X509ContentType.Cert));
            await sessions.SelectAsync(endpoint);

            EndpointRemovalResult result = await coordinator.RemoveAsync(endpoint.Id);

            Assert.IsTrue(result.Removed);
            Assert.IsTrue(result.SessionDisconnected);
            Assert.IsTrue(result.SecretRemoved);
            Assert.IsTrue(result.CertificateRemoved);
            Assert.IsNull(sessions.Current);
            Assert.AreEqual(EndpointId.Local, sessions.Status.Endpoint.Id);
            Assert.AreEqual(0, (await endpointStore.LoadAsync()).Endpoints.Count);
            Assert.IsNull(await secretStore.GetAsync("office-secret"));
            Assert.IsNull(await certificateStore.GetAsync("office-ca"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EndpointRemovalPreservesProtectedMaterialStillReferencedByAnotherEndpoint()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore endpointStore = new(paths);
        EndpointSecretStore secretStore = new(paths);
        EndpointCertificateStore certificateStore = new(paths);
        EndpointDescriptor first = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("first"),
                "First",
                new Uri("https://first.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        EndpointDescriptor second = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("second"),
                "Second",
                new Uri("https://second.example.test"))
            with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
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

        try
        {
            await endpointStore.SaveAsync([
                new EndpointRecord(first, "shared-secret", "shared-ca"),
                new EndpointRecord(second, "shared-secret", "shared-ca")]);
            await secretStore.SetAsync("shared-secret", "secret-value");
            await certificateStore.SetAsync("shared-ca", certificate.Export(X509ContentType.Cert));
            EndpointRemovalCoordinator coordinator = new(
                endpointStore,
                secretStore,
                certificateStore);

            EndpointRemovalResult result = await coordinator.RemoveAsync(first.Id);

            Assert.IsTrue(result.Removed);
            Assert.IsFalse(result.SecretRemoved);
            Assert.IsFalse(result.CertificateRemoved);
            Assert.AreEqual("secret-value", await secretStore.GetAsync("shared-secret"));
            Assert.IsNotNull(await certificateStore.GetAsync("shared-ca"));
            EndpointStoreLoadResult loaded = await endpointStore.LoadAsync();
            Assert.AreEqual(second.Id, loaded.Endpoints.Single().Descriptor.Id);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EndpointRemovalRejectsThePermanentLocalEndpoint()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointRemovalCoordinator coordinator = new(
            new EndpointStore(paths),
            new EndpointSecretStore(paths),
            new EndpointCertificateStore(paths));

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => coordinator.RemoveAsync(EndpointId.Local));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private sealed class RecordingEndpointConnector : IEndpointSessionConnector
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The created transport is transferred to EndpointSession, which owns and disposes it.")]
        public Task<EndpointSession> ConnectAsync(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationToken cancellationToken)
        {
            EndpointDescriptor transportEndpoint = endpoint.Security == EndpointTransportSecurity.HttpsCustomCertificate
                ? endpoint with { Security = EndpointTransportSecurity.HttpsSystemTrust }
                : endpoint;
            EndpointTransport transport = EndpointTransportFactory.Create(transportEndpoint);
            return Task.FromResult(new EndpointSession(
                transport,
                EndpointCapabilityDefaults.Remote,
                generation,
                selectionRevision));
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
