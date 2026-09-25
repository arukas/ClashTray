using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeRecoveryTests
{
    [TestMethod]
    public async Task StartupRecoveryRestartsRunningCoreAndClearsJournalAndBackup()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await PrepareManagedCoreAsync(paths);
        ConfigurationStore store = new(paths);
        string id = "0123456789abcdef";
        string configurationPath = Path.Combine(paths.ConfigurationsRoot, $"{id}.yaml");
        const string originalContent = "mixed-port: 7890\nproxies: []\nproxy-groups: []\nrules: []\n";
        ConfigurationProfile profile = new(
            id,
            "subscription",
            configurationPath,
            new Uri("https://subscription.invalid/config"),
            DateTimeOffset.UtcNow,
            false);
        await File.WriteAllTextAsync(configurationPath, originalContent);
        await RuntimeTestHelpers.WriteMetadataAsync(paths, profile);
        ConfigurationProfileBackup backup = await store.CaptureBackupAsync(profile);
        Guid backupId = Guid.NewGuid();
        await store.SavePersistentBackupAsync(backupId, profile, backup);
        await File.WriteAllTextAsync(configurationPath, "mixed-port: 7891\n");
        TestSettingsStore settings = new(new AppSettings(ActiveConfigurationId: id));
        ConfigurationSwitchJournalStore journalStore = new(paths);
        await journalStore.SaveAsync(CreateRecoveryJournal(
            id,
            id,
            previousCoreWasRunning: true,
            contentBackupId: backupId));
        RecordingServicePipeClient service = new(CoreState.Running);
        using RecoveryControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            new FakeSystemProxyController(SystemProxyState.Off),
            new AcceptingCandidateValidator(),
            controllerApiFactory: () => api);

        try
        {
            await runtime.InitializeAsync();

            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.IsTrue(runtime.IsCoreHealthConfirmedForTesting);
            Assert.IsNull(runtime.Snapshot.ErrorMessage);
            Assert.AreEqual(originalContent, await File.ReadAllTextAsync(configurationPath));
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
            Assert.IsFalse(Directory.EnumerateFiles(paths.ConfigurationSwitchBackupsRoot).Any());
            CollectionAssert.AreEqual(
                new[] { ServiceCommand.GetStatus, ServiceCommand.StopCore, ServiceCommand.StartCore },
                service.Commands.ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task StartupRecoveryKeepsJournalWhenRestartedCoreFailsHealthCheck()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await PrepareManagedCoreAsync(paths);
        ConfigurationProfile first = await ImportConfigurationAsync(paths, root, "first");
        TestSettingsStore settings = new(new AppSettings(ActiveConfigurationId: first.Id));
        ConfigurationSwitchJournalStore journalStore = new(paths);
        await journalStore.SaveAsync(CreateRecoveryJournal(
            first.Id,
            first.Id,
            previousCoreWasRunning: true));
        RecordingServicePipeClient service = new(CoreState.Running);
        using RecoveryControllerHandler handler = new();
        handler.FailAllVersionRequests();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            new FakeSystemProxyController(SystemProxyState.Off),
            new AcceptingCandidateValidator(),
            controllerApiFactory: () => api);

        try
        {
            List<string?> observedErrorMessages = new();
            runtime.SnapshotChanged += (_, snapshot) =>
            {
                lock (observedErrorMessages)
                {
                    observedErrorMessages.Add(snapshot.ErrorMessage);
                }
            };

            await runtime.InitializeAsync();

            Assert.IsTrue(File.Exists(paths.ConfigurationSwitchJournalFile));
            lock (observedErrorMessages)
            {
                Assert.IsTrue(
                    observedErrorMessages.Any(message =>
                        message?.Contains("旧核心未能通过健康检查", StringComparison.Ordinal) == true),
                    "The incomplete-recovery message must be published to the snapshot.");
            }

            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.IsFalse(runtime.IsCoreHealthConfirmedForTesting);
            ServiceCommand[] commands = service.Commands.ToArray();
            CollectionAssert.Contains(commands, ServiceCommand.StopCore);
            CollectionAssert.Contains(commands, ServiceCommand.StartCore);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task StartupRecoveryStopsCoreAndKeepsJournalWhenContentBackupIsMissing()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile first = await ImportConfigurationAsync(paths, root, "first");
        ConfigurationProfile second = await ImportConfigurationAsync(paths, root, "second");
        TestSettingsStore settings = new(new AppSettings(ActiveConfigurationId: second.Id));
        ConfigurationSwitchJournalStore journalStore = new(paths);
        await journalStore.SaveAsync(CreateRecoveryJournal(
            first.Id,
            second.Id,
            previousCoreWasRunning: true,
            contentBackupId: Guid.NewGuid()));
        RecordingServicePipeClient service = new(CoreState.Running);
        using RecoveryControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            new FakeSystemProxyController(SystemProxyState.Off),
            new AcceptingCandidateValidator(),
            controllerApiFactory: () => api);

        try
        {
            await runtime.InitializeAsync();

            Assert.AreEqual(
                "配置切换恢复记录仍未完成，旧订阅内容无法确认，核心已保持停止。",
                runtime.Snapshot.ErrorMessage);
            Assert.AreEqual(CoreState.Stopped, runtime.Snapshot.Core.State);
            Assert.IsTrue(File.Exists(paths.ConfigurationSwitchJournalFile));
            CollectionAssert.Contains(service.Commands.ToArray(), ServiceCommand.StopCore);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task StartupRecoveryStartsStoppedCoreWhenJournalRecordedRunningCore()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await PrepareManagedCoreAsync(paths);
        ConfigurationProfile first = await ImportConfigurationAsync(paths, root, "first");
        ConfigurationProfile second = await ImportConfigurationAsync(paths, root, "second");
        TestSettingsStore settings = new(new AppSettings(ActiveConfigurationId: first.Id));
        ConfigurationSwitchJournalStore journalStore = new(paths);
        await journalStore.SaveAsync(CreateRecoveryJournal(
            first.Id,
            second.Id,
            previousCoreWasRunning: true));
        RecordingServicePipeClient service = new(CoreState.Stopped);
        using RecoveryControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            new FakeSystemProxyController(SystemProxyState.Off),
            new AcceptingCandidateValidator(),
            controllerApiFactory: () => api);

        try
        {
            await runtime.InitializeAsync();

            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.IsTrue(runtime.IsCoreHealthConfirmedForTesting);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
            CollectionAssert.AreEqual(
                new[] { ServiceCommand.GetStatus, ServiceCommand.StartCore },
                service.Commands.ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task FailedSwitchRollbackRestoresPreviousConfigurationSelection()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await PrepareManagedCoreAsync(paths);
        ConfigurationProfile first = await ImportConfigurationAsync(paths, root, "first");
        ConfigurationProfile second = await ImportConfigurationAsync(paths, root, "second");
        TestSettingsStore settings = new(new AppSettings(ActiveConfigurationId: first.Id));
        RecordingServicePipeClient service = new(CoreState.Stopped);
        using RecoveryControllerHandler handler = new();
        handler.FailAllVersionRequests();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            new FakeSystemProxyController(SystemProxyState.Off),
            new AcceptingCandidateValidator(),
            controllerApiFactory: () => api);

        try
        {
            await runtime.InitializeAsync();
            runtime.AttachControllerForTesting(api, usingServiceCore: true);

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                runtime.SetActiveConfigurationAsync(second.Id));

            Assert.AreEqual("配置切换失败，且旧配置恢复失败。", exception.Message);
            Assert.AreEqual(first.Id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual(
                first.Id,
                runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
            Assert.AreEqual(1, settings.SaveCount);
            ConfigurationSwitchJournalLoadResult journalLoad =
                await new ConfigurationSwitchJournalStore(paths).LoadAsync();
            Assert.IsNotNull(journalLoad.Journal);
            Assert.AreEqual(ConfigurationSwitchStage.RollbackFailed, journalLoad.Journal.Stage);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CoreUpdateRollbackRestoresOldCoreWhenNewCoreFailsHealthCheck()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await PrepareManagedCoreAsync(paths);
        ConfigurationProfile profile = await ImportConfigurationAsync(paths, root, "active");
        TestSettingsStore settings = new(new AppSettings(ActiveConfigurationId: profile.Id));
        RecordingServicePipeClient service = new(CoreState.Stopped);
        using RecoveryControllerHandler handler = new();
        handler.FailNextVersionRequests(5);
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            new FakeSystemProxyController(SystemProxyState.Off),
            new AcceptingCandidateValidator(),
            controllerApiFactory: () => api);

        try
        {
            await runtime.InitializeAsync();
            runtime.AttachControllerForTesting(api, usingServiceCore: true);

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                runtime.InstallCoreUpdateAsync(new CoreUpdateManifest(
                    "v1.19.30",
                    new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v1.19.30/mihomo-windows-amd64-v1.19.30.zip"),
                    new string('0', 64))));

            Assert.AreEqual("核心更新健康检查失败，已自动回滚并恢复旧核心。", exception.Message);
            Assert.AreEqual(1, service.Commands.Count(command => command == ServiceCommand.InstallCore));
            Assert.AreEqual(1, service.Commands.Count(command => command == ServiceCommand.RollbackCore));
            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.IsTrue(runtime.IsCoreHealthConfirmedForTesting);
            Assert.IsTrue(runtime.Snapshot.Logs.Any(log =>
                log.Message.Contains("已自动回滚并恢复旧核心", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ConfigurationSwitchJournal CreateRecoveryJournal(
        string previousConfigurationId,
        string candidateConfigurationId,
        bool previousCoreWasRunning,
        Guid? contentBackupId = null) =>
        ConfigurationSwitchJournal.Create(
            Guid.NewGuid(),
            ConfigurationSwitchSource.Manual,
            previousConfigurationId,
            candidateConfigurationId,
            previousCoreWasRunning,
            previousSystemProxyPreference: false,
            previousSystemProxyState: SystemProxyState.Off,
            previousTunPreference: false,
            previousTunState: TunState.Unavailable,
            previousControllerGeneration: 0)
            .WithContentBackup(contentBackupId)
            .WithStage(ConfigurationSwitchStage.RuntimePromoted);

    private static async Task PrepareManagedCoreAsync(AppPaths paths)
    {
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.CoreRoot);
        File.Copy(Environment.ProcessPath!, paths.ManagedCoreExecutable);
        ManagedCoreMetadata metadata = new(
            "v0.0.0-test",
            new Uri("https://example.test/mihomo.zip"),
            new string('0', 64),
            new string('0', 64));
        await File.WriteAllTextAsync(
            paths.ManagedCoreMetadata,
            JsonSerializer.Serialize(metadata));
    }

    private static async Task<ConfigurationProfile> ImportConfigurationAsync(
        AppPaths paths,
        string root,
        string name)
    {
        string source = Path.Combine(root, $"{name}.yaml");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            source,
            $"# {name}\nmixed-port: 7890\nproxies: []\nproxy-groups: []\nrules: []\n");
        ConfigurationStore store = new(paths);
        return await store.ImportLocalAsync(source, name);
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

    private sealed class RecordingServicePipeClient : IServicePipeClient
    {
        private int _running;

        public RecordingServicePipeClient(CoreState initialCoreState)
        {
            _running = initialCoreState == CoreState.Running ? 1 : 0;
        }

        public ConcurrentQueue<ServiceCommand> Commands { get; } = new();

        public Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Enqueue(command);
            if (command == ServiceCommand.StartCore)
            {
                Volatile.Write(ref _running, 1);
            }
            else if (command == ServiceCommand.StopCore)
            {
                Volatile.Write(ref _running, 0);
            }

            CoreState core = Volatile.Read(ref _running) == 1
                ? CoreState.Running
                : CoreState.Stopped;
            return Task.FromResult(new ServiceResponse(
                Guid.NewGuid(),
                true,
                TunState.Off,
                Payload: "mihomo.exe",
                Core: core));
        }
    }

    private sealed class RecoveryControllerHandler : HttpMessageHandler
    {
        private int _failAllVersionRequests;
        private int _remainingVersionFailures;

        public void FailAllVersionRequests() => Volatile.Write(ref _failAllVersionRequests, 1);

        public void FailNextVersionRequests(int count) =>
            Volatile.Write(ref _remainingVersionFailures, count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/version")
            {
                if (Volatile.Read(ref _failAllVersionRequests) == 1 || TryConsumeVersionFailure())
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("simulated controller failure")
                    });
                }

                return Task.FromResult(JsonResponse("{\"version\":\"v1.19.30\"}"));
            }

            string body = path switch
            {
                "/configs" => "{\"mode\":\"rule\",\"allow-lan\":false,\"ipv6\":false,\"tun\":{\"enable\":false}}",
                "/proxies" => "{\"proxies\":{}}",
                "/traffic" => "{\"upTotal\":0,\"downTotal\":0,\"up\":0,\"down\":0}\n",
                "/memory" => "{\"inuse\":0}\n",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            };
            return Task.FromResult(JsonResponse(body));
        }

        private bool TryConsumeVersionFailure()
        {
            while (true)
            {
                int remaining = Volatile.Read(ref _remainingVersionFailures);
                if (remaining <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _remainingVersionFailures, remaining - 1, remaining) == remaining)
                {
                    return true;
                }
            }
        }

        private static HttpResponseMessage JsonResponse(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
    }
}
