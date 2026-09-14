using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record EndpointTransportOptions(
    string Secret = "",
    X509Certificate2? CustomCaCertificate = null);

public sealed class EndpointTransport : IDisposable
{
    private readonly string? _authorizationValue;
    private readonly X509Certificate2? _customCaCertificate;
    private readonly HttpClient _httpClient;
    private int _disposed;

    internal EndpointTransport(
        EndpointDescriptor endpoint,
        Uri baseUri,
        Uri webSocketUri,
        HttpClient httpClient,
        string? authorizationValue,
        X509Certificate2? customCaCertificate,
        bool bypassesSystemProxy)
    {
        Endpoint = endpoint;
        BaseUri = baseUri;
        WebSocketUri = webSocketUri;
        _httpClient = httpClient;
        _authorizationValue = authorizationValue;
        _customCaCertificate = customCaCertificate;
        BypassesSystemProxy = bypassesSystemProxy;
    }

    public EndpointDescriptor Endpoint { get; }

    public Uri BaseUri { get; }

    public Uri WebSocketUri { get; }

    public bool BypassesSystemProxy { get; }

    public HttpClient HttpClient
    {
        get
        {
            ThrowIfDisposed();
            return _httpClient;
        }
    }

    public ClientWebSocket CreateWebSocket()
    {
        ThrowIfDisposed();
        ClientWebSocket socket = new();
        socket.Options.Proxy = null;
        if (_authorizationValue is not null)
        {
            socket.Options.SetRequestHeader("Authorization", _authorizationValue);
        }

        if (_customCaCertificate is not null)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                EndpointCertificateValidator.Validate(
                    certificate,
                    chain,
                    errors,
                    _customCaCertificate);
        }

        return socket;
    }

    public Uri BuildWebSocketUri(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            throw new ArgumentException("WebSocket paths must be relative to the endpoint.", nameof(path));
        }

        return new Uri(WebSocketUri, path.TrimStart('/'));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _httpClient.Dispose();
        _customCaCertificate?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

public static class EndpointTransportFactory
{
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler ownership is transferred to HttpClient, which is then owned by EndpointTransport.")]
    public static EndpointTransport Create(
        EndpointDescriptor endpoint,
        EndpointTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        options ??= new EndpointTransportOptions();
        ArgumentNullException.ThrowIfNull(options.Secret);

        if (endpoint.Kind != EndpointKind.Remote)
        {
            throw new ArgumentException("Endpoint transport factory accepts remote endpoints only.", nameof(endpoint));
        }

        EndpointDescriptorValidator.ValidateForActiveSession(endpoint);
        Uri baseUri = EndpointUriNormalizer.NormalizeBaseUri(
            endpoint.BaseUri,
            endpoint.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed);
        ValidateSecurity(endpoint.Security, baseUri, options.CustomCaCertificate);

        Uri webSocketUri = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        }.Uri;

        X509Certificate2? customCaCertificate = CloneCustomCaCertificate(options.CustomCaCertificate);
        HttpClientHandler handler = new();
        handler.UseProxy = false;
        handler.CheckCertificateRevocationList = true;
        HttpClient? httpClient = null;
        try
        {
            if (customCaCertificate is not null)
            {
                handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
                    EndpointCertificateValidator.Validate(
                        certificate,
                        chain,
                        errors,
                        customCaCertificate);
            }

            httpClient = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = baseUri
            };

            string? authorizationValue = null;
            if (options.Secret.Length > 0)
            {
                AuthenticationHeaderValue authorization = new("Bearer", options.Secret);
                authorizationValue = authorization.ToString();
                httpClient.DefaultRequestHeaders.Authorization = authorization;
            }

            return new EndpointTransport(
                endpoint,
                baseUri,
                webSocketUri,
                httpClient,
                authorizationValue,
                customCaCertificate,
                bypassesSystemProxy: !handler.UseProxy);
        }
        catch
        {
            httpClient?.Dispose();
            if (httpClient is null)
            {
                handler.Dispose();
            }

            customCaCertificate?.Dispose();
            throw;
        }
    }

    private static void ValidateSecurity(
        EndpointTransportSecurity security,
        Uri baseUri,
        X509Certificate2? customCaCertificate)
    {
        bool isHttps = baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        EndpointTransportSecurity normalizedSecurity = isHttps
            ? EndpointTransportSecurity.HttpsSystemTrust
            : EndpointTransportSecurity.HttpExplicitlyConfirmed;

        if (security == EndpointTransportSecurity.HttpsCustomCertificate)
        {
            if (!isHttps)
            {
                throw new InvalidOperationException("Custom endpoint certificates require HTTPS.");
            }

            if (customCaCertificate is null)
            {
                throw new InvalidOperationException("A custom CA certificate is required for this endpoint.");
            }

            if (!IsCertificateAuthority(customCaCertificate))
            {
                throw new InvalidOperationException("The custom endpoint certificate must be a CA certificate.");
            }

            return;
        }

        if (security != normalizedSecurity)
        {
            throw new InvalidOperationException("Endpoint transport security does not match its URI.");
        }

        if (customCaCertificate is not null)
        {
            throw new InvalidOperationException("A custom CA certificate is only valid with custom HTTPS trust.");
        }
    }

    private static X509Certificate2? CloneCustomCaCertificate(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        byte[] encoded = certificate.Export(X509ContentType.Cert);
        return X509CertificateLoader.LoadCertificate(encoded);
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate) =>
        certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Any(extension => extension.CertificateAuthority)
        && certificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .All(extension => (extension.KeyUsages & X509KeyUsageFlags.KeyCertSign) != 0);
}

internal static class EndpointCertificateValidator
{
    public static bool Validate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        X509Certificate2 customCaCertificate)
    {
        ArgumentNullException.ThrowIfNull(customCaCertificate);
        if (certificate is null)
        {
            return false;
        }

        const SslPolicyErrors nonChainErrors =
            SslPolicyErrors.RemoteCertificateNotAvailable
            | SslPolicyErrors.RemoteCertificateNameMismatch;
        if ((errors & nonChainErrors) != 0)
        {
            return false;
        }

        if (errors != SslPolicyErrors.None
            && (errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0)
        {
            return false;
        }

        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using X509Chain customChain = new();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.Add(customCaCertificate);
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        customChain.ChainPolicy.DisableCertificateDownloads = true;
        if (chain is not null)
        {
            foreach (X509ChainElement element in chain.ChainElements)
            {
                customChain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return customChain.Build(leaf);
    }
}
