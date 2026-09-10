using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace ClashTray.App;

public sealed partial class MainWindow
{
    private readonly LogoUnlockSequence _logoUnlock = new();
    private readonly UISettings _themeUiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private string _themeName = "system";
    private string? _logoAsset;
    private bool? _nakhimovPaletteApplied;
    private bool _unlockInProgress;
    private bool _themeTrackingStopped;

    private void InitializeThemeTracking()
    {
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        _themeUiSettings.ColorValuesChanged += SystemColorsChanged;
    }

    private void StopThemeTracking()
    {
        _themeTrackingStopped = true;
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        _themeUiSettings.ColorValuesChanged -= SystemColorsChanged;
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) => UpdateThemeAssets();
    private void SystemColorsChanged(UISettings sender, object args) => QueueThemeRefresh();

    private void QueueThemeRefresh() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_themeTrackingStopped) UpdateThemeAssets();
    });

    private void ApplyTheme(string theme)
    {
        var normalizedTheme = theme.Trim().ToLowerInvariant();
        if (normalizedTheme == "nakhimov" && _runtime?.Settings.NakhimovUnlocked != true)
            normalizedTheme = "system";
        _themeName = normalizedTheme;
        RootGrid.RequestedTheme = normalizedTheme switch
        {
            "light" => ElementTheme.Light,
            "dark" or "nakhimov" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyNakhimovPalette(normalizedTheme == "nakhimov");
        UpdateThemeAssets();
        _updatingThemeControls = true;
        try
        {
            SystemThemeOption.IsChecked = normalizedTheme == "system";
            LightThemeOption.IsChecked = normalizedTheme == "light";
            DarkThemeOption.IsChecked = normalizedTheme == "dark";
            NakhimovThemeOption.Visibility = _runtime?.Settings.NakhimovUnlocked == true ? Visibility.Visible : Visibility.Collapsed;
            NakhimovThemeOption.IsChecked = normalizedTheme == "nakhimov";
            ThemeButtonIcon.Glyph = normalizedTheme switch
            {
                "light" => "\uE706",
                "dark" => "\uE708",
                "nakhimov" => "\uE734",
                _ => "\uE790"
            };
            ToolTipService.SetToolTip(ThemeButton, normalizedTheme switch
            {
                "light" => "主题：浅色",
                "dark" => "主题：深色",
                "nakhimov" => "主题：Nakhimov",
                _ => "主题：自动"
            });
        }
        finally { _updatingThemeControls = false; }
    }

    private void ApplyNakhimovPalette(bool enabled)
    {
        if (_nakhimovPaletteApplied == enabled) return;
        _nakhimovPaletteApplied = enabled;
        // Keep the HighContrast dictionary untouched, so Windows retains full control.
        var dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries["Default"];
        var colors = enabled
            ? new[] { Color.FromArgb(255, 24, 37, 33), Color.FromArgb(255, 35, 53, 46), Color.FromArgb(255, 64, 88, 76) }
            : new[] { Color.FromArgb(255, 25, 28, 34), Color.FromArgb(255, 36, 40, 48), Color.FromArgb(255, 54, 60, 70) };
        var keys = new[] { "ClashTrayCanvasBrush", "ClashTraySurfaceBrush", "ClashTrayStrokeBrush" };
        for (var index = 0; index < keys.Length; index++)
            ((SolidColorBrush)dictionary[keys[index]]).Color = colors[index];
    }

    private void UpdateThemeAssets()
    {
        if (ThemeLogo is null) return;
        var asset = _themeName == "nakhimov" ? "Nakhimov"
            : RootGrid.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (_logoAsset != asset)
        {
            ThemeLogo.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Themes/{asset}/logo.png"));
            _logoAsset = asset;
        }
        // The taskbar can use a different theme from the app. Keep its icon legible.
        var darkTaskbar = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            darkTaskbar = key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Taskbar theme unavailable: {exception.Message}");
        }
        if (_accessibilitySettings.HighContrast)
        {
            var background = _themeUiSettings.GetColorValue(UIColorType.Background);
            darkTaskbar = (background.R * 299 + background.G * 587 + background.B * 114) < 128000;
            if (_themeName != "nakhimov")
            {
                var contrastAsset = darkTaskbar ? "Dark" : "Light";
                if (_logoAsset != contrastAsset)
                {
                    ThemeLogo.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Themes/{contrastAsset}/logo.png"));
                    _logoAsset = contrastAsset;
                }
            }
        }
        _trayIcon?.SetTheme(darkTaskbar ? "Dark" : "Light");
    }

    private void ResetLogoClicks()
    {
        _logoUnlock.Reset();
        if (EasterEggTip is not null) EasterEggTip.IsOpen = false;
    }

    private async void LogoButton_Click(object sender, RoutedEventArgs e) => await HandleLogoClickAsync();

    private async Task HandleLogoClickAsync()
    {
        if (_runtime is null || _unlockInProgress) return;
        var remaining = _logoUnlock.Click(Environment.TickCount64);
        if (remaining > 0)
        {
            if (remaining <= 2)
            {
                EasterEggTip.Title = $"再点击 {remaining} 次…";
                EasterEggTip.Subtitle = "有一位特别的访客。";
                EasterEggTip.IsOpen = true;
            }
            return;
        }
        _unlockInProgress = true;
        try
        {
            var wasUnlocked = _runtime.Settings.NakhimovUnlocked;
            await _runtime.UpdateSettingsAsync(_runtime.Settings with { Theme = "nakhimov", NakhimovUnlocked = true });
            ApplyTheme(_runtime.Settings.Theme);
            EasterEggTip.Title = wasUnlocked ? "欢迎回来，Nakhimov" : "Nakhimov 已解锁";
            EasterEggTip.Subtitle = "彩蛋主题已启用，可在主题菜单中自由切换。";
            EasterEggTip.IsOpen = true;
        }
        catch (Exception exception)
        {
            EasterEggTip.IsOpen = false;
            ApplyTheme(_runtime.Settings.Theme);
            ShowError($"彩蛋主题保存失败：{exception.Message}");
        }
        finally { _unlockInProgress = false; }
    }

    private async void ThemeOption_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingThemeControls || _runtime is null || sender is not RadioButton { Tag: string theme }) return;
        ResetLogoClicks();
        ThemeFlyout.Hide();
        try
        {
            await _runtime.UpdateSettingsAsync(_runtime.Settings with { Theme = theme });
            ApplyTheme(_runtime.Settings.Theme);
        }
        catch (Exception exception)
        {
            ApplyTheme(_runtime.Settings.Theme);
            ShowError($"主题切换失败：{exception.Message}");
        }
    }
}
