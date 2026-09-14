using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Combines local device state with the selected controller session without mixing their commands.
/// </summary>
public static class AppSnapshotComposer
{
    public static AppSnapshot Compose(
        RuntimeSnapshot localSnapshot,
        AppSettings settings,
        IReadOnlyList<EndpointDescriptor>? endpoints = null,
        ControllerSessionSnapshot? activeController = null,
        long controllerGeneration = 0,
        DateTimeOffset? lastConfirmedAt = null)
    {
        AppSnapshot localProjection = RuntimeSnapshotAdapter.ToAppSnapshot(
            localSnapshot,
            settings,
            controllerGeneration,
            lastConfirmedAt,
            endpoints);
        if (activeController is null)
        {
            return localProjection;
        }

        EndpointDescriptor catalogEndpoint = localProjection.Endpoints.FirstOrDefault(endpoint =>
            endpoint.Id == activeController.Endpoint.Id)
            ?? throw new ArgumentException(
                "The active controller must be present in the endpoint catalog.",
                nameof(activeController));
        if (catalogEndpoint.Kind != activeController.Endpoint.Kind)
        {
            throw new ArgumentException(
                "The active controller kind does not match the endpoint catalog.",
                nameof(activeController));
        }

        EndpointCapability capabilities = activeController.Capabilities;
        if (activeController.Endpoint.Kind == EndpointKind.Remote)
        {
            capabilities &= EndpointCapabilityDefaults.Remote;
        }

        return localProjection with
        {
            ActiveController = activeController with { Capabilities = capabilities }
        };
    }
}
