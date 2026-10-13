using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.App;

internal sealed record EndpointRemovalFeedback(PageCommandResult Command, EndpointRemovalResult? Removal);
internal sealed record EndpointTestFeedback(PageCommandResult Command, EndpointHandshakeResult? Handshake);

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by the WinUI settings page in the App assembly.")]
internal sealed class SettingsEndpointEditor(ClashTrayRuntime runtime, PageCommandRunner commands)
{
    public EndpointId? EditingId { get; set; }
    public EndpointId? TestingId { get; private set; }

    public Task<PageCommandResult> SaveAsync(EndpointDraftSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        EndpointId? editingId = EditingId;
        return commands.RunAsync(() => editingId is EndpointId id
            ? runtime.UpdateRemoteEndpointAsync(id, submission.Descriptor, submission.Secret,
                submission.CustomCaCertificate, submission.InsecureHttpAcknowledgedAtUtc)
            : runtime.ProvisionRemoteEndpointAsync(submission.Descriptor, submission.Secret,
                submission.CustomCaCertificate, submission.InsecureHttpAcknowledgedAtUtc));
    }

    public Task<PageCommandResult> SelectAsync(EndpointId id) =>
        commands.RunAsync(() => runtime.SelectEndpointAsync(id));

    public async Task<EndpointRemovalFeedback> RemoveAsync(EndpointId id)
    {
        EndpointRemovalResult? removal = null;
        PageCommandResult command = await commands.RunAsync(async () =>
            removal = await runtime.RemoveRemoteEndpointAsync(id).ConfigureAwait(false)).ConfigureAwait(false);
        return new(command, removal);
    }

    public async Task<EndpointTestFeedback> TestAsync(EndpointId id)
    {
        TestingId = id;
        try
        {
            EndpointHandshakeResult? handshake = null;
            PageCommandResult command = await commands.RunAsync(async () =>
                handshake = await runtime.TestRemoteEndpointAsync(id).ConfigureAwait(false)).ConfigureAwait(false);
            return new(command, handshake);
        }
        finally
        {
            TestingId = null;
        }
    }
}
