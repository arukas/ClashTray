using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class CoreUpdateSettingsSection : UserControl
{
    private SettingsPageCommands _commands = null!;
    internal event Action<string?>? StatusChanged;
    private string? Status { set => StatusChanged?.Invoke(value); }

    public CoreUpdateSettingsSection() => InitializeComponent();

    internal void Initialize(SettingsPageCommands commands)
    {
        _commands = commands;
    }

    internal event Action? BusyChanged;
    internal void RefreshActions() => InstallCoreButton.IsEnabled = !_commands.Maintenance.IsBusy;
    private async void InstallCoreButton_Click(object sender, RoutedEventArgs e)
    {
        string sha256 = CoreSha256Box.Text.Trim();
        if (!Uri.TryCreate(CoreDownloadUriBox.Text.Trim(), UriKind.Absolute, out Uri? uri)
            || uri is null
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(CoreVersionBox.Text)
            || (sha256.Length > 0 && (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))))
        {
            Status = LocalizationService.Get("CoreUpdateFieldsRequired");
            return;
        }

        CoreUpdateManifest manifest = new(CoreVersionBox.Text.Trim(), uri, sha256);
        Task<PageCommandResult> pending = _commands.InstallCoreAsync(manifest);
        BusyChanged?.Invoke();
        PageCommandResult result = await pending;
        BusyChanged?.Invoke();
        if (result.CanPresent) { Status = result.Succeeded ? LocalizationService.Get("CoreUpdateDone") : result.Error; }
    }

    private void UseOfficialCoreUriButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CoreVersionBox.Text))
        {
            CoreVersionBox.Text = CoreUpdatePresets.PinnedVersion;
        }

        string version = CoreVersionBox.Text.Trim();
        Uri officialUri;
        try
        {
            officialUri = CoreUpdatePresets.BuildOfficialDownloadUri(version);
        }
        catch (ArgumentException)
        {
            Status = LocalizationService.Get("CoreUpdateFieldsRequired");
            return;
        }

        CoreDownloadUriBox.Text = officialUri.OriginalString;
        if (string.IsNullOrWhiteSpace(CoreSha256Box.Text)
            && version.Equals(CoreUpdatePresets.PinnedVersion, StringComparison.Ordinal))
        {
            CoreSha256Box.Text = CoreUpdatePresets.PinnedSha256;
        }
    }

}
