using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public static class EndpointUriNormalizer
{
    private const int MaxUriCharacters = 2048;
    private const int MaxDisplayNameCharacters = 128;

    public static EndpointDescriptor CreateRemoteDescriptor(
        EndpointId id,
        string displayName,
        string baseUri,
        bool allowExplicitHttp = false,
        bool isEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUri);
        return CreateRemoteDescriptor(
            id,
            displayName,
            NormalizeBaseUri(baseUri, allowExplicitHttp),
            allowExplicitHttp,
            isEnabled);
    }

    public static EndpointDescriptor CreateRemoteDescriptor(
        EndpointId id,
        string displayName,
        Uri baseUri,
        bool allowExplicitHttp = false,
        bool isEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Trim().Length > MaxDisplayNameCharacters)
        {
            throw new ArgumentException("Endpoint display name is too long.", nameof(displayName));
        }

        Uri normalizedUri = NormalizeBaseUri(baseUri, allowExplicitHttp);
        EndpointTransportSecurity security = normalizedUri.Scheme.Equals(
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase)
            ? EndpointTransportSecurity.HttpsSystemTrust
            : EndpointTransportSecurity.HttpExplicitlyConfirmed;
        return new EndpointDescriptor(
            new EndpointId(id.Value.Trim()),
            EndpointKind.Remote,
            displayName.Trim(),
            normalizedUri,
            security,
            isEnabled);
    }

    public static Uri NormalizeBaseUri(string value, bool allowExplicitHttp = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string trimmed = value.Trim();
        if (trimmed.Length > MaxUriCharacters)
        {
            throw new ArgumentException("Endpoint URI is too long.", nameof(value));
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)
            || parsed is null)
        {
            throw new UriFormatException("Endpoint URI must be an absolute HTTP(S) URI.");
        }

        return NormalizeBaseUri(parsed, allowExplicitHttp);
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "URI schemes and DNS host names are normalized to lowercase by URI convention; uppercase would reduce canonical readability.")]
    public static Uri NormalizeBaseUri(Uri parsed, bool allowExplicitHttp = false)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (!parsed.IsAbsoluteUri)
        {
            throw new UriFormatException("Endpoint URI must be an absolute HTTP(S) URI.");
        }

        if (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (!allowExplicitHttp)
            {
                throw new InvalidOperationException("HTTP endpoints require explicit confirmation.");
            }
        }
        else if (!parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new UriFormatException("Endpoint URI must use HTTP or HTTPS.");
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new UriFormatException("Endpoint URI cannot contain embedded credentials.");
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new UriFormatException("Endpoint URI cannot contain a query or fragment.");
        }

        UriBuilder builder = new(parsed)
        {
            Scheme = parsed.Scheme.ToLowerInvariant(),
            Host = parsed.Host.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty,
            Path = NormalizePath(parsed.AbsolutePath)
        };
        if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80)
            || (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return "/";
        }

        throw new UriFormatException(
            "Endpoint URI must use the controller origin without a path prefix.");
    }
}
