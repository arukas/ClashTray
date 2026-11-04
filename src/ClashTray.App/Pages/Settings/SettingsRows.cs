using ClashTray.Contracts;
using Microsoft.UI.Xaml;

namespace ClashTray.App;

public sealed class EndpointSettingsRow
{
    public EndpointSettingsRow(EndpointDescriptor endpoint, EndpointId activeId, EndpointId? testingId)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
        bool remote = endpoint.Kind == EndpointKind.Remote;
        Summary = remote
            ? $"{endpoint.BaseUri.AbsoluteUri} · {FormatSecurity(endpoint.Security)}"
            : LocalizationService.Get("EndpointLocalSummary");
        TestVisibility = remote ? Visibility.Visible : Visibility.Collapsed;
        TestEnabled = testingId is null;
        TestLabel = LocalizationService.Get(testingId == endpoint.Id ? "EndpointTesting" : "EndpointTest");
        SelectEnabled = endpoint.Id != activeId;
        SelectLabel = LocalizationService.Get(!SelectEnabled ? "EndpointCurrent" : remote ? "EndpointConnect" : "EndpointReturnLocal");
    }

    public EndpointDescriptor Endpoint { get; }
    public string Summary { get; }
    public Visibility TestVisibility { get; }
    public bool TestEnabled { get; }
    public string TestLabel { get; }
    public bool SelectEnabled { get; }
    public string SelectLabel { get; }

    private static string FormatSecurity(EndpointTransportSecurity security) => LocalizationService.Get(security switch
    {
        EndpointTransportSecurity.HttpsSystemTrust => "EndpointSecurityHttpsSystemTrust",
        EndpointTransportSecurity.HttpsCustomCertificate => "EndpointSecurityHttpsCustomCa",
        EndpointTransportSecurity.HttpExplicitlyConfirmed => "EndpointSecurityHttpConfirmed",
        _ => "EndpointSecurityLoopback"
    });
}

public sealed class ProviderSettingsRow
{
    public ProviderSettingsRow(ProviderStatus provider, bool isRuleProvider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
        IsRuleProvider = isRuleProvider;
        DisplayText = LocalizationService.Format("ProviderListItemFormat",
            LocalizationService.Get(isRuleProvider ? "ProviderItemKindRule" : "ProviderItemKindProxy"),
            provider.Name, provider.Count,
            provider.UpdatedAt?.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty);
    }

    public ProviderStatus Provider { get; }
    public bool IsRuleProvider { get; }
    public string DisplayText { get; }
}
