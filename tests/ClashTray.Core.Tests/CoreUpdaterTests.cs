using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CoreUpdaterTests
{
    [TestMethod]
    public async Task CoreUpdaterAcceptsVerifiedWindowsAmd64Executable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        var archivePath = Path.Combine(root, "mihomo.zip");

        try
        {
            var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(executablePath, "mihomo.exe");
            }

            var archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using var httpClient = new HttpClient(new ArchiveHandler(archiveBytes));
            var updater = new CoreUpdater(paths, httpClient);
            var manifest = CreateManifest(archiveBytes);

            var installedPath = await updater.DownloadAndInstallAsync(manifest);

            Assert.IsTrue(File.Exists(installedPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsArchiveWithoutWindowsExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        var archivePath = Path.Combine(root, "mihomo.zip");

        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("mihomo.exe");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("not an executable");
            }

            var archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using var httpClient = new HttpClient(new ArchiveHandler(archiveBytes));
            var updater = new CoreUpdater(paths, httpClient);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAndInstallAsync(CreateManifest(archiveBytes)));
            Assert.IsFalse(File.Exists(Path.Combine(paths.LocalRoot, "core", "mihomo.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CoreUpdateManifest CreateManifest(byte[] archiveBytes) =>
        new(
            "v0.0.0-test",
            new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v0.0.0-test/mihomo-windows-amd64-v0.0.0-test.zip"),
            Convert.ToHexString(SHA256.HashData(archiveBytes)));

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        private readonly byte[] _archiveBytes;

        public ArchiveHandler(byte[] archiveBytes) => _archiveBytes = archiveBytes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_archiveBytes)
            });
    }
}
