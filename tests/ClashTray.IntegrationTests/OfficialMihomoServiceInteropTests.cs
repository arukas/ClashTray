using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Service;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class OfficialMihomoServiceInteropTests
{
    private const string PinnedArchiveSha256 =
        "38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67";

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task ServiceStartsRestartsAndStopsPinnedMihomoWithTunPermanentlyDisabled()
    {
        string? executablePath = FindMihomoExecutable();
        if (executablePath is null)
        {
            Assert.Inconclusive(
                "Official Mihomo payload not found. Set CLASHTRAY_MIHOMO_PATH or build the packaging payload to run this test.");
            return;
        }

        ManagedCoreVerifier.ValidateWindowsAmd64Executable(executablePath);
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClashTrayIntegrationTests",
            Guid.NewGuid().ToString("N"));
        AppPaths paths = new(
            Path.Combine(root, "local"),
            Path.Combine(root, "program"));
        paths.EnsureProgramDataDirectories();
        string runtimeDirectory = Path.Combine(paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        File.Copy(executablePath, paths.ManagedCoreExecutable);
        string executableSha256 = await ComputeSha256Async(paths.ManagedCoreExecutable);
        await File.WriteAllTextAsync(
            paths.ManagedCoreMetadata,
            JsonSerializer.Serialize(new ManagedCoreMetadata(
                BundledMihomo.Version,
                new Uri(
                    $"https://github.com/MetaCubeX/mihomo/releases/download/{BundledMihomo.Version}/mihomo-windows-amd64-{BundledMihomo.Version}.zip"),
                PinnedArchiveSha256,
                executableSha256)));

        int controllerPort = GetAvailableLoopbackPort();
        int mixedPort = GetAvailableLoopbackPort();
        string configurationPath = Path.Combine(runtimeDirectory, "active.yaml");
        await File.WriteAllTextAsync(
            configurationPath,
            $"mixed-port: {mixedPort}{Environment.NewLine}"
            + $"external-controller: 127.0.0.1:{controllerPort}{Environment.NewLine}"
            + $"secret: \"\"{Environment.NewLine}"
            + $"allow-lan: false{Environment.NewLine}"
            + $"ipv6: false{Environment.NewLine}"
            + $"mode: rule{Environment.NewLine}"
            + $"log-level: info{Environment.NewLine}"
            + $"proxies: []{Environment.NewLine}"
            + $"proxy-groups: []{Environment.NewLine}"
            + $"rules: []{Environment.NewLine}"
            + $"tun:{Environment.NewLine}"
            + $"  enable: false{Environment.NewLine}");

        ServiceCorePayload payload = new(
            configurationPath,
            runtimeDirectory,
            controllerPort,
            string.Empty);
        ServiceRequest startRequest = new(
            Guid.NewGuid(),
            ServiceCommand.StartCore,
            JsonSerializer.Serialize(payload),
            ProtocolVersion: ServiceProtocol.CurrentVersion);
        DisabledTunNetworkHealthProbe healthProbe = new();

        try
        {
            await using ServiceRuntimeController controller = new(
                paths,
                managedUserSid: null,
                tunHealthProbe: healthProbe);

            ServiceResponse startResponse = await controller.HandleAsync(
                startRequest,
                CancellationToken.None);
            Assert.IsTrue(startResponse.Succeeded, startResponse.Error);
            Assert.AreEqual(CoreState.Running, startResponse.Core);

            ServiceResponse statusResponse = await WaitForSafeStatusAsync(controller);
            Assert.IsTrue(statusResponse.Succeeded, statusResponse.Error);
            Assert.AreEqual(CoreState.Running, statusResponse.Core);
            Assert.AreEqual(TunState.Off, statusResponse.Tun);

            using HttpClient httpClient = new();
            MihomoApiClient api = new(
                httpClient,
                new Uri($"http://127.0.0.1:{controllerPort}/"),
                string.Empty);
            string version = await WaitForVersionAsync(api);
            Assert.AreEqual(BundledMihomo.Version, version);

            ServiceResponse restartResponse = await controller.HandleAsync(
                new ServiceRequest(
                    Guid.NewGuid(),
                    ServiceCommand.RestartCore,
                    JsonSerializer.Serialize(payload),
                    ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(restartResponse.Succeeded, restartResponse.Error);
            Assert.AreEqual(CoreState.Running, restartResponse.Core);

            ServiceResponse restartedStatus = await WaitForSafeStatusAsync(controller);
            Assert.IsTrue(restartedStatus.Succeeded, restartedStatus.Error);
            Assert.AreEqual(CoreState.Running, restartedStatus.Core);
            Assert.AreEqual(TunState.Off, restartedStatus.Tun);
            string restartedVersion = await WaitForVersionAsync(api);
            Assert.AreEqual(BundledMihomo.Version, restartedVersion);

            ServiceResponse stopResponse = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.StopCore, ProtocolVersion: ServiceProtocol.CurrentVersion),
                CancellationToken.None);
            Assert.IsTrue(stopResponse.Succeeded, stopResponse.Error);
            Assert.AreEqual(CoreState.Stopped, stopResponse.Core);
            Assert.AreEqual(TunState.Off, stopResponse.Tun);
            Assert.AreEqual(0, healthProbe.EnabledProbeCalls);
            Assert.IsGreaterThan(0, healthProbe.DisabledProbeCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                await DeleteTemporaryDirectoryAsync(root);
            }
        }
    }

    private static async Task DeleteTemporaryDirectoryAsync(string directoryPath)
    {
        const int maximumAttempts = 12;
        const int retryDelayMilliseconds = 250;
        Exception? lastException = null;

        // Windows may briefly hold a just-exited executable during file-system scanning.
        // Normalize read-only attributes and retry for a bounded period; persistent locks still fail the test.
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                    directoryPath,
                    "*",
                    SearchOption.AllDirectories))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(directoryPath, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                if (attempt + 1 < maximumAttempts)
                {
                    await Task.Delay(retryDelayMilliseconds).ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            $"Could not delete the isolated Mihomo integration directory after {maximumAttempts} attempts.",
            lastException);
    }

    private static async Task<ServiceResponse> WaitForSafeStatusAsync(
        ServiceRuntimeController controller)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        ServiceResponse? lastResponse = null;
        while (!timeout.IsCancellationRequested)
        {
            lastResponse = await controller.HandleAsync(
                new ServiceRequest(Guid.NewGuid(), ServiceCommand.GetStatus, ProtocolVersion: ServiceProtocol.CurrentVersion),
                timeout.Token);
            if (lastResponse.Succeeded
                && lastResponse.Core == CoreState.Running
                && lastResponse.Tun == TunState.Off)
            {
                return lastResponse;
            }

            await Task.Delay(100, timeout.Token);
        }

        throw new TimeoutException(
            $"Service did not confirm a running core with TUN off. Last response: {lastResponse?.Error ?? "none"}.");
    }

    private static async Task<string> WaitForVersionAsync(MihomoApiClient api)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Exception? lastException = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using JsonDocument version = await api.GetVersionAsync(timeout.Token);
                string? value = MihomoDataParser.ParseVersion(version);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                lastException = new InvalidDataException("Mihomo returned an empty version.");
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }
            catch (SocketException exception)
            {
                lastException = exception;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(100, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException(
            "Mihomo loopback controller did not become ready within 10 seconds.",
            lastException);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static int GetAvailableLoopbackPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindMihomoExecutable() => OfficialMihomoTestSupport.FindMihomoExecutable();

    private sealed class DisabledTunNetworkHealthProbe : ITunNetworkHealthProbe
    {
        public int DisabledProbeCalls { get; private set; }

        public int EnabledProbeCalls { get; private set; }

        public Task<TunNetworkHealth> ProbeAsync(
            MihomoTunConfiguration configuration,
            TunNetworkExpectation expectation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectation == TunNetworkExpectation.Enabled)
            {
                EnabledProbeCalls++;
                throw new InvalidOperationException("This integration test must never probe an enabled TUN.");
            }

            DisabledProbeCalls++;
            return Task.FromResult(new TunNetworkHealth(
                InterfaceFound: false,
                HasValidAddress: false,
                HasRequiredRoute: false,
                HasRequiredDns: false,
                InterfaceName: null,
                Diagnostic: null,
                HasActiveRoute: false,
                HasActiveDns: false,
                ProbeSucceeded: true,
                RouteStateKnown: true));
        }
    }
}
