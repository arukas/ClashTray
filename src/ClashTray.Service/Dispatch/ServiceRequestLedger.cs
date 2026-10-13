using System.Security.Cryptography;
using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Service;

internal enum RequestRetentionKind { Observation, Mutation, Cleanup }
internal enum RequestAdmissionKind { Execute, Join, IdentityConflict, ResultUnavailable, Saturated }
internal readonly record struct RequestAdmission(RequestAdmissionKind Kind, ServiceRequestLedger.Entry? Entry = null);

// This ledger owns only request identity and bounded result retention. The
// dispatcher validates protocol/instance identity and confirms cleanup targets.
internal sealed class ServiceRequestLedger
{
    public const int MaximumCapacity = 128;
    private const int MaximumCleanupCapacity = 16;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly TimeProvider _clock;
    private readonly int _capacity;

    internal sealed class Entry(RequestRetentionKind kind, string fingerprint)
    {
        public RequestRetentionKind Kind { get; } = kind;
        public string Fingerprint { get; } = fingerprint;
        public TaskCompletionSource<ServiceResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MaxValue;
    }

    public ServiceRequestLedger(TimeProvider clock, int capacity = MaximumCapacity)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);
        _clock = clock;
        _capacity = capacity;
    }

    public RequestAdmission Admit(ServiceRequest request, Func<RequestRetentionKind> classifyRetention)
    {
        string content = $"{request.ProtocolVersion}:{request.Command}:{request.Payload}:{request.ExpectedServiceInstanceId}";
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            if (_entries.TryGetValue(request.RequestId, out Entry? existing))
            {
                return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? new(RequestAdmissionKind.Join, existing) : new(RequestAdmissionKind.IdentityConflict);
            }
            if (request.RecoveryOnly) { return new(RequestAdmissionKind.ResultUnavailable); }
            RequestRetentionKind kind = classifyRetention();
            int capacity = kind == RequestRetentionKind.Cleanup ? Math.Min(_capacity, MaximumCleanupCapacity) : _capacity;
            if (_entries.Values.Count(entry => entry.Kind == kind) >= capacity)
            { return new(RequestAdmissionKind.Saturated); }
            Entry accepted = new(kind, fingerprint);
            _entries.Add(request.RequestId, accepted);
            return new(RequestAdmissionKind.Execute, accepted);
        }
    }

    public void Complete(Entry entry, ServiceResponse response)
    {
        lock (_gate)
        {
            // Publish expiry and completion atomically. In-flight work cannot
            // expire; the retention window starts when the result is available.
            entry.ExpiresAt = _clock.GetUtcNow().Add(Retention);
            entry.Completion.TrySetResult(response);
            PurgeExpiredUnsafe();
        }
    }

    public void Cancel(Entry entry, CancellationToken lifetimeToken)
    {
        lock (_gate)
        {
            entry.ExpiresAt = _clock.GetUtcNow().Add(Retention);
            entry.Completion.TrySetCanceled(lifetimeToken);
            PurgeExpiredUnsafe();
        }
    }

    private void PurgeExpiredUnsafe()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (Guid id in _entries.Where(pair => pair.Value.Completion.Task.IsCompleted && pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key).ToArray())
        { _entries.Remove(id); }
    }
}
