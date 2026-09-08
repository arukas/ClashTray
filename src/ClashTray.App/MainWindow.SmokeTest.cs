using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ClashTray.App;

public sealed partial class MainWindow
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new() { WriteIndented = true };
    // Explicit developer-only smoke mode. Uses synthetic data, isolated storage,
    // no core initialization, no tray registration, and no network changes.
    internal async Task CaptureSmokeTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        if (NativeMethods.IsWindowVisible(_windowHandle)) throw new InvalidOperationException("Startup window was not hidden.");
        TogglePanel();
        if (!NativeMethods.IsWindowVisible(_windowHandle)) throw new InvalidOperationException("Tray selection did not show the panel.");
        HandleDeactivation();
        if (NativeMethods.IsWindowVisible(_windowHandle)) throw new InvalidOperationException("Deactivation did not hide the panel.");
        TogglePanel();
        TogglePanel();
        if (NativeMethods.IsWindowVisible(_windowHandle)) throw new InvalidOperationException("Repeated tray selection did not hide the panel.");
        ShowPanel();
        _isPinned = true; // Keep the diagnostic render stable if another app takes focus.
        var empty = _runtime!.Snapshot;
        var sample = empty with
        {
            Core = empty.Core with
            {
                State = CoreState.Running, Version = "v1.19.30", ConfigurationName = "示例配置.yaml",
                ConnectionCount = 103, MemoryBytes = 95 * 1024 * 1024,
                UploadBytesPerSecond = 7168, DownloadBytesPerSecond = 156672,
                UploadBytes = 7864320, DownloadBytes = 222402969
            },
            Tun = TunState.Off,
            Configurations = [new("sample", "示例配置.yaml", "sample.yaml", null, null, true)],
            ProxyNodes = [
                new("香港 · 01", "Shadowsocks", "34", true, []),
                new("日本 · 02", "VLESS", "60", false, []),
                new("新加坡 · 03", "Trojan", "89", false, []),
                new("DIRECT", "Direct", "0", false, [])
            ],
            ProxyGroups = [
                new("节点选择", "Selector", "香港 · 01", ["香港 · 01", "日本 · 02", "新加坡 · 03"]),
                new("自动选择", "URLTest", "日本 · 02", ["香港 · 01", "日本 · 02"]),
                new("流媒体", "Selector", "新加坡 · 03", ["新加坡 · 03", "日本 · 02"]),
                new("国内服务", "Selector", "DIRECT", ["DIRECT", "香港 · 01"])
            ],
            Providers = [new("示例订阅", "Proxy", "HTTP", new DateTimeOffset(2026,9,8,12,0,0,TimeSpan.Zero), null, 3)]
        };
        foreach (var theme in new[] { "light", "dark" })
        {
            foreach (var populated in new[] { false, true })
            {
                UpdateSnapshot(populated ? sample : empty);
                ApplyTheme(theme);
                if (populated)
                {
                    _trafficHistory.Clear();
                    for (var i = 0; i < 60; i++)
                        _trafficHistory.Enqueue((2000 + 1000 * Math.Sin(i / 3d), 80000 + 60000 * Math.Sin(i / 4d)));
                    DrawTraffic();
                }
                await Task.Delay(350);
                RootGrid.UpdateLayout();
                var renderer = new RenderTargetBitmap();
                await renderer.RenderAsync(RootGrid);
                var pixels = await renderer.GetPixelsAsync();
                var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(directory));
                var file = await folder.CreateFileAsync($"{(populated ? "nodes" : "empty")}-{theme}.png", CreationCollisionOption.ReplaceExisting);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)renderer.PixelWidth, (uint)renderer.PixelHeight, 96, 96, pixels.ToArray());
                await encoder.FlushAsync();
            }
        }
        NativeMethods.GetWindowRect(_windowHandle, out var actual);
        var anchor = GetTrayRect();
        var monitor = NativeMethods.MonitorFromRect(ref anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Monitor query failed.");
        if (actual.Right > info.Work.Right || actual.Bottom > info.Work.Bottom || actual.Left < info.Work.Left || actual.Top < info.Work.Top)
            throw new InvalidOperationException("Flyout escaped the work area.");
        await File.WriteAllTextAsync(Path.Combine(directory, "geometry.json"), JsonSerializer.Serialize(new
        {
            Window = new { actual.Left, actual.Top, actual.Right, actual.Bottom },
            WorkArea = new { info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom },
            RootGrid.ActualWidth, RootGrid.ActualHeight, RightGap = info.Work.Right - actual.Right,
            BottomGap = info.Work.Bottom - actual.Bottom,
            NodesTop = ProxyPageContent.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point()).Y,
            ScreenshotData = "synthetic; core and service operations disabled",
            StartupHidden = true, TrayToggle = "passed", DeactivationDismissal = "passed"
        }, DiagnosticJsonOptions));
        foreach (var page in new[] { (_rulesPage as UIElement, "规则"), (_connectionsPage as UIElement, "连接"), (_logsPage as UIElement, "日志"), (_settingsPage as UIElement, "设置") })
        {
            NavigateTo(page.Item1, page.Item2);
            await Task.Delay(100);
            RootGrid.UpdateLayout();
            if (PageContent.Content != page.Item1 || OtherPageScrollViewer.Visibility != Visibility.Visible)
                throw new InvalidOperationException($"Navigation failed: {page.Item2}");
        }
        NavigateTo(_proxyPage, "代理");
    }
}
