using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ClashTray.Core;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class OfficialMihomoUpdaterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task CoreUpdaterInstallsPinnedOfficialArchiveIntoIsolatedPaths()
    {
        string? archivePath = OfficialMihomoTestSupport.FindVerifiedMihomoArchive();
        if (archivePath is null)
        {
            Assert.Inconclusive("A verified pinned official Mihomo archive was not supplied.");
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "ClashTrayIntegrationTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string archiveUri = $"https://github.com/MetaCubeX/mihomo/releases/download/{BundledMihomo.Version}/mihomo-windows-amd64-{BundledMihomo.Version}.zip";
        CoreUpdateManifest manifest = new(BundledMihomo.Version, new Uri(archiveUri), OfficialMihomoTestSupport.PinnedArchiveSha256);

        try
        {
            using OfficialArchiveHandler handler = new(archivePath);
            using HttpClient httpClient = new(handler, disposeHandler: false);
            using CoreUpdater updater = new(paths, httpClient);

            string installedPath = await updater.DownloadAndInstallAsync(manifest);

            Assert.AreEqual(paths.ManagedCoreExecutable, installedPath);
            Assert.IsTrue(File.Exists(paths.ManagedCoreExecutable));
            Assert.IsTrue(File.Exists(paths.ManagedCoreMetadata));
            ManagedCoreMetadata metadata = JsonSerializer.Deserialize<ManagedCoreMetadata>(
                await File.ReadAllTextAsync(paths.ManagedCoreMetadata),
                JsonOptions)
                ?? throw new InvalidDataException("Official updater integration produced no metadata.");
            Assert.AreEqual(BundledMihomo.Version, metadata.Version);
            Assert.AreEqual(manifest.DownloadUri, metadata.DownloadUri);
            Assert.IsTrue(metadata.ArchiveSha256.Equals(OfficialMihomoTestSupport.PinnedArchiveSha256, StringComparison.OrdinalIgnoreCase));
            ManagedCoreVerifier.ValidateWindowsAmd64Executable(paths.ManagedCoreExecutable);
            await ManagedCoreVerifier.ValidateAsync(paths);
            await using FileStream installed = File.OpenRead(paths.ManagedCoreExecutable);
            string installedSha256 = Convert.ToHexString(await SHA256.HashDataAsync(installed));
            Assert.IsTrue(metadata.ExecutableSha256.Equals(installedSha256, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class OfficialArchiveHandler(string archivePath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            FileStream stream = File.OpenRead(archivePath);
            StreamContent content = new(stream);
            content.Headers.ContentLength = stream.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}