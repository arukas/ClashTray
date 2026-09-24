using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class OfficialMihomoInteropTests
{
    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task OfficialMihomoValidatesConfigAfterManagedMultilineYamlReplacement()
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
        string sourcePath = Path.Combine(root, "source.yaml");
        string outputPath = Path.Combine(root, "managed.yaml");
        string source = "secret: >-" + Environment.NewLine
            + "  stale secret" + Environment.NewLine
            + "  continuation" + Environment.NewLine
            + "external-controller: |" + Environment.NewLine
            + "  0.0.0.0:1" + Environment.NewLine
            + "  stale controller" + Environment.NewLine
            + "allow-lan: 'true" + Environment.NewLine
            + "  stale setting'" + Environment.NewLine
            + "mode: rule" + Environment.NewLine
            + "proxies: []" + Environment.NewLine
            + "proxy-groups: []" + Environment.NewLine
            + "rules: []" + Environment.NewLine
            + "tun:" + Environment.NewLine
            + "  enable: false" + Environment.NewLine;
        await File.WriteAllTextAsync(sourcePath, source);
        HashSet<int> ports = [];
        while (ports.Count < 4)
        {
            ports.Add(GetAvailableLoopbackPort());
        }

        int[] selectedPorts = ports.ToArray();
        AppSettings settings = new(
            ControllerPort: selectedPorts[0],
            HttpPort: selectedPorts[1],
            MixedPort: selectedPorts[2],
            SocksPort: selectedPorts[3]);
        MihomoProcessManager manager = new();
        try
        {
            string builtPath = await RuntimeConfigBuilder.BuildForCoreStartAsync(
                sourcePath,
                outputPath,
                settings,
                externalUiPath: null);
            string built = await File.ReadAllTextAsync(builtPath);
            Assert.IsFalse(built.Contains("stale secret", StringComparison.Ordinal));
            Assert.IsFalse(built.Contains("stale controller", StringComparison.Ordinal));
            Assert.IsFalse(built.Contains("stale setting", StringComparison.Ordinal));
            Assert.IsTrue(built.Contains($"external-controller: 127.0.0.1:{settings.ControllerPort}", StringComparison.Ordinal));
            Assert.IsTrue(built.Contains("secret: ''", StringComparison.Ordinal));

            bool valid = await manager.ValidateAsync(executablePath, builtPath, root);
            Assert.IsTrue(valid, "Official Mihomo rejected the configuration produced by RuntimeConfigBuilder.");
            Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath), "The source YAML must remain unchanged.");
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

    [TestMethod]
    [TestCategory("RequiresOfficialMihomo")]
    public async Task OfficialMihomoUsesManagedValuesBeforeExplicitDocumentEnd()
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
        string sourcePath = Path.Combine(root, "explicit-end.yaml");
        string outputPath = Path.Combine(root, "managed.yaml");
        string source = "---" + Environment.NewLine
            + "mode: direct" + Environment.NewLine
            + "allow-lan: true" + Environment.NewLine
            + "tun:" + Environment.NewLine
            + "  enable: false" + Environment.NewLine
            + "  stack: |-" + Environment.NewLine
            + "    gvisor" + Environment.NewLine
            + "proxies: []" + Environment.NewLine
            + "proxy-groups: []" + Environment.NewLine
            + "rules: []" + Environment.NewLine
            + "..." + Environment.NewLine;
        await File.WriteAllTextAsync(sourcePath, source);

        HashSet<int> ports = [];
        while (ports.Count < 4)
        {
            ports.Add(GetAvailableLoopbackPort());
        }

        int[] selectedPorts = ports.ToArray();
        AppSettings settings = new(
            ControllerPort: selectedPorts[0],
            HttpPort: selectedPorts[1],
            MixedPort: selectedPorts[2],
            SocksPort: selectedPorts[3],
            AllowLan: false,
            TunEnabled: true,
            TunStack: "system");
        MihomoProcessManager manager = new();
        try
        {
            await RuntimeConfigBuilder.BuildForCoreStartAsync(
                sourcePath,
                outputPath,
                settings,
                externalUiPath: null);
            bool valid = await manager.ValidateAsync(executablePath, outputPath, root);
            Assert.IsTrue(valid, "Official Mihomo rejected the managed configuration.");

            await manager.StartAsync(executablePath, outputPath, root);
            using HttpClient httpClient = new();
            MihomoApiClient api = new(
                httpClient,
                new Uri($"http://127.0.0.1:{settings.ControllerPort}/"),
                string.Empty);
            using JsonDocument version = await api.GetVersionAsync();
            Assert.AreEqual(BundledMihomo.Version, MihomoDataParser.ParseVersion(version));

            using JsonDocument configuration = await api.GetConfigurationAsync(force: false);
            JsonElement effective = configuration.RootElement;
            Assert.AreEqual(settings.MixedPort, effective.GetProperty("mixed-port").GetInt32());
            Assert.AreEqual(settings.AllowLan, effective.GetProperty("allow-lan").GetBoolean());
            JsonElement tun = effective.GetProperty("tun");
            Assert.IsFalse(tun.GetProperty("enable").GetBoolean());
            Assert.IsTrue(string.Equals("system", tun.GetProperty("stack").GetString(), StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(source, await File.ReadAllTextAsync(sourcePath));
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

            using CancellationTokenSource webSocketTimeout = new(TimeSpan.FromSeconds(10));
            using ClientWebSocket logsSocket = await api.ConnectWebSocketAsync(
                "/logs?level=info&format=structured",
                webSocketTimeout.Token);
            Assert.AreEqual(WebSocketState.Open, logsSocket.State);
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

    private static string? FindMihomoExecutable() => OfficialMihomoTestSupport.FindMihomoExecutable();


}
