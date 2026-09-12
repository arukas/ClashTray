using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CoreUpdaterTests
{
    [TestMethod]
    public async Task CoreUpdaterAcceptsVerifiedWindowsAmd64Executable()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string archivePath = Path.Combine(root, "mihomo.zip");

        try
        {
            string executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
            using (ZipArchive archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Create))
            {
                await archive.CreateEntryFromFileAsync(executablePath, "mihomo.exe");
            }

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using ArchiveHandler handler = new ArchiveHandler(archiveBytes);
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            CoreUpdater updater = new CoreUpdater(paths, httpClient);
            CoreUpdateManifest manifest = CreateManifest(archiveBytes);

            string installedPath = await updater.DownloadAndInstallAsync(manifest);

            Assert.AreEqual(paths.ManagedCoreExecutable, installedPath);
            Assert.IsTrue(File.Exists(installedPath));
            Assert.IsTrue(File.Exists(paths.ManagedCoreMetadata));
            await ManagedCoreVerifier.ValidateAsync(paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsArchiveWithoutWindowsExecutable()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string archivePath = Path.Combine(root, "mihomo.zip");

        try
        {
            using (ZipArchive archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("mihomo.exe");
                using StreamWriter writer = new StreamWriter(await entry.OpenAsync());
                await writer.WriteAsync("not an executable");
            }

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using ArchiveHandler handler = new ArchiveHandler(archiveBytes);
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            CoreUpdater updater = new CoreUpdater(paths, httpClient);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAndInstallAsync(CreateManifest(archiveBytes)));
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsOversizedArchiveBeforeDownloadingBody()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();

        try
        {
            using OversizedArchiveHandler handler = new OversizedArchiveHandler();
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            CoreUpdater updater = new CoreUpdater(paths, httpClient);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAndInstallAsync(
                CreateManifest([])));
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRefusesToOverwriteUnknownCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.CoreRoot);
        string executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
        File.Copy(executablePath, paths.ManagedCoreExecutable);

        try
        {
            CoreUpdater updater = new CoreUpdater(paths);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAndInstallAsync(
                new CoreUpdateManifest(
                    "v0.0.0-test",
                    new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v0.0.0-test/mihomo-windows-amd64-v0.0.0-test.zip"),
                    new string('0', 64))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CoreDiscoveryIgnoresUserWritableLocalCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string localCore = Path.Combine(paths.LocalRoot, "core", "mihomo.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(localCore)!);
        File.Copy(Environment.ProcessPath!, localCore);

        try
        {
            Assert.IsNull(new CoreDiscovery(paths).FindExecutable());
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

    private sealed class OversizedArchiveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ByteArrayContent content = new ByteArrayContent([0]);
            content.Headers.ContentLength = 128L * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }
}
