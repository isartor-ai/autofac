using Agentwerke.Application.Notifications;
using Agentwerke.Application.Observability;
using Agentwerke.Application.Workflows;
using Agentwerke.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace Agentwerke.Infrastructure.Workers;

public sealed class WorkflowRunExecutor : IWorkflowRunExecutor
{
    private readonly IWorkflowRunner _runner;
    private readonly IWorkflowDefinitionRepository _definitionRepository;
    private readonly IWorkflowRunRepository _runRepository;
    private readonly IApprovalRepository _approvalRepository;
    private readonly IWaitingExternalCorrelationRepository _waitingExternalCorrelationRepository;
    private readonly IRunOutbox _outbox;
    private readonly IWorkflowTracer _tracer;
    private readonly IApprovalNotifier _approvalNotifier;
    private readonly ILogger<WorkflowRunExecutor> _logger;

    public WorkflowRunExecutor(
        IWorkflowRunner runner,
        IWorkflowDefinitionRepository definitionRepository,
        IWorkflowRunRepository runRepository,
        IApprovalRepository approvalRepository,
        IWaitingExternalCorrelationRepository waitingExternalCorrelationRepository,
        IRunOutbox outbox,
        IWorkflowTracer tracer,
        IApprovalNotifier approvalNotifier,
        ILogger<WorkflowRunExecutor> logger)
    {
        _runner = runner;
        _definitionRepository = definitionRepository;
        _runRepository = runRepository;
        _approvalRepository = approvalRepository;
        _waitingExternalCorrelationRepository = waitingExternalCorrelationRepository;
        _outbox = outbox;
        _tracer = tracer;
        _approvalNotifier = approvalNotifier;
        _logger = logger;
    }

    public async Task ExecuteStartAsync(
        string runId,
        string workflowId,
        string? initiator,
        string? correlationId,
        CancellationToken ct)
    {
        var run = await _runRepository.GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run '{runId}' not found.");

        var bpmnXml = await GetBpmnXmlAsync(run.WorkflowId, ct);

        using var span = _tracer.StartSpan("workflow.run.start");
        span.SetTag("agentwerke.run_id", runId);
        span.SetTag("agentwerke.workflow_id", run.WorkflowId);
        if (correlationId is not null) span.SetTag("agentwerke.correlation_id", correlationId);

        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["RunId"] = runId,
            ["WorkflowId"] = run.WorkflowId,
            ["Operation"] = "start"
        });

        _logger.LogInformation("Executing start for run {RunId} workflow {WorkflowId}", runId, run.WorkflowId);

        try
        {
            var result = await _runner.StartAsync(
                run.WorkflowId, bpmnXml, run.RequestedBy, ct, correlationId, existingRunId: runId);

            span.SetTag("agentwerke.result_status", result.Status);
            await HandleResultAsync(runId, result, ct);
        }
        catch (Exception ex)
        {
            span.SetError(ex);
            throw;
        }
    }

    public async Task ExecuteResumeAsync(
        string runId,
        string? approvedBy,
        string? externalCorrelationKey,
        IReadOnlyDictionary<string, string>? externalPayload,
        string? resumedBy,
        CancellationToken ct)
    {
        var run = await _runRepository.GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run '{runId}' not found.");

        var bpmnXml = await GetBpmnXmlAsync(run.WorkflowId, ct);

        using var span = _tracer.StartSpan("workflow.run.resume");
        span.SetTag("agentwerke.run_id", runId);
        span.SetTag("agentwerke.workflow_id", run.WorkflowId);

        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["RunId"] = runId,
            ["WorkflowId"] = run.WorkflowId,
            ["Operation"] = "resume"
        });

        _logger.LogInformation("Executing resume for run {RunId}", runId);

        try
        {
            var result = await _runner.ResumeAsync(runId, bpmnXml, approvedBy, externalPayload, externalCorrelationKey, resumedBy, ct);
            span.SetTag("agentwerke.result_status", result.Status);
            await HandleResultAsync(runId, result, ct);
        }
        catch (Exception ex)
        {
            span.SetError(ex);
            throw;
        }
    }

    public async Task ExecuteRecoverAsync(string runId, CancellationToken ct)
    {
        var run = await _runRepository.GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Run '{runId}' not found.");

        var bpmnXml = await GetBpmnXmlAsync(run.WorkflowId, ct);

        using var span = _tracer.StartSpan("workflow.run.recover");
        span.SetTag("agentwerke.run_id", runId);
        span.SetTag("agentwerke.workflow_id", run.WorkflowId);

        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["RunId"] = runId,
            ["WorkflowId"] = run.WorkflowId,
            ["Operation"] = "recover"
        });

        _logger.LogInformation("Executing recovery for run {RunId}", runId);

        try
        {
            var result = await _runner.RecoverAsync(runId, bpmnXml, ct);
            span.SetTag("agentwerke.result_status", result.Status);
            await HandleResultAsync(runId, result, ct);
        }
        catch (Exception ex)
        {
            span.SetError(ex);
            throw;
        }
    }

    private async Task HandleResultAsync(string runId, WorkflowRunnerResult result, CancellationToken ct)
    {
        switch (result.Status)
        {
            case "waiting_user":
                if (result.WaitingApproval is not null)
                    await CreateApprovalRequestAsync(runId, result.WaitingApproval, ct);
                await _runRepository.UpdateRunStatusAsync(runId, "waiting_user", ct);
                break;

            case "waiting_timer":
                if (result.TimerDueAt.HasValue)
                    await _outbox.EnqueueAsync(OutboxOperations.Timer, runId, visibleAfter: result.TimerDueAt, ct: ct);
                await _runRepository.UpdateRunStatusAsync(runId, "waiting_timer", ct);
                break;

            case "waiting_external":
                // An external wait guarded by an interrupting boundary timer schedules the same
                // Timer outbox entry a timer event does; when it fires, recovery follows the
                // boundary flow instead of leaving the run parked forever (#208).
                if (result.TimerDueAt.HasValue)
                    await _outbox.EnqueueAsync(OutboxOperations.Timer, runId, visibleAfter: result.TimerDueAt, ct: ct);
                await _runRepository.UpdateRunStatusAsync(runId, "waiting_external", ct);
                break;

            case "completed":
                await _runRepository.UpdateRunStatusAsync(runId, "completed", ct);
                break;

            case "failed":
                await _runRepository.UpdateRunStatusAsync(runId, "failed", ct);
                break;

            default:
                _logger.LogWarning(
                    "Unexpected execution result status '{Status}' for run {RunId}", result.Status, runId);
                break;
        }

        await SyncWaitingExternalCorrelationAsync(runId, result, ct);
    }

    /// <summary>
    /// Keeps the waiting-external correlation store (#138) in sync with the run's status, so an
    /// inbound webhook can find which run to auto-resume. A run has at most one active wait, so
    /// any non-"waiting_external" result clears whatever was recorded for it.
    /// </summary>
    private async Task SyncWaitingExternalCorrelationAsync(string runId, WorkflowRunnerResult result, CancellationToken ct)
    {
        if (string.Equals(result.Status, "waiting_external", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(result.WaitingExternalCorrelationKey) &&
            !string.IsNullOrWhiteSpace(result.WaitingExternalMessageName))
        {
            await _waitingExternalCorrelationRepository.UpsertAsync(
                new WaitingExternalCorrelation
                {
                    RunId = runId,
                    CorrelationKey = result.WaitingExternalCorrelationKey,
                    MessageName = result.WaitingExternalMessageName,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                },
                ct);
        }
        else
        {
            await _waitingExternalCorrelationRepository.RemoveAsync(runId, ct);
        }
    }

    private async Task CreateApprovalRequestAsync(
        string runId,
        WaitingApprovalInfo info,
        CancellationToken ct)
    {
        var existing = await _approvalRepository.GetPendingApprovalForRunAsync(runId, ct);
        if (existing is not null)
            return;

        var run = await _runRepository.GetRunAsync(runId, ct);

        var approval = new ApprovalRequest
        {
            Id = $"apr_{Guid.NewGuid():N}",
            RunId = runId,
            WorkflowName = run?.WorkflowName ?? runId,
            ActionRequested = info.PurposeType,
            Requester = run?.RequestedBy ?? "system",
            // AgentName is NOT NULL in the schema; an approval gate with no resolvable
            // preceding agent (e.g. an approval-only workflow, or a workflow without
            // sequence flows) yields a null here, which crashed the insert and left the
            // run gated with no approval row (#163). Coalesce so the insert succeeds.
            AgentName = info.AgentName ?? string.Empty,
            PolicyRationale = info.PolicyTag,
            RiskLevel = info.RiskLevel,
            RiskScore = info.RiskScore,
            RiskFactors = info.RiskFactors?.ToList() ?? [],
            AffectedSystems = info.AffectedSystems?.ToList() ?? [],
            ArtifactName = info.ArtifactName,
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };

        await _approvalRepository.AddApprovalAsync(approval, ct);
        await _approvalRepository.SaveChangesAsync(ct);
        await _runRepository.IncrementPendingApprovalsAsync(runId, ct);

        // Notify configured chat channels that a human decision is needed (#31).
        // Best-effort inside the notifier — delivery failures never fail the run.
        await _approvalNotifier.NotifyApprovalRequestedAsync(
            new ApprovalNotification(
                RunId: runId,
                ApprovalId: approval.Id,
                WorkflowName: approval.WorkflowName,
                ActionRequested: approval.ActionRequested,
                RiskLevel: approval.RiskLevel,
                ArtifactName: approval.ArtifactName),
            ct);
    }

    private async Task<string> GetBpmnXmlAsync(string workflowId, CancellationToken ct)
    {
        var definition = await _definitionRepository.GetAsync(workflowId, ct)
            ?? throw new InvalidOperationException($"Workflow definition '{workflowId}' not found.");

        return definition.BpmnXml
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' has no BPMN XML.");
    }
}
