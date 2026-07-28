using Agentwerke.Application.Agents;
using Agentwerke.Application.Observability;
using Agentwerke.Application.Workflows;
using Agentwerke.Domain.Persistence;

namespace Agentwerke.Application.Tests;

// Shared test doubles for the orchestration service. Extracted from
// WorkflowRunOrchestrationServiceTests when the interaction verbs (#219) needed the same fakes; the
// sweeper (#221) will need them too.

internal sealed class UnusedWorkflowDefinitionRepository : IWorkflowDefinitionRepository
{
    public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<WorkflowDefinition?> FindTrackedAsync(string workflowId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task AddAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}

internal sealed class SingleWorkflowDefinitionRepository(WorkflowDefinition workflow) : IWorkflowDefinitionRepository
{
    public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkflowDefinition>>([workflow]);

    public Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Equals(workflow.Id, workflowId, StringComparison.Ordinal) ? workflow : null);

    public Task<WorkflowDefinition?> FindTrackedAsync(string workflowId, CancellationToken cancellationToken = default) =>
        GetAsync(workflowId, cancellationToken);

    public Task AddAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}

internal sealed class InMemoryWorkflowRunRepository : IWorkflowRunRepository
{
    private WorkflowRun? _run;

    public InMemoryWorkflowRunRepository(WorkflowRun? run = null)
    {
        _run = run;
    }

    public int PendingApprovalDecrements { get; private set; }
    public List<(string RunId, string Type, string Message)> Events { get; } = [];

    public Task<WorkflowRun?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_run is not null && string.Equals(_run.Id, runId, StringComparison.Ordinal) ? _run : null);
    }

    public Task<WorkflowRun> CreatePendingRunAsync(
        string runId,
        string workflowId,
        string workflowName,
        string workflowVersion,
        string? initiator,
        List<string> tags,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        _run = new WorkflowRun
        {
            Id = runId,
            WorkflowId = workflowId,
            WorkflowName = workflowName,
            WorkflowVersion = workflowVersion,
            Status = "pending",
            RequestedBy = initiator ?? string.Empty,
            StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            Tags = tags,
            CorrelationId = correlationId
        };
        return Task.FromResult(_run);
    }

    public Task UpdateRunStatusAsync(string runId, string status, CancellationToken cancellationToken)
    {
        if (_run is null)
        {
            throw new WorkflowRunNotFoundException(runId);
        }

        _run.Status = status;
        return Task.CompletedTask;
    }

    public Task UpdateCurrentStepAsync(string runId, string? currentStep, CancellationToken cancellationToken)
    {
        if (_run is null)
        {
            throw new WorkflowRunNotFoundException(runId);
        }

        _run.CurrentStep = currentStep ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task IncrementPendingApprovalsAsync(string runId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task DecrementPendingApprovalsAsync(string runId, CancellationToken cancellationToken)
    {
        PendingApprovalDecrements++;
        return Task.CompletedTask;
    }

    public Task AppendEventAsync(string runId, string type, string message, CancellationToken cancellationToken)
    {
        Events.Add((runId, type, message));
        return Task.CompletedTask;
    }
}

internal sealed class CapturingRunContextRepository : IRunContextRepository
{
    public List<(string RunId, string Key, string Value, string Kind)> Writes { get; } = [];

    public Task SetAsync(
        string runId,
        string key,
        string value,
        string kind,
        CancellationToken cancellationToken)
    {
        Writes.Add((runId, key, value, kind));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunContextEntry>> GetAllAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var entries = Writes
            .Where(write => write.RunId == runId)
            .Select(static write => new RunContextEntry
            {
                RunId = write.RunId,
                Key = write.Key,
                Value = write.Value,
                Kind = write.Kind
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<RunContextEntry>>(entries);
    }

    public Task<RunContextEntry?> GetAsync(string runId, string key, CancellationToken cancellationToken)
    {
        var write = Writes.LastOrDefault(w => w.RunId == runId && w.Key == key);
        return Task.FromResult<RunContextEntry?>(write == default ? null : new RunContextEntry
        {
            RunId = write.RunId,
            Key = write.Key,
            Value = write.Value,
            Kind = write.Kind
        });
    }

    public Task DeleteAsync(string runId, string key, CancellationToken cancellationToken)
    {
        Writes.RemoveAll(w => w.RunId == runId && w.Key == key);
        return Task.CompletedTask;
    }
}

internal sealed class NoOpRunContextRepository : IRunContextRepository
{
    public Task SetAsync(
        string runId,
        string key,
        string value,
        string kind,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunContextEntry>> GetAllAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RunContextEntry>>([]);
    }

    public Task<RunContextEntry?> GetAsync(string runId, string key, CancellationToken cancellationToken) =>
        Task.FromResult<RunContextEntry?>(null);

    public Task DeleteAsync(string runId, string key, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class InMemoryApprovalRepository : IApprovalRepository
{
    private readonly ApprovalRequest _approval;

    public InMemoryApprovalRepository(ApprovalRequest approval)
    {
        _approval = approval;
    }

    public Task<ApprovalRequest?> GetApprovalAsync(string approvalId, CancellationToken cancellationToken)
    {
        return Task.FromResult(string.Equals(_approval.Id, approvalId, StringComparison.Ordinal) ? _approval : null);
    }

    public Task<ApprovalRequest?> GetPendingApprovalForRunAsync(string runId, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            string.Equals(_approval.RunId, runId, StringComparison.Ordinal) &&
            string.Equals(_approval.Status, "pending", StringComparison.Ordinal)
                ? _approval
                : null);
    }

    public Task AddApprovalAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryAgentInteractionRepository : IAgentInteractionRepository
{
    private readonly Lock _gate = new();

    public List<AgentInteraction> Items { get; } = new();

    public InMemoryAgentInteractionRepository(params AgentInteraction[] seed) => Items.AddRange(seed);

    public Task AddAsync(AgentInteraction interaction, CancellationToken cancellationToken)
    {
        Items.Add(interaction);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentInteraction>> GetByRunAsync(string runId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentInteraction>>(Items.Where(i => i.RunId == runId).ToList());

    public Task<IReadOnlyList<AgentInteraction>> GetPostsForRunAsync(
        string runId, string? fromFilter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentInteraction>>(
            Items.Where(i => i.RunId == runId && i.Kind == AgentInteractionKinds.Post).ToList());

    public Task<AgentInteraction?> GetByIdAsync(string interactionId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(i => i.Id == interactionId));

    public Task<AgentInteraction?> GetPendingForRunAsync(string runId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(i =>
            i.RunId == runId && i.Status == AgentInteractionStatuses.Pending));

    /// <summary>
    /// Reads and writes under one lock so this fake reproduces the database's atomicity: exactly
    /// one concurrent caller may win a transition (#218).
    /// </summary>
    public Task<InteractionTransitionResult> TryTransitionAsync(
        string interactionId,
        string toStatus,
        string? response,
        string? respondedBy,
        string? respondedChannel,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var interaction = Items.FirstOrDefault(i => i.Id == interactionId);
            if (interaction is null)
            {
                return Task.FromResult(
                    new InteractionTransitionResult(InteractionTransitionOutcome.NotFound, null));
            }

            if (AgentInteractionStatuses.IsTerminal(interaction.Status))
            {
                return Task.FromResult(
                    new InteractionTransitionResult(InteractionTransitionOutcome.AlreadyTerminal, interaction));
            }

            if (response is not null)
            {
                interaction.Response = response;
            }

            if (respondedBy is not null)
            {
                interaction.RespondedBy = respondedBy;
                interaction.RespondedAt = DateTimeOffset.UtcNow.ToString("o");
            }

            if (respondedChannel is not null)
            {
                interaction.RespondedChannel = respondedChannel;
            }

            interaction.Status = toStatus;
            interaction.Version++;

            return Task.FromResult(
                new InteractionTransitionResult(InteractionTransitionOutcome.Won, interaction));
        }
    }

    public Task<IReadOnlyList<AgentInteraction>> GetPendingAsync(
        string? runId, string? addresseeType, CancellationToken cancellationToken)
    {
        var query = Items.Where(i => i.Status == AgentInteractionStatuses.Pending);

        if (!string.IsNullOrWhiteSpace(runId))
        {
            query = query.Where(i => i.RunId == runId);
        }

        if (!string.IsNullOrWhiteSpace(addresseeType))
        {
            query = query.Where(i => i.AddresseeType == addresseeType);
        }

        return Task.FromResult<IReadOnlyList<AgentInteraction>>(query.OrderBy(i => i.CreatedAt).ToList());
    }

    public Task<IReadOnlyList<AgentInteraction>> GetDueForExpiryAsync(
        string nowIso, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentInteraction>>(Items
            .Where(i => i.Status == AgentInteractionStatuses.Pending
                        && i.TimeoutAt is not null
                        && string.CompareOrdinal(i.TimeoutAt, nowIso) <= 0)
            .OrderBy(i => i.TimeoutAt)
            .ToList());

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CapturingAuditRepository : IAuditRepository
{
    public List<AuditRecord> Records { get; } = [];

    public Task AddAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal sealed class CapturingRunOutbox : IRunOutbox
{
    public List<(string Operation, string RunId, string? Payload)> Enqueued { get; } = [];

    public Task EnqueueAsync(
        string operation,
        string runId,
        string? payload = null,
        DateTimeOffset? visibleAfter = null,
        CancellationToken ct = default)
    {
        Enqueued.Add((operation, runId, payload));
        return Task.CompletedTask;
    }

    public Task<OutboxEntry?> TryClaimNextAsync(string workerId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task MarkCompletedAsync(string entryId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task MarkFailedAsync(string entryId, string error, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<string>> ListStuckRunIdsAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();
}

internal sealed class StubCorrelationContext : ICorrelationContext
{
    public StubCorrelationContext(string correlationId)
    {
        CorrelationId = correlationId;
    }

    public string CorrelationId { get; }
}

internal sealed class NoOpWorkflowMetrics : IWorkflowMetrics
{
    public void RunStarted(string workflowId, string workflowName) { }
    public void RunCompleted(string workflowId, string workflowName, double durationMs) { }
    public void RunFailed(string workflowId, string workflowName, string reason) { }
    public void StepCompleted(string stepType, string agentName, double durationMs, bool succeeded) { }
    public void ApprovalCreated(string riskLevel) { }
    public void ApprovalDecided(string decision, string riskLevel) { }

    /// <summary>Recorded rather than ignored so tests can assert who won a contested transition.</summary>
    public List<(string ToStatus, string Channel, bool Won)> InteractionTransitions { get; } = [];

    public void InteractionTransition(string toStatus, string channel, bool won) =>
        InteractionTransitions.Add((toStatus, channel, won));
    public void WebhookReceived(string source, bool triggered) { }
    public void ConnectorInvoked(string connectorId, string operation, double durationMs, bool succeeded) { }
    public void ModelInvoked(string agentName, string modelId, int inputTokens, int outputTokens, double latencyMs, double costUsd, bool succeeded) { }
    public void ToolPolicyDenied(string agentName, string policyTag, string kind) { }
    public void RecordWaitingExternalRuns(int total, int stale, double oldestAgeSeconds) { }
}
