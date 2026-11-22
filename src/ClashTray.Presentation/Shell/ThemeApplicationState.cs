namespace ClashTray.App;

internal sealed class ThemeApplicationState
{
    private string? _appliedTheme;
    private bool? _nakhimovUnlocked;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Theme names are persisted and consumed as lowercase values.")]
    public string? GetThemeToApply(string theme, bool nakhimovUnlocked)
    {
        ArgumentNullException.ThrowIfNull(theme);
        string normalizedTheme = theme.Trim().ToLowerInvariant();
        if (normalizedTheme == "nakhimov" && !nakhimovUnlocked)
        {
            normalizedTheme = "system";
        }

        if (_appliedTheme == normalizedTheme && _nakhimovUnlocked == nakhimovUnlocked)
        {
            return null;
        }

        _appliedTheme = normalizedTheme;
        _nakhimovUnlocked = nakhimovUnlocked;
        return normalizedTheme;
    }
}
