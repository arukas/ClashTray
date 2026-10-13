using System.Security.Cryptography;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Testing;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class OfficialMihomoStartupCancellationTests
{
    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task LocalStartupCancellationAfterProxyCommitRestoresOriginalProxy()
    {
        string? executablePath = OfficialMihomoTestSupport.FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive("Official Mihomo payload not found. Set CLASHTRAY_MIHOMO_PATH to run this test.");
            return;
        }

        ManagedCoreVerifier.ValidateWindowsAmd64Executable(executablePath);
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            Directory.CreateDirectory(paths.CoreRoot);
            File.Copy(executablePath, paths.ManagedCoreExecutable);
            string executableSha256;
            await using (FileStream executable = File.OpenRead(paths.ManagedCoreExecutable))
            {
                executableSha256 = Convert.ToHexString(await SHA256.HashDataAsync(executable));
            }

            ManagedCoreMetadata metadata = new(
                BundledMihomo.Version,
                new Uri($"https://github.com/MetaCubeX/mihomo/releases/download/{BundledMihomo.Version}/mihomo-windows-amd64-{BundledMihomo.Version}.zip"),
                OfficialMihomoTestSupport.PinnedArchiveSha256,
                executableSha256);
            await File.WriteAllTextAsync(paths.ManagedCoreMetadata, JsonSerializer.Serialize(metadata));
            int[] ports = LoopbackPortReservations.Allocate(4);
            AppSettings settings = new(
                ControllerPort: ports[0], HttpPort: ports[1], SocksPort: ports[2], MixedPort: ports[3],
                SystemProxyEnabled: true, TunEnabled: false);
            await new SettingsStore(paths).SaveAsync(settings);
            ProxyRegistryState original = new(0, null, null, "https://pac.example.test/original.pac", 1);
            MemoryProxyRegistry registry = new(original);
            using CancellationTokenSource startupCancellation = new();
            SystemProxyManager proxy = new(paths, registry, () => startupCancellation.Cancel());
            await using ClashTrayRuntime runtime = RuntimeTestBuilder.Create(paths, systemProxy: proxy);
            await runtime.InitializeAsync();
            string sourcePath = Path.Combine(root, "minimal.yaml");
            await File.WriteAllTextAsync(sourcePath,
                "proxies: []\nproxy-groups: []\nrules:\n  - MATCH,DIRECT\ntun:\n  enable: false\n");
            await runtime.ImportLocalConfigurationAsync(sourcePath, "startup-cancellation");

            await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.StartCoreAsync(startupCancellation.Token));

            Assert.IsTrue(startupCancellation.IsCancellationRequested);
            Assert.AreEqual(1, registry.EnableWriteCount, "Cancellation must occur after the production proxy manager committed enable.");
            Assert.AreEqual(CoreState.Stopped, runtime.Snapshot.Core.State);
            Assert.IsNull(runtime.ActiveRuntimeBinding, "The cancelled local child must lose its running binding.");
            Assert.AreEqual(original, registry.State, "Restore the complete original proxy state after stopping the cancelled core.");
            Assert.AreEqual(1, registry.RestoreWriteCount);
            Assert.AreEqual(SystemProxyState.Off, proxy.State);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled, "Core-loss cleanup must retain the enabled preference.");
            Assert.IsFalse(File.Exists(paths.ProxyBackupFile));
            Assert.IsFalse(File.Exists(paths.ProxyOwnershipFile));
        });
    }

    private sealed class MemoryProxyRegistry(ProxyRegistryState initial) : ISystemProxyRegistry
    {
        public ProxyRegistryState State { get; private set; } = initial;
        public int EnableWriteCount { get; private set; }
        public int RestoreWriteCount { get; private set; }
        public ProxyRegistryState ReadCurrentState() => State;
        public void SetProxyEnable(int value)
        {
            EnableWriteCount++;
            State = State with { ProxyEnable = value };
        }
        public void SetProxyServer(string? value) => State = State with { ProxyServer = value };
        public void SetProxyOverride(string? value) => State = State with { ProxyOverride = value };
        public void WriteState(ProxyRegistryState state)
        {
            RestoreWriteCount++;
            State = state;
        }
    }
}
