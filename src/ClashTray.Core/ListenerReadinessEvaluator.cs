namespace ClashTray.Core;

internal enum ListenerOwnerState
{
    Missing,
    Owned,
    Foreign,
    Unknown
}

internal sealed record ListenerOwnerObservation(ListenerOwnerState State, string? Detail = null);

internal enum ListenerReadinessDisposition
{
    Ready,
    WaitingForListener,
    OwnershipUnknown,
    ForeignOwner
}

internal sealed record ListenerReadinessResult(
    ListenerReadinessDisposition Disposition,
    string? ListenerName = null,
    string? Detail = null);

/// <summary>
/// Applies one ordered readiness policy to managed proxy and additional
/// listeners. The inspection delegate keeps transition tests deterministic.
/// </summary>
internal static class ListenerReadinessEvaluator
{
    public static ListenerReadinessResult Evaluate(
        IReadOnlyList<LocalPortBinding> proxyListeners,
        IReadOnlyList<LocalPortBinding> additionalListeners,
        Func<LocalPortBinding, ListenerOwnerObservation> inspect)
    {
        ArgumentNullException.ThrowIfNull(proxyListeners);
        ArgumentNullException.ThrowIfNull(additionalListeners);
        ArgumentNullException.ThrowIfNull(inspect);

        ListenerReadinessResult proxyResult = EvaluatePhase(proxyListeners, inspect);
        if (proxyResult.Disposition != ListenerReadinessDisposition.Ready)
        {
            return proxyResult;
        }

        return EvaluatePhase(additionalListeners, inspect);
    }

    private static ListenerReadinessResult EvaluatePhase(
        IReadOnlyList<LocalPortBinding> listeners,
        Func<LocalPortBinding, ListenerOwnerObservation> inspect)
    {
        int start = 0;
        while (start < listeners.Count)
        {
            LocalPortBinding first = listeners[start];
            List<(LocalPortBinding Binding, ListenerOwnerObservation Observation)> observations = [];
            int index = start;
            while (index < listeners.Count
                && listeners[index].Port == first.Port
                && listeners[index].Address.Equals(first.Address))
            {
                LocalPortBinding binding = listeners[index++];
                observations.Add((binding, inspect(binding)));
            }

            ListenerReadinessResult result = ClassifyPortGroup(observations);
            if (result.Disposition != ListenerReadinessDisposition.Ready)
            {
                return result;
            }

            start = index;
        }

        return new ListenerReadinessResult(ListenerReadinessDisposition.Ready);
    }

    private static ListenerReadinessResult ClassifyPortGroup(
        List<(LocalPortBinding Binding, ListenerOwnerObservation Observation)> observations)
    {
        if (observations.Count == 0)
        {
            return new ListenerReadinessResult(ListenerReadinessDisposition.Ready);
        }

        (LocalPortBinding Binding, ListenerOwnerObservation Observation)[] foreign = observations
            .Where(item => item.Observation.State == ListenerOwnerState.Foreign)
            .Take(1)
            .ToArray();
        if (foreign.Length > 0)
        {
            return new ListenerReadinessResult(
                ListenerReadinessDisposition.ForeignOwner,
                foreign[0].Binding.Name,
                foreign[0].Observation.Detail);
        }

        if (observations.All(item => item.Observation.State == ListenerOwnerState.Owned))
        {
            return new ListenerReadinessResult(ListenerReadinessDisposition.Ready);
        }

        (LocalPortBinding Binding, ListenerOwnerObservation Observation)[] unknown = observations
            .Where(item => item.Observation.State == ListenerOwnerState.Unknown)
            .Take(1)
            .ToArray();
        if (unknown.Length > 0)
        {
            return new ListenerReadinessResult(
                ListenerReadinessDisposition.OwnershipUnknown,
                unknown[0].Binding.Name,
                unknown[0].Observation.Detail);
        }

        (LocalPortBinding Binding, ListenerOwnerObservation Observation) missing = observations
            .First(item => item.Observation.State == ListenerOwnerState.Missing);
        return new ListenerReadinessResult(
            ListenerReadinessDisposition.WaitingForListener,
            missing.Binding.Name,
            missing.Observation.Detail);
    }
}
