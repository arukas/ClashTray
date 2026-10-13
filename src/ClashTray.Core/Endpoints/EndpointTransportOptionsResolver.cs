using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Owns the certificate loaded from protected endpoint storage until a transport is created.
/// </summary>
public sealed class EndpointTransportOptionsLease : IDisposable
{
    private readonly string _secret;
    private readonly X509Certificate2? _customCaCertificate;
    private int _disposed;

    internal EndpointTransportOptionsLease(
        string secret,
        X509Certificate2? customCaCertificate)
    {
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
        _customCaCertificate = customCaCertificate;
    }

    public EndpointTransportOptions Options
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new EndpointTransportOptions(_secret, _customCaCertificate);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _customCaCertificate?.Dispose();
        }
    }
}

/// <summary>
/// Resolves only local protected endpoint references. It does not create a client or connect.
/// </summary>
public sealed class EndpointTransportOptionsResolver
{
    private readonly EndpointSecretStore _secretStore;
    private readonly EndpointCertificateStore _certificateStore;

    public EndpointTransportOptionsResolver(
        EndpointSecretStore secretStore,
        EndpointCertificateStore certificateStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
    }

    public async Task<EndpointTransportOptionsLease> ResolveAsync(
        EndpointRecord endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EndpointDescriptorValidator.ValidateForActiveSession(endpoint.Descriptor);

        if (endpoint.Descriptor.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed
            && endpoint.InsecureHttpAcknowledgedAtUtc is null)
        {
            throw new EndpointSessionConnectException(
                EndpointSessionState.Failed,
                isTransient: false,
                "该 HTTP 端点尚未确认明文传输风险。");
        }

        string? secretReference = NormalizeOptionalReference(
            endpoint.SecretReference,
            nameof(endpoint.SecretReference));
        string secret = secretReference is null
            ? string.Empty
            : await _secretStore.GetAsync(secretReference, cancellationToken).ConfigureAwait(false)
                ?? throw new EndpointSessionConnectException(
                    EndpointSessionState.AuthenticationFailed,
                    isTransient: false,
                    "端点凭据不可用，请重新保存该端点的 secret。");

        if (endpoint.Descriptor.Security != EndpointTransportSecurity.HttpsCustomCertificate)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.CertificateReference))
            {
                throw new ArgumentException(
                    "自定义 CA 只能用于显式配置的 HTTPS 端点。",
                    nameof(endpoint));
            }

            return new EndpointTransportOptionsLease(secret, customCaCertificate: null);
        }

        string certificateReference = EndpointReferenceValidator.Normalize(
            endpoint.CertificateReference
                ?? throw new EndpointSessionConnectException(
                    EndpointSessionState.CertificateFailed,
                    isTransient: false,
                    "端点未配置自定义 CA。"),
            nameof(endpoint.CertificateReference));
        byte[] certificateBytes = await _certificateStore.GetAsync(
                certificateReference,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EndpointSessionConnectException(
                EndpointSessionState.CertificateFailed,
                isTransient: false,
                "端点自定义 CA 不可用，请重新导入证书。");

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
        }
        catch (CryptographicException exception)
        {
            throw new EndpointSessionConnectException(
                EndpointSessionState.CertificateFailed,
                isTransient: false,
                "端点自定义 CA 无法加载。",
                exception);
        }

        return new EndpointTransportOptionsLease(secret, certificate);
    }

    private static string? NormalizeOptionalReference(string? reference, string parameterName) =>
        string.IsNullOrWhiteSpace(reference)
            ? null
            : EndpointReferenceValidator.Normalize(reference, parameterName);
}
