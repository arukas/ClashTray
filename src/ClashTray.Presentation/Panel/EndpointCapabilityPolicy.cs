using ClashTray.Contracts;

namespace ClashTray.App;

internal static class EndpointCapabilityPolicy
{
    public static bool Allows(
        bool controllerWritable,
        EndpointCapability available,
        EndpointCapability required) =>
        controllerWritable && (available & required) == required;
}
