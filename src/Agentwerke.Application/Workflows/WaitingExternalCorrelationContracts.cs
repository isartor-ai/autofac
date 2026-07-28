using Agentwerke.Domain.Persistence;

namespace Agentwerke.Application.Workflows;

/// <summary>
/// Persists which run is currently waiting on which external event (Phase D2, #138), so an
/// inbound webhook event can be matched back to a waiting run and trigger an automatic resume
/// (Phase D1's <c>waiting_external</c> primitive, #137). Implemented in Agentwerke.Infrastructure.
/// </summary>
public interface IWaitingExternalCorrelationRepository
{
    /// <summary>Records (or replaces) the correlation a run is currently waiting on.</summary>
    Task UpsertAsync(WaitingExternalCorrelation correlation, CancellationToken cancellationToken);

    /// <summary>Clears any correlation recorded for a run, e.g. once it resumes or fails.</summary>
    Task RemoveAsync(string runId, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the run id waiting on the given message name + correlation key, if any.
    /// Returns null when no run is currently waiting on a matching event.
    /// </summary>
    Task<string?> FindWaitingRunIdAsync(string messageName, string correlationKey, CancellationToken cancellationToken);

    /// <summary>
    /// Every run currently parked on an external event. Used to publish the waiting_external
    /// gauge so ops can alert on waits that never resolve (#208).
    /// </summary>
    Task<IReadOnlyList<WaitingExternalCorrelation>> ListWaitingAsync(CancellationToken cancellationToken);
}
