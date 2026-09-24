using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CoreUpdaterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void CoreUpdaterAcceptsCanonicalOfficialReleaseUri()
    {
        CoreUpdater.ValidateManifest(new CoreUpdateManifest(
            "v1.19.31",
            new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip"),
            string.Empty));
    }

    [TestMethod]
    public void CoreUpdaterAcceptsMatchingSemVerPrereleaseUri()
    {
        CoreUpdater.ValidateManifest(new CoreUpdateManifest(
            "v1.20.0-rc.1",
            new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v1.20.0-rc.1/mihomo-windows-amd64-v1.20.0-rc.1.zip"),
            string.Empty));
    }

    [TestMethod]
    public void CoreUpdaterRejectsNonCanonicalOfficialReleaseUris()
    {
        string[] invalidUris =
        [
            "https://github.com/example-untrusted-owner/example-repo/raw/refs/heads/main/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            "https://github.com/ExampleOwner/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            "https://github.com/MetaCubeX/mihomo/blob/main/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.30/mihomo-windows-amd64-v1.19.31.zip",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-arm64-v1.19.31.zip",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.exe",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/%6dihomo-windows-amd64-v1.19.31.zip",
            "https://github.com//MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            "https://user@github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            "https://github.com:8443/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip?token=synthetic",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip?",
            "https://github.com:443/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            " http://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip#download",
            "https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31%2ezip"
        ];

        foreach (string downloadUri in invalidUris)
        {
            Assert.ThrowsExactly<ArgumentException>(() => CoreUpdater.ValidateManifest(new CoreUpdateManifest(
                "v1.19.31",
                new Uri(downloadUri, UriKind.Absolute),
                string.Empty)), downloadUri);
        }
    }

    [TestMethod]
    public void CoreUpdaterRejectsInvalidVersionsAgainstCanonicalUri()
    {
        foreach (string invalidVersion in new[] { "", "1.19.31", "v01.19.31", "v1.19.31+build.1", "v1.19.31-", "v1.19.31-rc.01" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => CoreUpdater.ValidateManifest(new CoreUpdateManifest(
                invalidVersion,
                new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip"),
                string.Empty)), invalidVersion);
        }
    }
    [TestMethod]
    public async Task CoreUpdaterRejectsForeignRepositoryBeforeRequestAndLeavesInstalledStateUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.CoreRoot);
        byte[] oldExecutable = await File.ReadAllBytesAsync(
            Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable."));
        await File.WriteAllBytesAsync(paths.ManagedCoreExecutable, oldExecutable);
        string oldExecutableSha256 = Convert.ToHexString(SHA256.HashData(oldExecutable));
        Uri oldUri = new("https://github.com/MetaCubeX/mihomo/releases/download/v1.19.30/mihomo-windows-amd64-v1.19.30.zip");
        ManagedCoreMetadata oldMetadata = new("v1.19.30", oldUri, new string('A', 64), oldExecutableSha256);
        await File.WriteAllTextAsync(paths.ManagedCoreMetadata, JsonSerializer.Serialize(oldMetadata, JsonOptions));
        byte[] oldMetadataBytes = await File.ReadAllBytesAsync(paths.ManagedCoreMetadata);
        byte[] backupExecutable = "previous executable"u8.ToArray();
        byte[] backupMetadata = "previous metadata"u8.ToArray();
        await File.WriteAllBytesAsync(paths.ManagedCoreExecutable + ".previous", backupExecutable);
        await File.WriteAllBytesAsync(paths.ManagedCoreMetadata + ".previous", backupMetadata);

        try
        {
            await ManagedCoreVerifier.ValidateAsync(paths);
            using CountingFailureHandler handler = new();
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            using CoreUpdater updater = new CoreUpdater(paths, httpClient);
            CoreUpdateManifest manifest = new(
                "v1.19.31",
                new Uri("https://github.com/example-untrusted-owner/example-repo/raw/refs/heads/main/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip"),
                new string('B', 64));

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => updater.DownloadAndInstallAsync(manifest));

            Assert.AreEqual(0, handler.RequestCount);
            CollectionAssert.AreEqual(oldExecutable, await File.ReadAllBytesAsync(paths.ManagedCoreExecutable));
            CollectionAssert.AreEqual(oldMetadataBytes, await File.ReadAllBytesAsync(paths.ManagedCoreMetadata));
            CollectionAssert.AreEqual(backupExecutable, await File.ReadAllBytesAsync(paths.ManagedCoreExecutable + ".previous"));
            CollectionAssert.AreEqual(backupMetadata, await File.ReadAllBytesAsync(paths.ManagedCoreMetadata + ".previous"));
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
                await archive.CreateEntryFromFileAsync(executablePath, "mihomo-windows-amd64.exe");
            }

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using ArchiveHandler handler = new ArchiveHandler(archiveBytes);
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            using CoreUpdater updater = new CoreUpdater(paths, httpClient);
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
            using CoreUpdater updater = new CoreUpdater(paths, httpClient);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAndInstallAsync(CreateManifest(archiveBytes)));
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsDuplicateOfficialArchiveExecutables()
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
                await archive.CreateEntryFromFileAsync(executablePath, "mihomo-windows-amd64.exe");
                await archive.CreateEntryFromFileAsync(executablePath, "mihomo-windows-amd64.exe");
            }

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using ArchiveHandler handler = new(archiveBytes);
            using HttpClient httpClient = new(handler, disposeHandler: false);
            using CoreUpdater updater = new(paths, httpClient);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAndInstallAsync(CreateManifest(archiveBytes)));
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable));
            Assert.IsFalse(File.Exists(paths.ManagedCoreMetadata));
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
    public async Task CoreUpdaterRejectsOversizedArchiveBeforeDownloadingBody()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();

        try
        {
            using OversizedArchiveHandler handler = new OversizedArchiveHandler();
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            using CoreUpdater updater = new CoreUpdater(paths, httpClient);

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
    public async Task CoreUpdaterRollsBackToPreviousVerifiedInstall()
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
                await archive.CreateEntryFromFileAsync(executablePath, "mihomo-windows-amd64.exe");
            }

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using MutableArchiveHandler handler = new MutableArchiveHandler(archiveBytes);
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            using CoreUpdater updater = new CoreUpdater(paths, httpClient);

            await updater.DownloadAndInstallAsync(CreateManifest(archiveBytes, "v1.0.0"));
            await updater.DownloadAndInstallAsync(CreateManifest(archiveBytes, "v2.0.0"));

            await updater.RollbackLastInstallAsync();

            ManagedCoreMetadata metadata = JsonSerializer.Deserialize<ManagedCoreMetadata>(
                await File.ReadAllTextAsync(paths.ManagedCoreMetadata),
                JsonOptions)
                ?? throw new InvalidDataException("Rollback metadata is missing.");
            Assert.AreEqual("v1.0.0", metadata.Version);
            await ManagedCoreVerifier.ValidateAsync(paths);
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable + ".previous"));
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable + ".failed"));
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
            using CoreUpdater updater = new CoreUpdater(paths);
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

    [TestMethod]
    public async Task CoreUpdaterInstallsWithoutChecksumWhenManifestOmitsIt()
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
                await archive.CreateEntryFromFileAsync(executablePath, "mihomo-windows-amd64.exe");
            }

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using ArchiveHandler handler = new ArchiveHandler(archiveBytes);
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            using CoreUpdater updater = new CoreUpdater(paths, httpClient);

            string installedPath = await updater.DownloadAndInstallAsync(CreateManifest(archiveBytes) with { Sha256 = "" });

            Assert.IsTrue(File.Exists(installedPath));
            ManagedCoreMetadata metadata = JsonSerializer.Deserialize<ManagedCoreMetadata>(
                await File.ReadAllTextAsync(paths.ManagedCoreMetadata),
                JsonOptions)
                ?? throw new InvalidDataException("Installed metadata is missing.");
            Assert.AreEqual(string.Empty, metadata.ArchiveSha256);
            await ManagedCoreVerifier.ValidateAsync(paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsChecksumMismatchWhenProvided()
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
                await archive.CreateEntryFromFileAsync(executablePath, "mihomo-windows-amd64.exe");
            }

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            using ArchiveHandler handler = new ArchiveHandler(archiveBytes);
            using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
            using CoreUpdater updater = new CoreUpdater(paths, httpClient);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updater.DownloadAndInstallAsync(
                CreateManifest(archiveBytes) with { Sha256 = new string('0', 64) }));
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ManagedCoreVerifierIgnoresExecutableHashDrift()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.CoreRoot);

        try
        {
            string executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
            File.Copy(executablePath, paths.ManagedCoreExecutable);
            ManagedCoreMetadata metadata = new(
                "v0.0.0-test",
                new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v0.0.0-test/mihomo-windows-amd64-v0.0.0-test.zip"),
                string.Empty,
                new string('0', 64));
            await File.WriteAllTextAsync(paths.ManagedCoreMetadata, JsonSerializer.Serialize(metadata, JsonOptions));

            await ManagedCoreVerifier.ValidateAsync(paths);
            Assert.AreEqual("v0.0.0-test", ManagedCoreVerifier.TryReadInstalledVersion(paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ManagedCoreVersionFallsBackToNullWithoutMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();

        try
        {
            Assert.IsNull(ManagedCoreVerifier.TryReadInstalledVersion(paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CoreUpdateManifest CreateManifest(byte[] archiveBytes, string version = "v0.0.0-test") =>
        new(
            version,
            new Uri($"https://github.com/MetaCubeX/mihomo/releases/download/{version}/mihomo-windows-amd64-{version}.zip"),
            Convert.ToHexString(SHA256.HashData(archiveBytes)));

    private sealed class CountingFailureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
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

    private sealed class MutableArchiveHandler : HttpMessageHandler
    {
        public MutableArchiveHandler(byte[] archiveBytes)
        {
            ArchiveBytes = archiveBytes;
        }

        public byte[] ArchiveBytes { get; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ArchiveBytes)
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
