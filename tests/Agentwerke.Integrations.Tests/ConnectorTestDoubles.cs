using Agentwerke.AgentSecOps;
using Agentwerke.Application.Observability;
using Agentwerke.Application.Secrets;
using Agentwerke.Domain.Persistence;

namespace Agentwerke.Integrations.Tests;

internal sealed class StubSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values;

    public StubSecretStore(Dictionary<string, string>? values = null)
    {
        _values = values ?? [];
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.TryGetValue(key, out var value);
        return Task.FromResult<string?>(value);
    }
}

internal sealed class AllowAllPolicyEvaluationService : IPolicyEvaluationService
{
    public PolicyDecision Evaluate(PolicyEvaluationRequest request)
    {
        return new PolicyDecision
        {
            Kind = "allow",
            PolicyId = "test-allow",
            PolicyName = "Test Allow",
            Rationale = "Allowed for test",
            RiskScore = 1,
            RiskLevel = "low",
            DecidedAt = DateTimeOffset.UtcNow.ToString("o")
        };
    }
}

internal sealed class NoOpAuditRepository : IAuditRepository
{
    public Task AddAsync(AuditRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NoOpWorkflowMetrics : IWorkflowMetrics
{
    public void ApprovalCreated(string riskLevel) { }
    public void ApprovalDecided(string decision, string riskLevel) { }
    public void ConnectorInvoked(string connectorId, string operation, double durationMs, bool succeeded) { }
    public void ModelInvoked(string agentName, string modelId, int inputTokens, int outputTokens, double latencyMs, double costUsd, bool succeeded) { }
    public void RunCompleted(string workflowId, string workflowName, double durationMs) { }
    public void RunFailed(string workflowId, string workflowName, string reason) { }
    public void RunStarted(string workflowId, string workflowName) { }
    public void StepCompleted(string stepType, string agentName, double durationMs, bool succeeded) { }
    public void ToolPolicyDenied(string agentName, string policyTag, string kind) { }
    public void RecordWaitingExternalRuns(int total, int stale, double oldestAgeSeconds) { }
    public void InteractionTransition(string toStatus, string channel, bool won) { }
    public void WebhookReceived(string source, bool triggered) { }
}

internal sealed class StubCorrelationContext : ICorrelationContext
{
    public string CorrelationId => "corr-test";
}

internal sealed class NoOpWorkflowTracer : IWorkflowTracer
{
    public ISpan StartSpan(string name) => NoOpSpan.Instance;

    private sealed class NoOpSpan : ISpan
    {
        public static readonly NoOpSpan Instance = new();
        public void SetTag(string key, string value) { }
        public void SetError(Exception ex) { }
        public void Dispose() { }
    }
}
