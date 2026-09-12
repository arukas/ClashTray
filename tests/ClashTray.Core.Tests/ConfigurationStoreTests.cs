using ClashTray.Contracts;
using System.Text.Json;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ConfigurationStoreTests
{
    private static readonly int[] ExpectedNewestItems = [2, 3];

    [TestMethod]
    public async Task SubscriptionMetadataStoresUrlAsCurrentUserProtectedData()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using SubscriptionHandler handler = new SubscriptionHandler();
        try
        {
            AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
            ConfigurationStore store = new ConfigurationStore(paths, handler);
            Uri uri = new Uri("https://subscription.invalid/config?token=secret-value");

            ConfigurationImportResult imported = await store.ImportSubscriptionWithResultAsync(uri);
            string metadata = await File.ReadAllTextAsync(
                Path.Combine(paths.ConfigurationsRoot, $"{imported.Profile.Id}.json"));

            Assert.IsFalse(metadata.Contains(uri.AbsoluteUri, StringComparison.Ordinal));
            Assert.IsFalse(metadata.Contains("secret-value", StringComparison.Ordinal));
            StringAssert.Contains(metadata, "subscriptionUriProtected", StringComparison.Ordinal);
            Assert.AreEqual(uri, (await store.ListAsync()).Single().SubscriptionUri);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task LegacyPlaintextSubscriptionMetadataMigratesToProtectedData()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        string id = "0123456789abcdef";
        Uri uri = new Uri("https://subscription.invalid/config?token=legacy-secret");
        try
        {
            AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
            ConfigurationStore store = new ConfigurationStore(paths);
            string configurationPath = Path.Combine(paths.ConfigurationsRoot, $"{id}.yaml");
            await File.WriteAllTextAsync(configurationPath, "mixed-port: 7890\n");
            await File.WriteAllTextAsync(
                Path.Combine(paths.ConfigurationsRoot, $"{id}.json"),
                JsonSerializer.Serialize(new
                {
                    id,
                    name = "Legacy",
                    path = configurationPath,
                    subscriptionUri = uri.AbsoluteUri,
                    isActive = false
                }));

            IReadOnlyList<ConfigurationProfile> profiles = await store.ListAsync();
            string migrated = await File.ReadAllTextAsync(
                Path.Combine(paths.ConfigurationsRoot, $"{id}.json"));

            Assert.AreEqual(uri, profiles.Single().SubscriptionUri);
            Assert.IsFalse(migrated.Contains(uri.AbsoluteUri, StringComparison.Ordinal));
            using JsonDocument document = JsonDocument.Parse(migrated);
            Assert.IsFalse(document.RootElement.TryGetProperty("subscriptionUri", out _));
            Assert.IsTrue(document.RootElement.TryGetProperty("subscriptionUriProtected", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task SubscriptionImportAndRefreshSendBundledMihomoUserAgent(int downloads)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using SubscriptionHandler handler = new SubscriptionHandler();
        try
        {
            AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
            ConfigurationStore store = new ConfigurationStore(paths, handler);
            Uri uri = new Uri("https://subscription.invalid/config");
            for (int index = 0; index < downloads; index++)
            {
                // Manual and scheduled refresh use the same import path with the saved URI/name.
                ConfigurationImportResult update = await store.ImportSubscriptionWithResultAsync(uri, "Test subscription");
                Assert.AreEqual(index == 0, update.ContentChanged);
                Assert.AreEqual(uri, update.Profile.SubscriptionUri);
                Assert.AreEqual(64, update.Sha256.Length);
                Assert.AreEqual("mixed-port: 7890\n", await File.ReadAllTextAsync(update.Profile.Path));
            }

            Assert.AreEqual("v1.19.30", BundledMihomo.Version);
            Assert.AreEqual(downloads, handler.UserAgents.Count);
            Assert.IsTrue(handler.UserAgents.All(ua => ua == "clash.meta/v1.19.30"));
            Assert.AreEqual(1, (await store.ListAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SubscriptionImportDetectsChangedContentBySha256()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using SubscriptionHandler handler = new SubscriptionHandler();
        try
        {
            AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
            ConfigurationStore store = new ConfigurationStore(paths, handler);
            Uri uri = new Uri("https://subscription.invalid/config");

            ConfigurationImportResult first = await store.ImportSubscriptionWithResultAsync(uri);
            handler.ResponseBody = "mixed-port: 7891\n";
            ConfigurationImportResult second = await store.ImportSubscriptionWithResultAsync(uri);

            Assert.IsTrue(first.ContentChanged);
            Assert.IsTrue(second.ContentChanged);
            Assert.AreNotEqual(first.Sha256, second.Sha256);
            Assert.AreEqual("mixed-port: 7891\n", await File.ReadAllTextAsync(second.Profile.Path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SubscriptionUserAgentDoesNotBypassConfigurationValidation()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using SubscriptionHandler handler = new SubscriptionHandler { ResponseBody = "<html>Login required</html>" };
        try
        {
            AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
            ConfigurationStore store = new ConfigurationStore(paths, handler);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                store.ImportSubscriptionAsync(new Uri("https://subscription.invalid/config")));
            Assert.AreEqual("clash.meta/v1.19.30", handler.UserAgents.Single());
            Assert.AreEqual(0, (await store.ListAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RejectedSubscriptionCandidatePreservesLastAcceptedConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using SubscriptionHandler handler = new SubscriptionHandler();
        CandidateValidator validator = new CandidateValidator();
        try
        {
            AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
            ConfigurationStore store = new ConfigurationStore(paths, handler, validator);
            Uri uri = new Uri("https://subscription.invalid/config");

            ConfigurationImportResult first = await store.ImportSubscriptionWithResultAsync(uri);
            string previousContent = await File.ReadAllTextAsync(first.Profile.Path);
            string previousMetadata = await File.ReadAllTextAsync(
                Path.Combine(paths.ConfigurationsRoot, $"{first.Profile.Id}.json"));

            validator.Reject = true;
            handler.ResponseBody = "mixed-port: not-a-number\n";

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                store.ImportSubscriptionWithResultAsync(uri));

            Assert.AreEqual(previousContent, await File.ReadAllTextAsync(first.Profile.Path));
            Assert.AreEqual(
                previousMetadata,
                await File.ReadAllTextAsync(Path.Combine(paths.ConfigurationsRoot, $"{first.Profile.Id}.json")));
            ConfigurationProfile current = (await store.ListAsync()).Single();
            Assert.AreEqual(first.Profile.LastRefreshed, current.LastRefreshed);
            Assert.AreEqual(
                0,
                Directory.EnumerateFiles(paths.ConfigurationsRoot, ".candidate-*").Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class SubscriptionHandler : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];
        public string ResponseBody { get; set; } = "mixed-port: 7890\n";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string userAgent = request.Headers.UserAgent.ToString();
            UserAgents.Add(userAgent);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(userAgent == "clash.meta/v1.19.30" ? ResponseBody : "unsupported client"),
            });
        }
    }

    private sealed class CandidateValidator : IConfigurationCandidateValidator
    {
        public bool Reject { get; set; }

        public Task ValidateAsync(string candidatePath, CancellationToken cancellationToken = default)
        {
            if (Reject)
            {
                throw new InvalidDataException("candidate rejected");
            }

            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public void ValidateYamlAcceptsMihomoConfiguration()
    {
        ConfigurationStore.ValidateYaml("mixed-port: 7890\nmode: rule\n"u8);
    }

    [TestMethod]
    public void ValidateYamlRejectsEmptyContent()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ConfigurationStore.ValidateYaml(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void ValidateYamlRejectsInvalidUtf8()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ConfigurationStore.ValidateYaml(new byte[]
            {
                (byte)'m', (byte)'i', (byte)'x', (byte)'e', (byte)'d',
                (byte)'-', (byte)'p', (byte)'o', (byte)'r', (byte)'t', (byte)':', 0xFF
            }));
    }

    [TestMethod]
    public void BoundedBufferRetainsNewestItems()
    {
        BoundedBuffer<int> buffer = new BoundedBuffer<int>(2);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        CollectionAssert.AreEqual(ExpectedNewestItems, buffer.Snapshot().ToArray());
    }

    [TestMethod]
    public async Task ReloadRejectsConfigurationPathOutsideStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        string outsidePath = Path.Combine(root, "outside.yaml");
        await File.WriteAllTextAsync(outsidePath, "mixed-port: 7890\n");

        try
        {
            ConfigurationProfile profile = new ConfigurationProfile("outside", "outside", outsidePath, null, null, false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReloadAsync(profile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ListIgnoresMetadataWithPathTraversalIdentifier()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        string configurationPath = Path.Combine(paths.ConfigurationsRoot, "valid.yaml");
        await File.WriteAllTextAsync(configurationPath, "mixed-port: 7890\n");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ConfigurationsRoot, "malicious.json"),
            "{\"Id\":\"..\\\\outside\\\\target\",\"Name\":\"bad\",\"Path\":\""
            + configurationPath.Replace("\\", "\\\\", StringComparison.Ordinal)
            + "\",\"IsActive\":false}");

        try
        {
            IReadOnlyList<ConfigurationProfile> profiles = await store.ListAsync();

            Assert.AreEqual(0, profiles.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteRejectsMetadataPathTraversalIdentifier()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        string configurationPath = Path.Combine(paths.ConfigurationsRoot, "valid.yaml");
        await File.WriteAllTextAsync(configurationPath, "mixed-port: 7890\n");
        string outsidePath = Path.Combine(root, "outside.json");
        await File.WriteAllTextAsync(outsidePath, "keep");

        try
        {
            ConfigurationProfile profile = new ConfigurationProfile(
                "..\\outside",
                "bad",
                configurationPath,
                null,
                null,
                false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.DeleteAsync(profile));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(outsidePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
