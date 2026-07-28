using Agentwerke.Application.Observability;
using System.Diagnostics.Metrics;

namespace Agentwerke.Observability;

public sealed class WorkflowMetrics : IWorkflowMetrics, IDisposable
{
    public const string MeterName = "Agentwerke.Workflows";

    private readonly Meter _meter;
    private readonly Counter<long> _runsStarted;
    private readonly Counter<long> _runsCompleted;
    private readonly Counter<long> _runsFailed;
    private readonly Histogram<double> _runDurationMs;
    private readonly Histogram<double> _stepDurationMs;
    private readonly Counter<long> _stepsSucceeded;
    private readonly Counter<long> _stepsFailed;
    private readonly Counter<long> _approvalsCreated;
    private readonly Counter<long> _approvalsDecided;
    private readonly Counter<long> _interactionTransitions;
    private readonly Counter<long> _webhooksReceived;
    private readonly Counter<long> _connectorsSucceeded;
    private readonly Counter<long> _connectorsFailed;
    private readonly Histogram<double> _connectorDurationMs;
    private readonly Counter<long> _modelInvocationsSucceeded;
    private readonly Counter<long> _modelInvocationsFailed;
    private readonly Histogram<double> _modelLatencyMs;
    private readonly Counter<long> _modelInputTokens;
    private readonly Counter<long> _modelOutputTokens;
    private readonly Histogram<double> _modelCostUsd;
    private readonly Counter<long> _toolPolicyDenials;

    // Last observation of the waiting_external population, published through observable gauges
    // so every Prometheus scrape reports the most recent sweep (#208).
    private int _waitingExternalRuns;
    private int _waitingExternalRunsStale;
    private double _waitingExternalOldestAgeSeconds;

    public WorkflowMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _runsStarted = _meter.CreateCounter<long>("workflow.runs.started", description: "Total workflow runs started.");
        _runsCompleted = _meter.CreateCounter<long>("workflow.runs.completed", description: "Workflow runs completed successfully.");
        _runsFailed = _meter.CreateCounter<long>("workflow.runs.failed", description: "Workflow runs that ended in failure.");
        _runDurationMs = _meter.CreateHistogram<double>("workflow.run.duration_ms", unit: "ms", description: "End-to-end workflow run duration.");

        _stepsSucceeded = _meter.CreateCounter<long>("workflow.steps.succeeded", description: "Service task steps completed successfully.");
        _stepsFailed = _meter.CreateCounter<long>("workflow.steps.failed", description: "Service task steps that failed.");
        _stepDurationMs = _meter.CreateHistogram<double>("workflow.step.duration_ms", unit: "ms", description: "Service task step execution duration.");

        _approvalsCreated = _meter.CreateCounter<long>("workflow.approvals.created", description: "Approval requests created.");
        _approvalsDecided = _meter.CreateCounter<long>("workflow.approvals.decided", description: "Approval decisions recorded.");
        _interactionTransitions = _meter.CreateCounter<long>("workflow.interactions.transitions", description: "Terminal agent-interaction transitions, tagged by whether the caller won the race.");

        _webhooksReceived = _meter.CreateCounter<long>("workflow.webhooks.received", description: "Inbound webhooks received.");
        _connectorsSucceeded = _meter.CreateCounter<long>("workflow.connectors.succeeded", description: "Connector invocations completed successfully.");
        _connectorsFailed = _meter.CreateCounter<long>("workflow.connectors.failed", description: "Connector invocations that failed or were blocked.");
        _connectorDurationMs = _meter.CreateHistogram<double>("workflow.connector.duration_ms", unit: "ms", description: "Connector invocation duration.");

        _modelInvocationsSucceeded = _meter.CreateCounter<long>("agent.model.invocations.succeeded", description: "Successful language-model invocations.");
        _modelInvocationsFailed = _meter.CreateCounter<long>("agent.model.invocations.failed", description: "Failed language-model invocations.");
        _modelLatencyMs = _meter.CreateHistogram<double>("agent.model.latency_ms", unit: "ms", description: "Language-model round-trip latency including tool iterations.");
        _modelInputTokens = _meter.CreateCounter<long>("agent.model.tokens.input", description: "Cumulative input tokens consumed by language-model invocations.");
        _modelOutputTokens = _meter.CreateCounter<long>("agent.model.tokens.output", description: "Cumulative output tokens produced by language-model invocations.");
        _modelCostUsd = _meter.CreateHistogram<double>("agent.model.cost_usd", unit: "USD", description: "Estimated USD cost per language-model invocation.");
        _toolPolicyDenials = _meter.CreateCounter<long>("agent.tool.policy_denials", description: "Tool calls denied or escalated by policy enforcement.");

        _meter.CreateObservableGauge(
            "workflow.runs.waiting_external",
            () => Volatile.Read(ref _waitingExternalRuns),
            description: "Runs currently parked waiting on an external event.");
        _meter.CreateObservableGauge(
            "workflow.runs.waiting_external.stale",
            () => Volatile.Read(ref _waitingExternalRunsStale),
            description: "Runs parked waiting on an external event for longer than the configured threshold.");
        _meter.CreateObservableGauge(
            "workflow.runs.waiting_external.oldest_age_seconds",
            () => Volatile.Read(ref _waitingExternalOldestAgeSeconds),
            unit: "s",
            description: "Age of the longest-parked run waiting on an external event.");
    }

    public void RunStarted(string workflowId, string workflowName)
    {
        _runsStarted.Add(1,
            new KeyValuePair<string, object?>("workflow.id", workflowId),
            new KeyValuePair<string, object?>("workflow.name", workflowName));
    }

    public void RunCompleted(string workflowId, string workflowName, double durationMs)
    {
        _runsCompleted.Add(1,
            new KeyValuePair<string, object?>("workflow.id", workflowId),
            new KeyValuePair<string, object?>("workflow.name", workflowName));
        _runDurationMs.Record(durationMs,
            new KeyValuePair<string, object?>("workflow.id", workflowId),
            new KeyValuePair<string, object?>("workflow.name", workflowName));
    }

    public void RunFailed(string workflowId, string workflowName, string reason)
    {
        _runsFailed.Add(1,
            new KeyValuePair<string, object?>("workflow.id", workflowId),
            new KeyValuePair<string, object?>("workflow.name", workflowName),
            new KeyValuePair<string, object?>("failure.reason", reason));
    }

    public void StepCompleted(string stepType, string agentName, double durationMs, bool succeeded)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("step.type", stepType),
            new KeyValuePair<string, object?>("agent.name", agentName)
        };

        if (succeeded)
            _stepsSucceeded.Add(1, tags);
        else
            _stepsFailed.Add(1, tags);

        _stepDurationMs.Record(durationMs, tags);
    }

    public void ApprovalCreated(string riskLevel)
    {
        _approvalsCreated.Add(1, new KeyValuePair<string, object?>("risk.level", riskLevel));
    }

    public void ApprovalDecided(string decision, string riskLevel)
    {
        _approvalsDecided.Add(1,
            new KeyValuePair<string, object?>("decision", decision),
            new KeyValuePair<string, object?>("risk.level", riskLevel));
    }

    public void InteractionTransition(string toStatus, string channel, bool won)
    {
        _interactionTransitions.Add(1,
            new KeyValuePair<string, object?>("to.status", toStatus),
            new KeyValuePair<string, object?>("channel", channel),
            new KeyValuePair<string, object?>("won", won));
    }

    public void WebhookReceived(string source, bool triggered)
    {
        _webhooksReceived.Add(1,
            new KeyValuePair<string, object?>("webhook.source", source),
            new KeyValuePair<string, object?>("workflow.triggered", triggered));
    }

    public void ConnectorInvoked(string connectorId, string operation, double durationMs, bool succeeded)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("connector.id", connectorId),
            new KeyValuePair<string, object?>("connector.operation", operation)
        };

        if (succeeded)
        {
            _connectorsSucceeded.Add(1, tags);
        }
        else
        {
            _connectorsFailed.Add(1, tags);
        }

        _connectorDurationMs.Record(durationMs, tags);
    }

    public void ModelInvoked(string agentName, string modelId, int inputTokens, int outputTokens, double latencyMs, double costUsd, bool succeeded)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("agent.name", agentName),
            new KeyValuePair<string, object?>("model.id", modelId)
        };

        if (succeeded)
            _modelInvocationsSucceeded.Add(1, tags);
        else
            _modelInvocationsFailed.Add(1, tags);

        _modelLatencyMs.Record(latencyMs, tags);
        _modelInputTokens.Add(inputTokens, tags);
        _modelOutputTokens.Add(outputTokens, tags);
        _modelCostUsd.Record(costUsd, tags);
    }

    public void ToolPolicyDenied(string agentName, string policyTag, string kind)
    {
        _toolPolicyDenials.Add(1,
            new KeyValuePair<string, object?>("agent.name", agentName),
            new KeyValuePair<string, object?>("policy.tag", policyTag),
            new KeyValuePair<string, object?>("denial.kind", kind));
    }

    public void RecordWaitingExternalRuns(int total, int stale, double oldestAgeSeconds)
    {
        Volatile.Write(ref _waitingExternalRuns, total);
        Volatile.Write(ref _waitingExternalRunsStale, stale);
        Volatile.Write(ref _waitingExternalOldestAgeSeconds, oldestAgeSeconds);
    }

    public void Dispose() => _meter.Dispose();
}
