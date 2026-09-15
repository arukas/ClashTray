using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class OfficialMihomoInteropTests
{
    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task PinnedMihomoStartsWithTunDisabledAndServesLoopbackController()
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
        Directory.CreateDirectory(root);
        string configurationPath = Path.Combine(root, "controller-only.yaml");
        int controllerPort = GetAvailableLoopbackPort();
        int mixedPort = GetAvailableLoopbackPort();
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

        MihomoProcessManager manager = new MihomoProcessManager();
        try
        {
            bool valid = await manager.ValidateAsync(executablePath, configurationPath, root);
            Assert.IsTrue(valid, "The pinned Mihomo executable rejected the controller-only configuration.");

            await manager.StartAsync(executablePath, configurationPath, root);
            Assert.AreEqual(CoreState.Running, manager.State);

            using HttpClient httpClient = new HttpClient();
            MihomoApiClient api = new(
                httpClient,
                new Uri($"http://127.0.0.1:{controllerPort}/"),
                string.Empty);
            string version = await WaitForVersionAsync(api);
            Assert.AreEqual(BundledMihomo.Version, version);

            using JsonDocument configuration = await api.GetConfigurationAsync(force: false);
            bool? tunEnabled = MihomoDataParser.ParseTunEnabled(configuration);
            Assert.IsNotNull(tunEnabled);
            Assert.IsFalse(tunEnabled.Value, "The real-core smoke configuration must keep TUN disabled.");
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

        throw new TimeoutException("Mihomo loopback controller did not become ready within 10 seconds.", lastException);
    }

    private static int GetAvailableLoopbackPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindMihomoExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("CLASHTRAY_MIHOMO_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "packaging",
                "out",
                "payload-local-full",
                "Core",
                "mihomo.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
