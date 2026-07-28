using Agentwerke.Domain.Persistence;

namespace Agentwerke.Application.Observability;

/// <summary>
/// Carries the correlation ID for the current unit of work (HTTP request or background job).
/// Set by middleware at the request boundary; injected into services that need to tag their output.
/// </summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}

/// <summary>Mutable implementation populated by middleware.</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; set; } = string.Empty;
}

/// <summary>
/// Persists immutable audit records for user and agent actions.
/// Implemented in Agentwerke.Infrastructure.
/// </summary>
public interface IAuditRepository
{
    Task AddAsync(AuditRecord record, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Read side of the audit trail for the audit/decision-trace explorer (#189).
/// Separate from <see cref="IAuditRepository"/> so the many write-side stubs need
/// not implement it.
/// </summary>
public interface IAuditQuery
{
    Task<IReadOnlyList<AuditRecord>> QueryAsync(string? runId, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Records workflow execution metrics. Implemented in Agentwerke.Observability (singleton backed by System.Diagnostics.Metrics).
/// </summary>
public interface IWorkflowMetrics
{
    void RunStarted(string workflowId, string workflowName);
    void RunCompleted(string workflowId, string workflowName, double durationMs);
    void RunFailed(string workflowId, string workflowName, string reason);
    void StepCompleted(string stepType, string agentName, double durationMs, bool succeeded);
    void ApprovalCreated(string riskLevel);
    void ApprovalDecided(string decision, string riskLevel);

    /// <summary>
    /// Records a terminal interaction transition (#219). <paramref name="won"/> is false when the
    /// caller lost the race to another channel, a duplicate, or the sweeper.
    ///
    /// Losses are expected once a question fans out to several channels and are not errors — but a
    /// loss rate that climbs while fan-out is off means something is answering twice.
    /// </summary>
    void InteractionTransition(string toStatus, string channel, bool won);
    void WebhookReceived(string source, bool triggered);
    void ConnectorInvoked(string connectorId, string operation, double durationMs, bool succeeded);

    /// <summary>
    /// Records a language-model invocation by an agent: latency, token usage, estimated cost, and outcome.
    /// </summary>
    void ModelInvoked(string agentName, string modelId, int inputTokens, int outputTokens, double latencyMs, double costUsd, bool succeeded);

    /// <summary>
    /// Records a tool-call that was denied by policy enforcement.
    /// <paramref name="kind"/> is "reject" or "escalate".
    /// </summary>
    void ToolPolicyDenied(string agentName, string policyTag, string kind);

    /// <summary>
    /// Publishes the current population of runs parked in <c>waiting_external</c> (#208) so ops can
    /// alert on waits that are stuck. <paramref name="stale"/> counts those parked longer than the
    /// configured threshold; <paramref name="oldestAgeSeconds"/> is 0 when nothing is waiting.
    /// </summary>
    void RecordWaitingExternalRuns(int total, int stale, double oldestAgeSeconds);
}

/// <summary>
/// Thin tracer abstraction over <c>System.Diagnostics.ActivitySource</c>.
/// Lets engine/connector code create spans without taking a direct OTel package reference.
/// </summary>
public interface IWorkflowTracer
{
    ISpan StartSpan(string name);
}

/// <summary>
/// Represents a single trace span. Dispose to end it.
/// </summary>
public interface ISpan : IDisposable
{
    void SetTag(string key, string value);
    void SetError(Exception ex);
}
