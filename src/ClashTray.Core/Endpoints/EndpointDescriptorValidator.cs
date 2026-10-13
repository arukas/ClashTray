using ClashTray.Contracts;

namespace ClashTray.Core;

internal static class EndpointDescriptorValidator
{
    public static void ValidateForActiveSession(EndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.DisplayName);
        if (!endpoint.IsEnabled)
        {
            throw new ArgumentException("Disabled endpoints cannot become the active controller session.", nameof(endpoint));
        }

        ArgumentNullException.ThrowIfNull(endpoint.BaseUri);
        if (endpoint.Kind == EndpointKind.Local)
        {
            if (endpoint.Security != EndpointTransportSecurity.Loopback
                || !endpoint.BaseUri.IsLoopback
                || !endpoint.BaseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Local controller sessions must use an HTTP loopback URI and loopback transport security.",
                    nameof(endpoint));
            }

            return;
        }

        if (endpoint.Kind != EndpointKind.Remote)
        {
            throw new ArgumentException("Unknown endpoint kind.", nameof(endpoint));
        }

        Uri normalizedUri = EndpointUriNormalizer.NormalizeBaseUri(
            endpoint.BaseUri,
            endpoint.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed);
        bool isHttps = normalizedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        EndpointTransportSecurity expectedSecurity = isHttps
            ? EndpointTransportSecurity.HttpsSystemTrust
            : EndpointTransportSecurity.HttpExplicitlyConfirmed;
        if (endpoint.Security == EndpointTransportSecurity.HttpsCustomCertificate && !isHttps)
        {
            throw new ArgumentException(
                "Custom endpoint certificates require HTTPS.",
                nameof(endpoint));
        }

        if (endpoint.Security != EndpointTransportSecurity.HttpsCustomCertificate
            && endpoint.Security != expectedSecurity)
        {
            throw new ArgumentException(
                "Endpoint transport security does not match its URI.",
                nameof(endpoint));
        }
    }
}
