using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record TargetCommand
{
    public TargetCommand(
        EndpointId endpointId,
        long expectedGeneration,
        EndpointCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId.Value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedGeneration);

        EndpointId = endpointId;
        ExpectedGeneration = expectedGeneration;
        Command = command;
        RequiredCapability = EndpointCommandPolicy.GetRequiredCapability(command);
    }

    public EndpointId EndpointId { get; }

    public long ExpectedGeneration { get; }

    public EndpointCommand Command { get; }

    public EndpointCapability RequiredCapability { get; }
}
