using System.Globalization;
using ClashTray.Core;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace ClashTray.App;

/// <summary>
/// Single access point for MRT Core string resources. Domain and data strings
/// (node names, provider names, raw Mihomo log lines, user input) must never
/// pass through here; only ClashTray's own UI text is localized.
/// </summary>
internal static class LocalizationService
{
    // The ResourceLoader must stay lazy: instantiating any MRT type initializes the
    // resource manager for the process, after which PrimaryLanguageOverride throws
    // InvalidOperationException. ApplyStartupLanguage must run from the App
    // constructor before InitializeComponent loads MRT resources.
    private static readonly Lazy<ResourceLoader> LazyLoader = new(() => new ResourceLoader());

    private static ResourceLoader Loader => LazyLoader.Value;

    public static string Get(string key)
    {
        string value = Loader.GetString(key);
        // Returning the key on a miss keeps the gap visible during development
        // instead of rendering an empty label; the resource tests prevent it.
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Applies the configured language override after the WinUI Application object
    /// exists but before any MRT resources, window, tray menu, or notification is
    /// created. "system" (or an unreadable setting) leaves the Windows preference
    /// chain in charge, which falls back to en-US resources.
    /// </summary>
    public static void ApplyStartupLanguage(AppPaths paths, bool forceChineseForDiagnostics = false)
    {
        string? overrideLanguage = forceChineseForDiagnostics
            ? "zh-CN"
            : SettingsStore.ReadLanguageOverride(paths);
        if (!string.IsNullOrEmpty(overrideLanguage))
        {
            ApplicationLanguages.PrimaryLanguageOverride = overrideLanguage;
        }
    }
}
