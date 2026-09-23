using System.Text;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.App;

internal enum EndpointTransportChoice
{
    HttpsSystemTrust,
    HttpsCustomCertificate,
    HttpExplicitlyConfirmed
}

internal sealed record EndpointDraftSubmission(
    EndpointDescriptor Descriptor,
    string? Secret,
    ReadOnlyMemory<byte>? CustomCaCertificate,
    DateTimeOffset? InsecureHttpAcknowledgedAtUtc);

internal static class EndpointDraftMapper
{
    public static EndpointTransportChoice ParseTransportChoice(string? tag) => tag switch
    {
        "https-system" => EndpointTransportChoice.HttpsSystemTrust,
        "https-custom" => EndpointTransportChoice.HttpsCustomCertificate,
        "http-explicit" => EndpointTransportChoice.HttpExplicitlyConfirmed,
        _ => throw new ArgumentException("Unsupported endpoint transport choice.", nameof(tag))
    };

    public static EndpointDraftSubmission CreateSubmission(
        EndpointId endpointId,
        string displayName,
        Uri endpointUri,
        EndpointTransportChoice transport,
        bool httpRiskConfirmed,
        string? secret,
        string? customCaPem,
        bool isEditing,
        DateTimeOffset nowUtc)
    {
        bool isExplicitHttp = transport == EndpointTransportChoice.HttpExplicitlyConfirmed;
        bool isCustomHttps = transport == EndpointTransportChoice.HttpsCustomCertificate;
        if (isExplicitHttp && !httpRiskConfirmed)
        {
            throw new ArgumentException("HTTP endpoints require explicit confirmation.", nameof(httpRiskConfirmed));
        }

        if (isCustomHttps && string.IsNullOrWhiteSpace(customCaPem) && !isEditing)
        {
            throw new ArgumentException("A custom CA is required for a new custom HTTPS endpoint.", nameof(customCaPem));
        }

        if (!isCustomHttps && !string.IsNullOrWhiteSpace(customCaPem))
        {
            throw new ArgumentException("A custom CA can only be used with custom HTTPS trust.", nameof(customCaPem));
        }

        bool uriUsesHttp = endpointUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (uriUsesHttp != isExplicitHttp)
        {
            throw new ArgumentException("The selected endpoint transport does not match the URI scheme.", nameof(endpointUri));
        }

        EndpointTransportSecurity security = transport switch
        {
            EndpointTransportChoice.HttpsSystemTrust => EndpointTransportSecurity.HttpsSystemTrust,
            EndpointTransportChoice.HttpsCustomCertificate => EndpointTransportSecurity.HttpsCustomCertificate,
            EndpointTransportChoice.HttpExplicitlyConfirmed => EndpointTransportSecurity.HttpExplicitlyConfirmed,
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
                endpointId,
                displayName,
                endpointUri,
                allowExplicitHttp: isExplicitHttp)
            with { Security = security };
        string? normalizedSecret = string.IsNullOrEmpty(secret) ? null : secret;
        ReadOnlyMemory<byte>? customCaCertificate = null;
        if (isCustomHttps && !string.IsNullOrWhiteSpace(customCaPem))
        {
            customCaCertificate = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(customCaPem));
        }
        return new EndpointDraftSubmission(
            descriptor,
            normalizedSecret,
            customCaCertificate,
            isExplicitHttp ? nowUtc : null);
    }
}