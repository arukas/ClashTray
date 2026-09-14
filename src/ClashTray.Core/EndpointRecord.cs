using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record EndpointRecord(
    EndpointDescriptor Descriptor,
    string? SecretReference = null,
    string? CertificateReference = null);

public enum EndpointStoreLoadStatus
{
    FirstRun,
    Loaded,
    Recovered,
    ReadFailed
}

public sealed record EndpointStoreLoadResult(
    IReadOnlyList<EndpointRecord> Endpoints,
    EndpointStoreLoadStatus Status,
    bool WasQuarantined,
    string? Message);

public enum EndpointSecretStoreLoadStatus
{
    FirstRun,
    Loaded,
    Recovered,
    ReadFailed
}

public sealed record EndpointSecretStoreLoadResult(
    IReadOnlyDictionary<string, string> Secrets,
    EndpointSecretStoreLoadStatus Status,
    bool WasQuarantined,
    string? Message);

public enum EndpointCertificateStoreLoadStatus
{
    FirstRun,
    Loaded,
    Recovered,
    ReadFailed
}

public sealed record EndpointCertificateStoreLoadResult(
    IReadOnlyDictionary<string, byte[]> Certificates,
    EndpointCertificateStoreLoadStatus Status,
    bool WasQuarantined,
    string? Message);
