using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeServiceReconciliationTests
{
    [TestMethod]
    public async Task UnknownServiceStartAdoptsRunningCoreWithoutLocalFallback()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new(RuntimeTestHelpers.CreatePortSafeSettings());
        UnknownStartService service = new(reportRunningAfterUnknown: true, settings.Settings);
        using ControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(
            httpClient,
            new Uri($"http://127.0.0.1:{settings.Settings.ControllerPort}/"),
            string.Empty);
        await PrepareManagedCoreAsync(paths);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            candidateValidator: new AcceptingCandidateValidator(),
            controllerApiFactory: () => api);

        try
        {
            await runtime.InitializeAsync();
            await ImportConfigurationAsync(runtime, root);

            await runtime.StartCoreAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.IsTrue(runtime.IsCoreHealthConfirmedForTesting);
            CollectionAssert.AreEqual(
                new[] { ServiceCommand.GetStatus, ServiceCommand.StartCore, ServiceCommand.GetStatus },
                service.Commands.Take(3).ToArray());

            await runtime.DisposeAsync();
            Assert.IsTrue(service.Commands.Contains(ServiceCommand.StopCore));
        }
        finally
        {
            await runtime.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task UnknownServiceStartRetainsStopOwnershipWhenStatusIsNotYetRunning()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new(RuntimeTestHelpers.CreatePortSafeSettings());
        UnknownStartService service = new(reportRunningAfterUnknown: false, settings.Settings);
        await PrepareManagedCoreAsync(paths);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            candidateValidator: new AcceptingCandidateValidator());

        try
        {
            await runtime.InitializeAsync();
            await ImportConfigurationAsync(runtime, root);

            await runtime.StartCoreAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.AreEqual(CoreState.Failed, runtime.Snapshot.Core.State);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "结果尚未确认", StringComparison.Ordinal);

            await runtime.DisposeAsync();
            Assert.IsTrue(service.Commands.Contains(ServiceCommand.StopCore));
        }
        finally
        {
            await runtime.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public Task DelayedServiceStartAdoptsBindingBeforeCreatingController() =>
        VerifyDelayedServiceRecoveryAsync(rejectOldMetadataFirst: false);

    [TestMethod]
    public Task DelayedServiceRecoveryRejectsOldMetadataThenAdoptsCompatibleBinding() =>
        VerifyDelayedServiceRecoveryAsync(rejectOldMetadataFirst: true);

    private static async Task VerifyDelayedServiceRecoveryAsync(bool rejectOldMetadataFirst)
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new(RuntimeTestHelpers.CreatePortSafeSettings());
        UnknownStartService service = new(reportRunningAfterUnknown: false, settings.Settings);
        using ControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient,
            new Uri($"http://127.0.0.1:{settings.Settings.ControllerPort}/"), string.Empty);
        TaskCompletionSource<CoreRuntimeBinding?> firstControllerBinding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource healthy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incompatible = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClashTrayRuntime? observedRuntime = null;
        await PrepareManagedCoreAsync(paths);
        await using ClashTrayRuntime runtime = new(paths, null, service, settings,
            candidateValidator: new AcceptingCandidateValidator(),
            controllerApiFactory: () =>
            {
                firstControllerBinding.TrySetResult(observedRuntime!.ActiveRuntimeBinding);
                return api;
            });
        observedRuntime = runtime;
        runtime.SnapshotChanged += (_, _) =>
        {
            if (runtime.Snapshot.ErrorMessage?.Contains("同步升级", StringComparison.Ordinal) == true)
            {
                incompatible.TrySetResult();
            }
            if (runtime.IsCoreHealthConfirmedForTesting)
            {
                healthy.TrySetResult();
            }
        };

        try
        {
            await runtime.InitializeAsync();
            await ImportConfigurationAsync(runtime, root);
            await runtime.StartCoreAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(CoreState.Failed, runtime.Snapshot.Core.State);

            CoreRuntimeBinding compatibleBinding = service.RuntimeBinding;
            if (rejectOldMetadataFirst)
            {
                service.ReportBinding(compatibleBinding with { ListenerBindings = null });
            }

            service.ReportRunning();
            if (rejectOldMetadataFirst)
            {
                await incompatible.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(firstControllerBinding.Task.IsCompleted);
                Assert.IsNull(runtime.ActiveRuntimeBinding);
                service.ReportBinding(compatibleBinding);
            }

            CoreRuntimeBinding? adopted = await firstControllerBinding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreSame(service.RuntimeBinding, adopted,
                "The first polling status must be adopted before the controller is created.");
            await healthy.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
            Assert.AreEqual(rejectOldMetadataFirst ? 4 : 3, service.Commands.Count(command => command == ServiceCommand.GetStatus),
                "Recovery must not require a second status query through the exception path.");
        }
        finally
        {
            await runtime.DisposeAsync();
            DeleteRoot(root);
        }
    }

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

    private static async Task ImportConfigurationAsync(ClashTrayRuntime runtime, string root)
    {
        string source = Path.Combine(root, "source.yaml");
        await File.WriteAllTextAsync(
            source,
            "mixed-port: 7890\nproxies: []\nproxy-groups: []\nrules: []\n");
        await runtime.ImportLocalConfigurationAsync(source, "test");
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

    private sealed class AcceptingCandidateValidator : IConfigurationCandidateValidator
    {
        public Task ValidateAsync(
            string candidatePath,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnknownStartService : IServicePipeClient
    {
        private readonly bool _reportRunningAfterUnknown;
        private int _running;
        private CoreRuntimeBinding _binding;

        public UnknownStartService(bool reportRunningAfterUnknown, AppSettings settings)
        {
            _reportRunningAfterUnknown = reportRunningAfterUnknown;
            _binding = RuntimeTestHelpers.CreateRuntimeBinding(settings);
        }

        public ConcurrentQueue<ServiceCommand> Commands { get; } = new();

        public CoreRuntimeBinding RuntimeBinding => Volatile.Read(ref _binding);

        public void ReportBinding(CoreRuntimeBinding binding) => Volatile.Write(ref _binding, binding);

        public void ReportRunning() => Volatile.Write(ref _running, 1);

        public Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Enqueue(command);
            if (command == ServiceCommand.StartCore)
            {
                if (_reportRunningAfterUnknown)
                {
                    Volatile.Write(ref _running, 1);
                }

                return Task.FromException<ServiceResponse>(new ServiceRequestUnknownException(
                    "response lost",
                    null,
                    Guid.NewGuid(),
                    ServiceDispatchState.DispatchedAwaitingResult));
            }

            if (command == ServiceCommand.StopCore)
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
                Core: core,
                RuntimeBinding: core == CoreState.Running
                    ? RuntimeBinding
                    : null));
        }
    }

    private sealed class ControllerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body = request.RequestUri?.AbsolutePath switch
            {
                "/version" => "{\"version\":\"v1.19.30\"}",
                "/configs" => "{\"mode\":\"rule\",\"tun\":{\"enable\":false}}",
                "/proxies" => "{\"proxies\":{}}",
                "/traffic" => "{\"upTotal\":0,\"downTotal\":0,\"up\":0,\"down\":0}\n",
                "/memory" => "{\"inuse\":0}\n",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
