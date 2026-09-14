using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record MihomoControllerSession(
    MihomoApiClient Api,
    EndpointDescriptor Endpoint,
    EndpointCapability Capabilities,
    long Generation);

internal sealed class MihomoControllerSessionRegistry
{
    private MihomoControllerSession? _current;
    private long _generation;

    public MihomoControllerSession? Current => Volatile.Read(ref _current);

    public long Generation => Volatile.Read(ref _generation);

    public MihomoControllerSession Attach(
        MihomoApiClient api,
        EndpointDescriptor endpoint,
        EndpointCapability capabilities)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);

        long generation = Interlocked.Increment(ref _generation);
        MihomoControllerSession session = new(api, endpoint, capabilities, generation);
        Volatile.Write(ref _current, session);
        return session;
    }

    public void Detach()
    {
        Volatile.Write(ref _current, null);
        Interlocked.Increment(ref _generation);
    }

    public MihomoControllerSession Capture() =>
        Current ?? throw new InvalidOperationException("Mihomo 核心尚未运行。");

    public bool IsCurrent(MihomoApiClient api, long generation)
    {
        ArgumentNullException.ThrowIfNull(api);
        MihomoControllerSession? current = Current;
        return current is not null
            && ReferenceEquals(current.Api, api)
            && current.Generation == generation;
    }

    public void EnsureCommandAllowed(
        MihomoApiClient api,
        long generation,
        EndpointCommand command,
        string staleSessionMessage)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(staleSessionMessage);
        MihomoControllerSession? current = Current;
        if (current is null
            || !ReferenceEquals(current.Api, api)
            || current.Generation != generation)
        {
            throw new InvalidOperationException(staleSessionMessage);
        }

        EndpointCommandPolicy.EnsureAllowed(
            current.Endpoint.Kind,
            current.Capabilities,
            command);
    }

    private static void ValidateEndpoint(EndpointDescriptor endpoint)
        => EndpointDescriptorValidator.ValidateForActiveSession(endpoint);
}

internal static class ControllerEndpointFactory
{
    public static EndpointDescriptor CreateLocal(int controllerPort)
    {
        if (controllerPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(controllerPort));
        }

        return new EndpointDescriptor(
            EndpointId.Local,
            EndpointKind.Local,
            EndpointId.Local.Value,
            new Uri($"http://127.0.0.1:{controllerPort}/"),
            EndpointTransportSecurity.Loopback);
    }
}
