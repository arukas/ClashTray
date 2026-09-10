using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
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
        await VerifyThemeUnlockAsync(directory);
        var empty = _runtime!.Snapshot;
        var sample = empty with
        {
            Core = empty.Core with
            {
                State = CoreState.Running, Version = "v1.19.30", ConfigurationName = "示例配置.yaml",
                ConnectionCount = 103, MemoryBytes = 95 * 1024 * 1024,
                UploadBytesPerSecond = 7168, DownloadBytesPerSecond = 156672,
                UploadBytes = 7864320, DownloadBytes = 222402969,
                TrafficAvailable = true, MemoryAvailable = true
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
        await VerifyNodeScrollingAsync(directory, sample);
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
            if (!ReferenceEquals(PageContent.Content, page.Item1) || OtherPageScrollViewer.Visibility != Visibility.Visible)
                throw new InvalidOperationException($"Navigation failed: {page.Item2}");
            await SaveDiagnosticFrameAsync(directory, $"page-{page.Item2}");
        }
        NavigateTo(_proxyPage, "代理");
    }

    private async Task VerifyNodeScrollingAsync(string directory, RuntimeSnapshot sample)
    {
        var members = Enumerable.Range(1, 200).Select(i => $"香港 · {i:000}").ToArray();
        members[1] = "日本 · 超长节点名称用于检查截断与完整名称提示 · Tokyo Premium 02";
        var crowded = sample with
        {
            ProxyNodes = members.Select((name, i) => new ProxyNode(name, "Shadowsocks", (30 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), i == 0, [])).ToArray(),
            ProxyGroups = [
                new("节点选择", "Selector", members[0], members),
                new("流媒体", "Selector", members[2], members.Take(5).ToArray()),
                new("自动选择", "URLTest", members[1], members.Take(3).ToArray())
            ]
        };
        UpdateSnapshot(crowded);
        RootGrid.UpdateLayout();
        var groups = (StackPanel)_proxyPage!.FindName("GroupsPanel");
        var search = (TextBox)_proxyPage.FindName("NodeSearchBox");
        var listBeforeExpansion = VisualDescendants<ListView>(_proxyPage).ToArray();
        if (listBeforeExpansion.Any(list => list.Items.Count > 0))
            throw new InvalidOperationException("Collapsed groups eagerly created node items.");

        void InvokeGroup(int index)
        {
            var card = (Border)groups.Children[index];
            var button = (Button)((Grid)((StackPanel)card.Child).Children[0]).Children[0];
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        InvokeGroup(0);
        InvokeGroup(1);
        await Task.Delay(200);
        RootGrid.UpdateLayout();
        var lists = VisualDescendants<ListView>(_proxyPage).Where(list => list.Items.Count > 0).ToArray();
        if (lists.Length != 2 || lists[0].Items.Count != 200 || lists[1].Items.Count != 5)
            throw new InvalidOperationException("Multiple expanded groups did not retain their nodes.");
        if (lists.Any(list => ScrollViewer.GetVerticalScrollMode(list) != ScrollMode.Disabled))
            throw new InvalidOperationException("Node list owns a nested scroll viewport.");
        if (VisualDescendants<ScrollViewer>(_proxyPage).Any(scroll => scroll.ScrollableHeight > 1))
            throw new InvalidOperationException("A nested node scrollbar has scrollable content.");
        if (DashboardScrollViewer.ScrollableHeight <= 0)
            throw new InvalidOperationException("Dashboard cannot scroll the expanded node content.");

        foreach (var theme in new[] { "light", "dark" })
        {
            ApplyTheme(theme);
            DashboardScrollViewer.ChangeView(null, 210, null, true);
            await SaveDiagnosticFrameAsync(directory, $"expanded-{theme}");
        }
        DashboardScrollViewer.ChangeView(null, DashboardScrollViewer.ScrollableHeight, null, true);
        await Task.Delay(150);
        RootGrid.UpdateLayout();
        var lastCard = (FrameworkElement)groups.Children.Last();
        var bottom = lastCard.TransformToVisual(DashboardScrollViewer).TransformPoint(new Windows.Foundation.Point(0, lastCard.ActualHeight)).Y;
        if (bottom > DashboardScrollViewer.ActualHeight + 1)
            throw new InvalidOperationException("Last group cannot be reached by the dashboard scrollbar.");
        await SaveDiagnosticFrameAsync(directory, "last-group");

        search.Text = "香港 · 200";
        await Task.Delay(300);
        RootGrid.UpdateLayout();
        var filtered = VisualDescendants<ListView>(_proxyPage).Where(list => list.Items.Count > 0).ToArray();
        if (groups.Children.Count != 1 || filtered.Length != 1 || filtered[0].Items.Count != 1)
            throw new InvalidOperationException("Node search did not isolate the last node.");
        DashboardScrollViewer.ChangeView(null, 100, null, true);
        await SaveDiagnosticFrameAsync(directory, "search-result");
        search.Text = "no-such-node";
        await Task.Delay(300);
        if (((TextBlock)_proxyPage.FindName("NoResultsText")).Visibility != Visibility.Visible || groups.Children.Count != 0)
            throw new InvalidOperationException("Missing search results did not show an empty state.");
        search.Text = "";
        await Task.Delay(300);
        RootGrid.UpdateLayout();
        if (VisualDescendants<ListView>(_proxyPage).Count(list => list.Items.Count > 0) != 2)
            throw new InvalidOperationException("Clearing search lost expansion state.");

        // A metrics-only snapshot must preserve the current node controls.
        var firstCard = groups.Children[0];
        _proxyPage.UpdateSnapshot(crowded with { Core = crowded.Core with { ConnectionCount = 999 } });
        if (!ReferenceEquals(firstCard, groups.Children[0]))
            throw new InvalidOperationException("Metrics update rebuilt node controls.");

        await File.WriteAllTextAsync(Path.Combine(directory, "node-scroll-checks.json"), JsonSerializer.Serialize(new
        {
            Nodes = 200, ExpandedGroups = 2, NestedScrollableViewports = 0,
            LastGroupReachable = true, SearchLastNode = true, NoResults = true,
            ExpansionRestored = true, MetricsPreserveControls = true,
            HighContrast = "Uses system resources; OS high-contrast mode not toggled by this test.",
            InputLimits = "Wheel, touch and physical keyboard require manual verification."
        }, DiagnosticJsonOptions));
        DashboardScrollViewer.ChangeView(null, 0, null, true);
    }

    private async Task VerifyThemeUnlockAsync(string directory)
    {
        if (NakhimovThemeOption.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Locked theme is visible before the gesture.");
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(LogoButton).GetPattern(PatternInterface.Invoke);
        for (var index = 0; index < 4; index++)
        {
            invoke.Invoke();
            await Task.Delay(50);
        }
        if (_runtime!.Settings.NakhimovUnlocked || _themeName == "nakhimov")
            throw new InvalidOperationException("Theme unlocked before the fifth click.");
        invoke.Invoke();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(50);
            if (_runtime.Settings.NakhimovUnlocked && !_unlockInProgress) break;
        }
        if (!_runtime.Settings.NakhimovUnlocked || _themeName != "nakhimov"
            || NakhimovThemeOption.Visibility != Visibility.Visible
            || _logoAsset != "Nakhimov")
            throw new InvalidOperationException("Fifth Logo button invocation did not enable the theme.");
        var store = new ClashTray.Core.SettingsStore(new ClashTray.Core.AppPaths(
            Path.Combine(directory, "user"), Path.Combine(directory, "service")));
        var restored = await store.LoadAsync();
        if (!restored.NakhimovUnlocked || restored.Theme != "nakhimov")
            throw new InvalidOperationException("Unlock state was not persisted.");
        await SaveDiagnosticFrameAsync(directory, "nakhimov-unlocked");
        EasterEggTip.IsOpen = false;
        await SaveDiagnosticFrameAsync(directory, "nakhimov-panel");
        await _runtime.UpdateSettingsAsync(_runtime.Settings with { Theme = "light" });
        ApplyTheme(_runtime.Settings.Theme);
        if (NakhimovThemeOption.Visibility != Visibility.Visible || _logoAsset != "Light")
            throw new InvalidOperationException("Switching back lost unlock state or kept the wrong logo.");
        if (((SolidColorBrush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries["Default"])["ClashTrayCanvasBrush"]).Color
            != Windows.UI.Color.FromArgb(255, 25, 28, 34))
            throw new InvalidOperationException("Normal dark palette was not restored.");
        await File.WriteAllTextAsync(Path.Combine(directory, "theme-unlock-checks.json"), JsonSerializer.Serialize(new
        {
            HiddenUntilUnlocked = true, FourClicksStayLocked = true, FifthButtonInvocationUnlocks = true,
            NakhimovLogoSelected = true, SavedStateReloads = true, SwitchBackKeepsUnlock = true,
            OriginalPaletteRestored = true,
            ManualChecks = "Physical mouse double-click timing and OS high contrast require manual verification."
        }, DiagnosticJsonOptions));
        ResetLogoClicks();
    }
    private async Task SaveDiagnosticFrameAsync(string directory, string name)
    {
        await Task.Delay(200);
        RootGrid.UpdateLayout();
        var renderer = new RenderTargetBitmap();
        await renderer.RenderAsync(RootGrid);
        var pixels = await renderer.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(directory));
        var file = await folder.CreateFileAsync($"{name}.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)renderer.PixelWidth, (uint)renderer.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }}
