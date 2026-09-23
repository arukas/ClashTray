using System.Text.Json;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SystemProxyTransactionRecoveryTests
{
    [TestMethod]
    public async Task EnablePersistsBackupOwnershipAndTransactionBeforeFirstRegistryWrite()
    {
        string root = CreateRoot();
        AppPaths paths = CreatePaths(root);
        FakeSystemProxyRegistry registry = new(CreateOriginalState());
        registry.BeforeWrite = () =>
        {
            registry.EvidencePresentAtFirstWrite =
                File.Exists(paths.ProxyBackupFile)
                && File.Exists(paths.ProxyOwnershipFile)
                && File.Exists(paths.ProxyTransactionFile);
        };
        SystemProxyManager manager = new SystemProxyManager(paths, registry, () => { });

        try
        {
            await manager.EnableAsync(7890, "localhost;127.*");

            Assert.IsTrue(registry.EvidencePresentAtFirstWrite);
            Assert.AreEqual(
                new ProxyRegistryState(1, "127.0.0.1:7890", "localhost;127.*", "https://pac.example.test/original.pac", 1),
                registry.State);
            Assert.IsFalse(File.Exists(paths.ProxyTransactionFile));
            Assert.IsTrue(File.Exists(paths.ProxyBackupFile));
            Assert.IsTrue(File.Exists(paths.ProxyOwnershipFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PartialRegistryWriteFailureRestoresOriginalWithoutLosingRecoveryEvidence()
    {
        string root = CreateRoot();
        AppPaths paths = CreatePaths(root);
        ProxyRegistryState original = CreateOriginalState();
        FakeSystemProxyRegistry registry = new(original) { FailAfterProxyServerWrite = true };
        SystemProxyManager manager = new SystemProxyManager(paths, registry, () => { });

        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => manager.EnableAsync(7890, "localhost"));

            Assert.AreEqual(original, registry.State);
            Assert.AreEqual(1, registry.RestoreWriteCount);
            Assert.IsFalse(File.Exists(paths.ProxyTransactionFile));
            Assert.IsFalse(File.Exists(paths.ProxyBackupFile));
            Assert.IsFalse(File.Exists(paths.ProxyOwnershipFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 0)]
    [DataRow(1, 1)]
    [DataRow(2, 1)]
    [DataRow(2, 2)]
    [DataRow(3, 2)]
    [DataRow(3, 3)]
    public async Task ProcessRestartRestoresEveryPossibleRegistryWriteInterruption(
        int lastWriteStarted,
        int writesCompleted)
    {
        string root = CreateRoot();
        AppPaths paths = CreatePaths(root);
        ProxyRegistryState original = CreateOriginalState();
        ProxyRegistryState intended = CreateIntendedState(original);
        SystemProxyTransitionJournal journal = SystemProxyTransitionJournal.Create(
            original,
            intended,
            previousOwnership: null,
            createdBackup: true);
        if (lastWriteStarted > 0)
        {
            journal = journal with
            {
                Stage = SystemProxyTransitionStage.Applying,
                LastWriteStarted = (SystemProxyRegistryField)lastWriteStarted
            };
        }

        await WriteRecoveryArtifactsAsync(paths, original, intended, journal);
        FakeSystemProxyRegistry registry = new(CreatePartiallyAppliedState(original, intended, writesCompleted));

        try
        {
            SystemProxyTransactionRecoveryResult result =
                await SystemProxyTransactionRecovery.RecoverAsync(paths, registry);

            Assert.AreEqual(SystemProxyTransactionRecoveryResult.Restored, result);
            Assert.AreEqual(original, registry.State);
            Assert.AreEqual(1, registry.RestoreWriteCount);
            Assert.IsFalse(File.Exists(paths.ProxyTransactionFile));
            Assert.IsFalse(File.Exists(paths.ProxyBackupFile));
            Assert.IsFalse(File.Exists(paths.ProxyOwnershipFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ProcessRestartLeavesExternalProxyModificationUntouched()
    {
        string root = CreateRoot();
        AppPaths paths = CreatePaths(root);
        ProxyRegistryState original = CreateOriginalState();
        ProxyRegistryState intended = CreateIntendedState(original);
        SystemProxyTransitionJournal journal = SystemProxyTransitionJournal.Create(
                original,
                intended,
                previousOwnership: null,
                createdBackup: true)
            with
            {
                Stage = SystemProxyTransitionStage.Applying,
                LastWriteStarted = SystemProxyRegistryField.ProxyServer
            };
        await WriteRecoveryArtifactsAsync(paths, original, intended, journal);
        ProxyRegistryState externallyChanged = CreatePartiallyAppliedState(original, intended, writesCompleted: 1)
            with { ProxyOverride = "proxy-from-another-app" };
        FakeSystemProxyRegistry registry = new(externallyChanged);

        try
        {
            SystemProxyTransactionRecoveryResult result =
                await SystemProxyTransactionRecovery.RecoverAsync(paths, registry);

            Assert.AreEqual(SystemProxyTransactionRecoveryResult.Conflict, result);
            Assert.AreEqual(externallyChanged, registry.State);
            Assert.AreEqual(0, registry.RestoreWriteCount);
            Assert.IsTrue(File.Exists(paths.ProxyTransactionFile));
            Assert.IsTrue(File.Exists(paths.ProxyBackupFile));
            Assert.IsTrue(File.Exists(paths.ProxyOwnershipFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task WriteRecoveryArtifactsAsync(
        AppPaths paths,
        ProxyRegistryState original,
        ProxyRegistryState intended,
        SystemProxyTransitionJournal journal)
    {
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.ProxyBackupFile, JsonSerializer.Serialize(original));
        await File.WriteAllTextAsync(
            paths.ProxyOwnershipFile,
            JsonSerializer.Serialize(SystemProxyOwnershipPolicy.Create(intended)));
        await new SystemProxyTransactionJournalStore(paths).SaveAsync(journal);
    }

    private static AppPaths CreatePaths(string root) =>
        new(Path.Combine(root, "local"), Path.Combine(root, "program"));

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProxyRegistryState CreateOriginalState() =>
        new(
            ProxyEnable: 0,
            ProxyServer: null,
            ProxyOverride: null,
            AutoConfigUrl: "https://pac.example.test/original.pac",
            AutoDetect: 1);

    private static ProxyRegistryState CreateIntendedState(ProxyRegistryState original) =>
        original with
        {
            ProxyEnable = 1,
            ProxyServer = "127.0.0.1:7890",
            ProxyOverride = "localhost;127.*"
        };

    private static ProxyRegistryState CreatePartiallyAppliedState(
        ProxyRegistryState original,
        ProxyRegistryState intended,
        int writesCompleted)
    {
        ProxyRegistryState state = original;
        if (writesCompleted >= 1)
        {
            state = state with { ProxyEnable = intended.ProxyEnable };
        }

        if (writesCompleted >= 2)
        {
            state = state with { ProxyServer = intended.ProxyServer };
        }

        if (writesCompleted >= 3)
        {
            state = state with { ProxyOverride = intended.ProxyOverride };
        }

        return state;
    }

    private sealed class FakeSystemProxyRegistry(ProxyRegistryState initialState) : ISystemProxyRegistry
    {
        public ProxyRegistryState State { get; private set; } = initialState;

        public Action? BeforeWrite { get; set; }

        public bool FailAfterProxyServerWrite { get; init; }

        public bool EvidencePresentAtFirstWrite { get; set; }

        public int RestoreWriteCount { get; private set; }

        public ProxyRegistryState ReadCurrentState() => State;

        public void SetProxyEnable(int value)
        {
            BeforeWrite?.Invoke();
            State = State with { ProxyEnable = value };
        }

        public void SetProxyServer(string? value)
        {
            State = State with { ProxyServer = value };
            if (FailAfterProxyServerWrite)
            {
                throw new IOException("Synthetic registry failure after ProxyServer write.");
            }
        }

        public void SetProxyOverride(string? value) =>
            State = State with { ProxyOverride = value };

        public void WriteState(ProxyRegistryState state)
        {
            RestoreWriteCount++;
            State = state;
        }
    }
}