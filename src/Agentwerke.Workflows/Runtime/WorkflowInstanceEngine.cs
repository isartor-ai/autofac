using System.Text.Json;
using System.Text.RegularExpressions;
using Agentwerke.Application.Workflows;
using Agentwerke.Domain.Persistence;
using Agentwerke.Workflows.Bpmn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentwerke.Workflows.Runtime;

public sealed class WorkflowInstanceEngine : IWorkflowEngineAdapter
{
    private const string RunningStatus = "running";
    private const string WaitingUserStatus = "waiting_user";
    private const string WaitingTimerStatus = "waiting_timer";
    private const string WaitingExternalStatus = "waiting_external";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    /// <summary>Max executions of one node within a single advance before the run fails (runaway loop protection).</summary>
    private const int MaxNodeVisitsPerAdvance = 25;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex TemplateVariablePattern = new("{{\\s*([a-zA-Z0-9_.-]+)\\s*}}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IWorkflowRuntimeStore _store;
    private readonly IServiceTaskExecutor _serviceTaskExecutor;
    private readonly IRunContextRepository _runContext;
    private readonly ILogger<WorkflowInstanceEngine> _logger;
    private readonly IServiceScopeFactory? _serviceScopeFactory;

    public WorkflowInstanceEngine(
        IWorkflowRuntimeStore store,
        IServiceTaskExecutor serviceTaskExecutor,
        IRunContextRepository runContext,
        ILogger<WorkflowInstanceEngine> logger,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        _store = store;
        _serviceTaskExecutor = serviceTaskExecutor;
        _runContext = runContext;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public string EngineId => "in-process";

    public Task<WorkflowExecutionState> StartAsync(
        string workflowDefinitionId,
        BpmnWorkflowDefinition definition,
        string? initiator,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        return StartAsync(
            new WorkflowEngineStartRequest(workflowDefinitionId, definition, initiator, correlationId),
            cancellationToken);
    }

    public Task<WorkflowExecutionState> ResumeAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        string? approvedBy,
        CancellationToken cancellationToken)
    {
        return ResumeAsync(
            new WorkflowEngineResumeRequest(runId, definition, approvedBy),
            cancellationToken);
    }

    public Task<WorkflowExecutionState> RecoverAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        CancellationToken cancellationToken)
    {
        return RecoverAsync(
            new WorkflowEngineRecoverRequest(runId, definition),
            cancellationToken);
    }

    public async Task<WorkflowExecutionState> StartAsync(
        WorkflowEngineStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflowDefinitionId = request.WorkflowDefinitionId;
        var definition = request.Definition;
        var initiator = request.Initiator;
        var correlationId = request.CorrelationId;
        ValidateDefinition(definition);

        WorkflowRun run;
        if (request.ExistingRunId is not null)
        {
            run = await _store.GetRunAsync(request.ExistingRunId, cancellationToken)
                ?? throw new InvalidOperationException($"Pre-created run '{request.ExistingRunId}' was not found.");
        }
        else
        {
            run = await _store.CreateRunAsync(
                workflowDefinitionId,
                initiator,
                cancellationToken,
                correlationId);
        }

        _logger.LogInformation(
            "Workflow run started. RunId={RunId} WorkflowId={WorkflowId} Initiator={Initiator} CorrelationId={CorrelationId}",
            run.Id, workflowDefinitionId, initiator, correlationId);

        await _store.AppendEventAsync(run.Id, "run_started",
            Serialize(new { runId = run.Id, workflowDefinitionId, initiator, correlationId }),
            cancellationToken);

        return await AdvanceAsync(run.Id, definition, startNodeId: null, cancellationToken);
    }

    public async Task<WorkflowExecutionState> ResumeAsync(
        WorkflowEngineResumeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateDefinition(request.Definition);

        var checkpoint = await GetCheckpointAsync(request.RunId, cancellationToken)
            ?? throw new InvalidOperationException($"No persisted checkpoint exists for run '{request.RunId}'.");

        if (string.Equals(checkpoint.Status, WaitingUserStatus, StringComparison.Ordinal))
        {
            await _store.AppendEventAsync(request.RunId, "user_task_completed",
                Serialize(new
                {
                    runId = request.RunId,
                    nodeId = checkpoint.WaitingOnNodeId,
                    approvedBy = request.ApprovedBy,
                    timestampUtc = DateTime.UtcNow.ToString("o")
                }),
                cancellationToken);
        }
        else if (string.Equals(checkpoint.Status, WaitingExternalStatus, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(request.ExternalCorrelationKey))
            {
                throw new InvalidOperationException($"Run '{request.RunId}' requires an external correlation key to resume.");
            }

            if (!string.Equals(checkpoint.ExternalCorrelationKey, request.ExternalCorrelationKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Run '{request.RunId}' correlation key '{request.ExternalCorrelationKey}' does not match the waiting checkpoint.");
            }

            await MergeExternalPayloadAsync(request.RunId, request.ExternalPayload ?? new Dictionary<string, string>(), cancellationToken);

            await _store.AppendEventAsync(request.RunId, "external_event_received",
                Serialize(new
                {
                    runId = request.RunId,
                    nodeId = checkpoint.WaitingOnNodeId,
                    messageName = checkpoint.ExternalMessageName,
                    correlationKey = request.ExternalCorrelationKey,
                    payload = request.ExternalPayload ?? new Dictionary<string, string>(),
                    resumedBy = request.ResumedBy,
                    timestampUtc = DateTime.UtcNow.ToString("o")
                }),
                cancellationToken);
        }
        else
        {
            throw new InvalidOperationException($"Run '{request.RunId}' is not waiting for resumable input.");
        }

        return await AdvanceAsync(request.RunId, request.Definition, checkpoint.NextNodeId, cancellationToken);
    }

    public async Task<WorkflowExecutionState> RecoverAsync(
        WorkflowEngineRecoverRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateDefinition(request.Definition);

        _ = await _store.GetRunAsync(request.RunId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow run '{request.RunId}' was not found.");

        var checkpoint = await GetCheckpointAsync(request.RunId, cancellationToken)
            ?? throw new InvalidOperationException($"No persisted checkpoint exists for run '{request.RunId}'.");

        if (string.Equals(checkpoint.Status, CompletedStatus, StringComparison.Ordinal))
        {
            return new WorkflowExecutionState(
                RunId: request.RunId, Status: CompletedStatus,
                NextNodeId: null, WaitingOnNodeId: null, CompletedAt: checkpoint.CompletedAt);
        }

        if (string.Equals(checkpoint.Status, WaitingUserStatus, StringComparison.Ordinal))
        {
            var artifactName = checkpoint.WaitingOnNodeId is null
                ? null
                : await FindWaitingApprovalArtifactNameAsync(request.RunId, request.Definition, checkpoint.WaitingOnNodeId, cancellationToken);
            return new WorkflowExecutionState(
                RunId: request.RunId, Status: WaitingUserStatus,
                NextNodeId: checkpoint.NextNodeId, WaitingOnNodeId: checkpoint.WaitingOnNodeId,
                CompletedAt: null, WaitingApprovalArtifactName: artifactName);
        }

        if (string.Equals(checkpoint.Status, WaitingTimerStatus, StringComparison.Ordinal))
        {
            await _store.AppendEventAsync(
                request.RunId,
                "timer_fired",
                Serialize(new { runId = request.RunId, nodeId = checkpoint.WaitingOnNodeId, firedAt = DateTime.UtcNow.ToString("o") }),
            cancellationToken);
        }

        if (string.Equals(checkpoint.Status, WaitingExternalStatus, StringComparison.Ordinal))
        {
            var deadline = ParseTimestamp(checkpoint.ExternalTimeoutAt);

            // The wait's boundary timer has expired: the external system never delivered
            // (CI never ran, webhook misconfigured, build cancelled). Follow the boundary
            // flow instead of parking forever (#208).
            if (deadline is not null && checkpoint.BoundaryNodeId is not null && deadline <= DateTimeOffset.UtcNow)
            {
                return await TriggerExternalWaitTimeoutAsync(request.RunId, request.Definition, checkpoint, deadline.Value, cancellationToken);
            }

            return new WorkflowExecutionState(
                RunId: request.RunId,
                Status: WaitingExternalStatus,
                NextNodeId: checkpoint.NextNodeId,
                WaitingOnNodeId: checkpoint.WaitingOnNodeId,
                CompletedAt: null,
                // Re-arm the boundary timer so a recovery pass before the deadline does not
                // drop it. Always in the future here, so this cannot spin.
                TimerDueAt: deadline,
                WaitingExternalCorrelationKey: checkpoint.ExternalCorrelationKey,
                WaitingExternalMessageName: checkpoint.ExternalMessageName);
        }

        return await AdvanceAsync(request.RunId, request.Definition, checkpoint.NextNodeId, cancellationToken);
    }

    // ── Graph execution ───────────────────────────────────────────────────────

    private async Task<WorkflowExecutionState> AdvanceAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        string? startNodeId,
        CancellationToken cancellationToken)
    {
        var graph = FlowGraph.Build(definition);
        var currentNodeId = startNodeId ?? definition.Nodes[0].Id;
        // Bounds conditional-gateway loop-backs within one advance; waits (user/timer/
        // external) return from this method, so each resume gets a fresh budget.
        var nodeVisits = new Dictionary<string, int>(StringComparer.Ordinal);

        await _store.UpdateRunStatusAsync(runId, RunningStatus, completedAt: null, cancellationToken);

        while (true)
        {
            if (!graph.NodeById.TryGetValue(currentNodeId, out var node))
                throw new InvalidOperationException($"Node '{currentNodeId}' not found in workflow definition.");

            if (!IsSupportedRuntimeNode(node.ElementName))
                throw new InvalidOperationException($"Node '{node.Id}' type '{node.ElementName}' is not supported by the in-process engine.");

            var visits = nodeVisits.GetValueOrDefault(currentNodeId) + 1;
            nodeVisits[currentNodeId] = visits;
            if (visits > MaxNodeVisitsPerAdvance)
            {
                await _store.AppendEventAsync(runId, "loop_guard_triggered",
                    Serialize(new { runId, nodeId = node.Id, visits, maxNodeVisits = MaxNodeVisitsPerAdvance }),
                    cancellationToken);
                await _store.UpdateRunStatusAsync(runId, FailedStatus, completedAt: null, cancellationToken);
                await SaveCheckpointAsync(runId, FailedStatus, null, null, null, cancellationToken);
                return new WorkflowExecutionState(runId, FailedStatus, null, null, null);
            }

            await _store.AppendEventAsync(runId, "node_entered",
                Serialize(new { runId, nodeId = node.Id, nodeType = node.ElementName }),
                cancellationToken);

            switch (node.ElementName)
            {
                case "startEvent":
                    await CompleteNodeAsync(runId, node, cancellationToken);
                    await SaveCheckpointAsync(runId, RunningStatus, graph.GetSingleSuccessor(node.Id), null, null, cancellationToken);
                    currentNodeId = graph.GetSingleSuccessor(node.Id);
                    break;

                case "serviceTask":
                    var result = await ExecuteServiceTaskAsync(runId, definition, graph, node, cancellationToken);
                    if (result == ServiceExecutionResult.Failed)
                    {
                        await _store.UpdateRunStatusAsync(runId, FailedStatus, completedAt: null, cancellationToken);
                        await SaveCheckpointAsync(runId, FailedStatus, null, null, null, cancellationToken);
                        return new WorkflowExecutionState(runId, FailedStatus, null, null, null);
                    }
                    if (result == ServiceExecutionResult.WaitingUser)
                    {
                        // The agent asked a human and is waiting (#192). Suspend with the checkpoint
                        // pointing back at THIS node so resume re-runs the step with the answer
                        // available. WaitingOnNodeId is null in the returned state so the executor
                        // does not create an approval row — the pending AgentInteraction is the record.
                        await SaveCheckpointAsync(runId, WaitingUserStatus, node.Id, node.Id, null, cancellationToken);
                        return new WorkflowExecutionState(runId, WaitingUserStatus, node.Id, null, null);
                    }
                    var afterService = graph.GetSingleSuccessor(node.Id);
                    // Skip the boundary event node when present — it was already handled inline
                    if (graph.NodeById.TryGetValue(afterService, out var afterNode) && afterNode.ElementName == "boundaryEvent")
                        afterService = graph.GetSingleSuccessor(afterService);
                    await SaveCheckpointAsync(runId, RunningStatus, afterService, null, null, cancellationToken);
                    currentNodeId = afterService;
                    break;

                case "userTask":
                    var nextAfterUser = graph.GetSingleSuccessor(node.Id);
                    await _store.AppendEventAsync(runId, "user_task_waiting",
                        Serialize(new
                        {
                            runId, nodeId = node.Id,
                            purposeType = node.ApprovalMetadata?.PurposeType,
                            policyTag = node.ApprovalMetadata?.PolicyTag
                        }), cancellationToken);
                    // Intentionally do NOT flip the run's Status to waiting_user here. The
                    // executor (WorkflowRunExecutor.HandleResultAsync) creates the approval
                    // row and THEN sets the status, so the run never appears as
                    // awaiting_approval before its approval is queryable (#163). The
                    // checkpoint still records waiting_user for crash recovery.
                    await SaveCheckpointAsync(runId, WaitingUserStatus, nextAfterUser, node.Id, null, cancellationToken);
                    var artifactName = await FindWaitingApprovalArtifactNameAsync(runId, definition, node.Id, cancellationToken);
                    return new WorkflowExecutionState(runId, WaitingUserStatus, nextAfterUser, node.Id, null, WaitingApprovalArtifactName: artifactName);

                case "exclusiveGateway":
                    var gatewayVariables = await BuildRunVariableMapAsync(runId, cancellationToken);
                    var decision = graph.GetConditionalSuccessor(
                        node.Id,
                        key => gatewayVariables.TryGetValue(key, out var value) ? value : null);
                    await _store.AppendEventAsync(runId, "gateway_evaluated",
                        Serialize(new
                        {
                            runId,
                            gatewayId = node.Id,
                            gatewayType = "exclusive",
                            chosenFlowId = decision.FlowId,
                            chosenTargetId = decision.TargetRef,
                            usedDefaultFlow = decision.UsedDefaultFlow,
                            conditions = decision.EvaluatedConditions
                        }),
                        cancellationToken);
                    await CompleteNodeAsync(runId, node, cancellationToken);
                    await SaveCheckpointAsync(runId, RunningStatus, decision.TargetRef, null, null, cancellationToken);
                    currentNodeId = decision.TargetRef;
                    break;

                case "parallelGateway":
                    var outgoing = graph.GetOutgoing(node.Id);
                    if (outgoing.Count > 1)
                    {
                        // FORK
                        var branchNodeIds = outgoing.Select(static f => f.TargetRef).ToList();
                        var joinNodeId = graph.FindJoin(branchNodeIds);

                        await _store.AppendEventAsync(runId, "parallel_forked",
                            Serialize(new { runId, gatewayId = node.Id, branchNodeIds }),
                            cancellationToken);

                        ServiceExecutionResult[] branchResults;

                        if (_serviceScopeFactory is not null)
                        {
                            branchResults = await Task.WhenAll(
                                branchNodeIds.Select(branchId =>
                                    ExecuteBranchInScopeAsync(runId, definition, graph, branchId, joinNodeId, cancellationToken)));
                        }
                        else
                        {
                            var results = new List<ServiceExecutionResult>(branchNodeIds.Count);
                            foreach (var branchId in branchNodeIds)
                            {
                                results.Add(await ExecuteBranchAsync(runId, definition, graph, branchId, joinNodeId, cancellationToken));
                            }

                            branchResults = results.ToArray();
                        }

                        if (branchResults.Any(static result => result == ServiceExecutionResult.Failed))
                        {
                            await _store.UpdateRunStatusAsync(runId, FailedStatus, completedAt: null, cancellationToken);
                            await SaveCheckpointAsync(runId, FailedStatus, null, null, null, cancellationToken);
                            return new WorkflowExecutionState(runId, FailedStatus, null, null, null);
                        }

                        var afterJoin = graph.GetSingleSuccessor(joinNodeId);
                        await _store.AppendEventAsync(runId, "parallel_joined",
                            Serialize(new { runId, gatewayId = joinNodeId }),
                            cancellationToken);
                        await CompleteNodeAsync(runId, node, cancellationToken);
                        await SaveCheckpointAsync(runId, RunningStatus, afterJoin, null, null, cancellationToken);
                        currentNodeId = afterJoin;
                    }
                    else
                    {
                        // JOIN (reached after all branches complete) — should not be hit in normal flow
                        await CompleteNodeAsync(runId, node, cancellationToken);
                        currentNodeId = graph.GetSingleSuccessor(node.Id);
                    }
                    break;

                case "intermediateCatchEvent":
                    if (node.ExternalEventMetadata is not null)
                    {
                        return await WaitForExternalEventAsync(
                            runId,
                            graph,
                            node,
                            graph.GetSingleSuccessor(node.Id),
                            cancellationToken);
                    }

                    return await ScheduleTimerAndPauseAsync(
                        runId,
                        node,
                        graph.GetSingleSuccessor(node.Id),
                        cancellationToken);

                case "receiveTask":
                    return await WaitForExternalEventAsync(
                        runId,
                        graph,
                        node,
                        graph.GetSingleSuccessor(node.Id),
                        cancellationToken);

                case "boundaryEvent":
                    // Handled inline by service task execution; if reached directly, just pass through
                    await _store.AppendEventAsync(runId, "boundary_event_registered",
                        Serialize(new { runId, boundaryNodeId = node.Id }),
                        cancellationToken);
                    await CompleteNodeAsync(runId, node, cancellationToken);
                    currentNodeId = graph.GetSingleSuccessor(node.Id);
                    break;

                case "endEvent":
                    var completedAt = DateTime.UtcNow.ToString("o");
                    await CompleteNodeAsync(runId, node, cancellationToken);
                    await _store.AppendEventAsync(runId, "run_completed",
                        Serialize(new { runId, completedAt }), cancellationToken);
                    _logger.LogInformation("Workflow run completed. RunId={RunId}", runId);
                    await _store.UpdateRunStatusAsync(runId, CompletedStatus, completedAt, cancellationToken);
                    await SaveCheckpointAsync(runId, CompletedStatus, null, null, completedAt, cancellationToken);
                    return new WorkflowExecutionState(runId, CompletedStatus, null, null, completedAt);
            }
        }
    }

    private async Task<ServiceExecutionResult> ExecuteBranchAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        FlowGraph graph,
        string branchStartId,
        string joinNodeId,
        CancellationToken cancellationToken)
    {
        var nodeId = branchStartId;
        while (!string.Equals(nodeId, joinNodeId, StringComparison.Ordinal))
        {
            if (!graph.NodeById.TryGetValue(nodeId, out var branchNode))
                throw new InvalidOperationException($"Branch node '{nodeId}' not found.");

            await _store.AppendEventAsync(runId, "parallel_branch_entered",
                Serialize(new { runId, branchNodeId = branchNode.Id, branchNodeType = branchNode.ElementName }),
                cancellationToken);

            if (branchNode.ElementName == "serviceTask")
            {
                var result = await ExecuteServiceTaskAsync(runId, definition, graph, branchNode, cancellationToken);
                if (result == ServiceExecutionResult.Failed)
                    return ServiceExecutionResult.Failed;
            }
            else if (branchNode.ElementName == "intermediateCatchEvent")
            {
                await ExecuteParallelTimerAsync(runId, branchNode, cancellationToken);
            }
            else
            {
                await _store.AppendEventAsync(runId, "node_completed",
                    Serialize(new { runId, nodeId = branchNode.Id, nodeType = branchNode.ElementName }),
                    cancellationToken);
            }

            nodeId = graph.GetSingleSuccessor(branchNode.Id);
        }
        return ServiceExecutionResult.Completed;
    }

    private async Task<ServiceExecutionResult> ExecuteBranchInScopeAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        FlowGraph graph,
        string branchStartId,
        string joinNodeId,
        CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory!.CreateAsyncScope();
        var scopedStore = scope.ServiceProvider.GetRequiredService<IWorkflowRuntimeStore>();
        var scopedExecutor = scope.ServiceProvider.GetRequiredService<IServiceTaskExecutor>();
        var scopedRunContext = scope.ServiceProvider.GetRequiredService<IRunContextRepository>();

        var nodeId = branchStartId;
        while (!string.Equals(nodeId, joinNodeId, StringComparison.Ordinal))
        {
            if (!graph.NodeById.TryGetValue(nodeId, out var branchNode))
                throw new InvalidOperationException($"Branch node '{nodeId}' not found.");

            await scopedStore.AppendEventAsync(runId, "parallel_branch_entered",
                Serialize(new { runId, branchNodeId = branchNode.Id, branchNodeType = branchNode.ElementName }),
                cancellationToken);

            if (branchNode.ElementName == "serviceTask")
            {
                var result = await ExecuteServiceTaskAsync(
                    runId,
                    definition,
                    graph,
                    branchNode,
                    cancellationToken,
                    scopedStore,
                    scopedExecutor,
                    scopedRunContext);

                if (result == ServiceExecutionResult.Failed)
                    return ServiceExecutionResult.Failed;
            }
            else if (branchNode.ElementName == "intermediateCatchEvent")
            {
                await ExecuteParallelTimerAsync(runId, branchNode, cancellationToken, scopedStore);
            }
            else
            {
                await scopedStore.AppendEventAsync(runId, "node_completed",
                    Serialize(new { runId, nodeId = branchNode.Id, nodeType = branchNode.ElementName }),
                    cancellationToken);
            }

            nodeId = graph.GetSingleSuccessor(branchNode.Id);
        }

        return ServiceExecutionResult.Completed;
    }

    private async Task<ServiceExecutionResult> ExecuteServiceTaskAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        FlowGraph graph,
        BpmnNodeDefinition node,
        CancellationToken cancellationToken,
        IWorkflowRuntimeStore? storeOverride = null,
        IServiceTaskExecutor? executorOverride = null,
        IRunContextRepository? runContextOverride = null)
    {
        var store = storeOverride ?? _store;
        var executor = executorOverride ?? _serviceTaskExecutor;
        var runContext = runContextOverride ?? _runContext;
        var metadata = node.Metadata;
        var maxRetries = metadata?.MaxRetries ?? 0;
        var retryBackoffSeconds = metadata?.RetryBackoffSeconds ?? 0;
        var simulateTimeout = metadata?.SimulateTimeout ?? false;
        var timeoutSeconds = metadata?.TimeoutSeconds;

        // Find boundary event: either the adjacent node in flow that is a boundaryEvent
        // (legacy convention) or one attached via BPMN attachedToRef.
        var successorId = graph.GetSingleSuccessorOrNull(node.Id);
        BpmnNodeDefinition? boundaryNode = null;
        if (successorId is not null
            && graph.NodeById.TryGetValue(successorId, out var candidate)
            && candidate.ElementName == "boundaryEvent")
        {
            boundaryNode = candidate;
        }
        boundaryNode ??= graph.GetAttachedTimerBoundary(node.Id);

        var step = await store.CreateStepAsync(
            runId, node.Id, node.Name, node.ElementName, metadata?.Agent, cancellationToken);

        if (simulateTimeout && boundaryNode is not null)
        {
            await store.AppendEventAsync(runId, "timer_scheduled",
                Serialize(new { runId, nodeId = node.Id, timeoutSeconds = timeoutSeconds ?? 0 }),
                cancellationToken);
            await store.AppendEventAsync(runId, "timeout_triggered",
                Serialize(new { runId, nodeId = node.Id, boundaryNodeId = boundaryNode.Id }),
                cancellationToken);
            await store.AppendEventAsync(runId, "boundary_event_triggered",
                Serialize(new { runId, boundaryNodeId = boundaryNode.Id, sourceNodeId = node.Id }),
                cancellationToken);
            await store.AppendEventAsync(runId, "node_completed",
                Serialize(new { runId, nodeId = node.Id, nodeType = node.ElementName, reason = "timeout_boundary" }),
                cancellationToken);
            await store.UpdateStepStatusAsync(step.Id, "timed_out", null, null, DateTime.UtcNow.ToString("o"), null, null, cancellationToken);
            return ServiceExecutionResult.Completed;
        }

        var attempt = 1;
        while (true)
        {
            await store.AppendEventAsync(runId, "service_task_attempted",
                Serialize(new
                {
                    runId, nodeId = node.Id, stepId = step.Id, attempt, maxRetries,
                    agent = metadata?.Agent, action = metadata?.Action,
                    environment = metadata?.Environment,
                    purposeType = metadata?.PurposeType, policyTag = metadata?.PolicyTag,
                    requiresEvidence = metadata?.RequiresEvidence
                }), cancellationToken);
            await store.AppendEventAsync(runId, "agent_reasoning_started",
                Serialize(new
                {
                    runId,
                    nodeId = node.Id,
                    stepId = step.Id,
                    agent = metadata?.Agent,
                    summary = BuildAgentReasoningStartSummary(metadata, attempt)
                }), cancellationToken);

            var seenReasoningUpdates = new HashSet<string>(StringComparer.Ordinal);
            Task ReportAgentProgressAsync(AgentExecutionProgressUpdate update, CancellationToken ct)
            {
                var eventType = MapAgentProgressEventType(update.Kind);
                var summary = update.Summary?.Trim();
                if (eventType is null || string.IsNullOrWhiteSpace(summary))
                {
                    return Task.CompletedTask;
                }

                if (string.Equals(update.Kind, AgentExecutionProgressKinds.Reasoning, StringComparison.Ordinal))
                {
                    seenReasoningUpdates.Add(summary);
                }

                return store.AppendEventAsync(
                    runId,
                    eventType,
                    Serialize(new
                    {
                        runId,
                        nodeId = node.Id,
                        stepId = step.Id,
                        agent = metadata?.Agent,
                        summary,
                        toolName = update.ToolName,
                        toolCallId = update.ToolCallId,
                        status = update.Status,
                        detail = update.Detail
                    }),
                    ct);
            }

            var outcome = await executor.ExecuteAsync(runId, step.Id, node, attempt, cancellationToken, ReportAgentProgressAsync);
            var visibleReasoning = FindVisibleReasoningSummary(outcome);
            if (!string.IsNullOrWhiteSpace(visibleReasoning)
                && !seenReasoningUpdates.Contains(visibleReasoning.Trim()))
            {
                await store.AppendEventAsync(runId, "agent_reasoning_recorded",
                    Serialize(new
                    {
                        runId,
                        nodeId = node.Id,
                        stepId = step.Id,
                        agent = metadata?.Agent,
                        summary = visibleReasoning
                    }), cancellationToken);
            }

            if (outcome.PolicyDecision is not null)
            {
                await store.AppendEventAsync(runId, "policy_decision_recorded",
                    Serialize(new
                    {
                        runId, nodeId = node.Id, stepId = step.Id,
                        kind = outcome.PolicyDecision.Kind,
                        policyId = outcome.PolicyDecision.PolicyId,
                        policyName = outcome.PolicyDecision.PolicyName,
                        rationale = outcome.PolicyDecision.Rationale,
                        riskScore = outcome.PolicyDecision.RiskScore,
                        riskLevel = outcome.PolicyDecision.RiskLevel,
                        constraints = outcome.PolicyDecision.Constraints
                    }), cancellationToken);
            }

            foreach (var action in outcome.ExternalActions ?? [])
            {
                await store.AppendEventAsync(runId, "external_action_recorded",
                    Serialize(new
                    {
                        runId, nodeId = node.Id, stepId = step.Id,
                        provider = action.Provider, action = action.Action,
                        status = action.Status, resourceId = action.ResourceId,
                        resourceUrl = action.ResourceUrl, summary = action.Summary,
                        correlationKey = action.CorrelationKey, attempt
                    }), cancellationToken);
            }

            if (string.Equals(outcome.StepStatus, WaitingUserStatus, StringComparison.Ordinal))
            {
                // The agent paused mid-step to ask a human (#192). Record the wait and hand back
                // to the caller, which suspends the run and re-runs this node on resume.
                await store.AppendEventAsync(runId, "service_task_waiting_user",
                    Serialize(new { runId, nodeId = node.Id, stepId = step.Id, attempt, reason = outcome.FailureReason ?? "awaiting_human_input" }),
                    cancellationToken);
                await store.UpdateStepStatusAsync(step.Id, WaitingUserStatus, null, outcome.FailureReason, DateTime.UtcNow.ToString("o"), outcome.PolicyDecision, outcome.RuntimeSnapshot, cancellationToken);
                return ServiceExecutionResult.WaitingUser;
            }

            if (!outcome.Succeeded)
            {
                if (IsNonFatalServiceTaskStatus(outcome.StepStatus))
                {
                    await store.AppendEventAsync(runId, "service_task_needs_config",
                        Serialize(new { runId, nodeId = node.Id, stepId = step.Id, attempt, reason = outcome.FailureReason ?? "model_configuration_missing" }),
                        cancellationToken);
                    await store.AppendEventAsync(runId, "node_completed",
                        Serialize(new { runId, nodeId = node.Id, nodeType = node.ElementName, reason = outcome.StepStatus }),
                        cancellationToken);
                    await store.UpdateStepStatusAsync(step.Id, outcome.StepStatus!, null, outcome.FailureReason, DateTime.UtcNow.ToString("o"), outcome.PolicyDecision, outcome.RuntimeSnapshot, cancellationToken);
                    return ServiceExecutionResult.Completed;
                }

                await store.AppendEventAsync(runId, "service_task_failed",
                    Serialize(new { runId, nodeId = node.Id, stepId = step.Id, attempt, reason = outcome.FailureReason ?? "execution_error" }),
                    cancellationToken);

                if (attempt <= maxRetries)
                {
                    await store.AppendEventAsync(runId, "retry_scheduled",
                        Serialize(new { runId, nodeId = node.Id, nextAttempt = attempt + 1, retryBackoffSeconds }),
                        cancellationToken);
                    if (retryBackoffSeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(retryBackoffSeconds), cancellationToken);
                    attempt++;
                    continue;
                }

                if (boundaryNode is not null)
                {
                    await store.AppendEventAsync(runId, "boundary_event_triggered",
                        Serialize(new { runId, boundaryNodeId = boundaryNode.Id, sourceNodeId = node.Id }),
                        cancellationToken);
                    await store.AppendEventAsync(runId, "node_completed",
                        Serialize(new { runId, nodeId = node.Id, nodeType = node.ElementName, reason = "retry_exhausted_boundary" }),
                        cancellationToken);
                    await store.UpdateStepStatusAsync(step.Id, FailedStatus, null, outcome.FailureReason, DateTime.UtcNow.ToString("o"), outcome.PolicyDecision, outcome.RuntimeSnapshot, cancellationToken);
                    return ServiceExecutionResult.Completed;
                }

                await store.AppendEventAsync(runId, "service_task_retry_exhausted",
                    Serialize(new { runId, nodeId = node.Id, stepId = step.Id, attempts = attempt }),
                    cancellationToken);
                await store.UpdateStepStatusAsync(step.Id, FailedStatus, null, outcome.FailureReason, DateTime.UtcNow.ToString("o"), outcome.PolicyDecision, outcome.RuntimeSnapshot, cancellationToken);
                return ServiceExecutionResult.Failed;
            }

            await store.AppendEventAsync(runId, "agent_output_recorded",
                Serialize(new { runId, nodeId = node.Id, stepId = step.Id, agent = metadata?.Agent, outputLength = outcome.Output?.Length ?? 0 }),
                cancellationToken);
            await store.AppendEventAsync(runId, "node_completed",
                Serialize(new { runId, nodeId = node.Id, nodeType = node.ElementName }),
                cancellationToken);
            await store.UpdateStepStatusAsync(step.Id, CompletedStatus, outcome.Output, null, DateTime.UtcNow.ToString("o"), outcome.PolicyDecision, outcome.RuntimeSnapshot, cancellationToken);

            // Persist the step's primary output to run context so later tasks can read it.
            if (!string.IsNullOrEmpty(outcome.Output))
            {
                await runContext.SetAsync(runId, $"output.{node.Id}", outcome.Output, RunContextKinds.Output, cancellationToken);
            }

            return ServiceExecutionResult.Completed;
        }
    }

    private static bool IsNonFatalServiceTaskStatus(string? status) =>
        string.Equals(status, AgentTaskOutcomeStatuses.NeedsConfig, StringComparison.Ordinal);

    private static string BuildAgentReasoningStartSummary(
        AgentwerkeTaskMetadata? metadata,
        int attempt)
    {
        var action = string.IsNullOrWhiteSpace(metadata?.Action)
            ? "this step"
            : $"'{metadata.Action}'";
        return $"Starting {action}: assembling context, checking runtime constraints, and preparing the model/tool loop (attempt {attempt}).";
    }

    private static string? FindVisibleReasoningSummary(AgentTaskOutcome outcome) =>
        outcome.RuntimeSnapshot?.ModelTraces
            .LastOrDefault(static trace => !string.IsNullOrWhiteSpace(trace.ReasoningSummary))
            ?.ReasoningSummary;

    private static string? MapAgentProgressEventType(string? kind) =>
        kind switch
        {
            AgentExecutionProgressKinds.Reasoning => "agent_reasoning_delta",
            AgentExecutionProgressKinds.ToolStarted => "agent_tool_call_started",
            AgentExecutionProgressKinds.ToolFinished => "agent_tool_call_finished",
            AgentExecutionProgressKinds.SandboxLog => "agent_sandbox_log",
            _ => null
        };

    private async Task<WorkflowExecutionState> ScheduleTimerAndPauseAsync(
        string runId,
        BpmnNodeDefinition node,
        string? nextNodeId,
        CancellationToken cancellationToken)
    {
        var duration = ParseDuration(node.TimerDuration);
        var dueAt = DateTimeOffset.UtcNow.Add(duration);

        await _store.AppendEventAsync(runId, "timer_scheduled",
            Serialize(new { runId, nodeId = node.Id, dueAt = dueAt.ToString("o"), duration = node.TimerDuration ?? "PT0S" }),
            cancellationToken);

        await _store.UpdateRunStatusAsync(runId, WaitingTimerStatus, completedAt: null, cancellationToken);
        await SaveCheckpointAsync(runId, WaitingTimerStatus, nextNodeId, node.Id, completedAt: null, cancellationToken);

        return new WorkflowExecutionState(
            RunId: runId,
            Status: WaitingTimerStatus,
            NextNodeId: nextNodeId,
            WaitingOnNodeId: node.Id,
            CompletedAt: null,
            TimerDueAt: dueAt);
    }

    private async Task<WorkflowExecutionState> WaitForExternalEventAsync(
        string runId,
        FlowGraph graph,
        BpmnNodeDefinition node,
        string? nextNodeId,
        CancellationToken cancellationToken)
    {
        var externalEvent = node.ExternalEventMetadata
            ?? throw new InvalidOperationException($"Node '{node.Id}' is missing external event metadata.");

        var correlationKey = await RenderCorrelationKeyAsync(runId, externalEvent.CorrelationKeyTemplate, cancellationToken);

        // An interrupting boundary timer on the wait bounds how long the run may park here.
        // Without one the run waits indefinitely, which is the pre-#208 behaviour.
        var boundaryNode = graph.GetAttachedTimerBoundary(node.Id);
        DateTimeOffset? timeoutAt = boundaryNode is null
            ? null
            : DateTimeOffset.UtcNow.Add(ParseDuration(boundaryNode.TimerDuration));

        await _store.AppendEventAsync(runId, "external_event_waiting",
            Serialize(new
            {
                runId,
                nodeId = node.Id,
                messageName = externalEvent.MessageName,
                correlationKey,
                timeoutAt = timeoutAt?.ToString("o"),
                boundaryNodeId = boundaryNode?.Id
            }),
            cancellationToken);

        if (boundaryNode is not null)
        {
            await _store.AppendEventAsync(runId, "timer_scheduled",
                Serialize(new
                {
                    runId,
                    nodeId = boundaryNode.Id,
                    attachedToNodeId = node.Id,
                    dueAt = timeoutAt!.Value.ToString("o"),
                    duration = boundaryNode.TimerDuration
                }),
                cancellationToken);
        }

        await _store.UpdateRunStatusAsync(runId, WaitingExternalStatus, completedAt: null, cancellationToken);
        await SaveCheckpointAsync(
            runId,
            WaitingExternalStatus,
            nextNodeId,
            node.Id,
            completedAt: null,
            cancellationToken,
            externalCorrelationKey: correlationKey,
            externalMessageName: externalEvent.MessageName,
            externalTimeoutAt: timeoutAt?.ToString("o"),
            boundaryNodeId: boundaryNode?.Id);

        return new WorkflowExecutionState(
            RunId: runId,
            Status: WaitingExternalStatus,
            NextNodeId: nextNodeId,
            WaitingOnNodeId: node.Id,
            CompletedAt: null,
            TimerDueAt: timeoutAt,
            WaitingExternalCorrelationKey: correlationKey,
            WaitingExternalMessageName: externalEvent.MessageName);
    }

    /// <summary>
    /// Fires the interrupting boundary timer on an external wait whose event never arrived:
    /// records the timeout on the run's event log and continues down the boundary's outgoing
    /// flow (escalate / notify / fail) instead of leaving the run parked (#208).
    /// </summary>
    private async Task<WorkflowExecutionState> TriggerExternalWaitTimeoutAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        CheckpointPayload checkpoint,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var graph = FlowGraph.Build(definition);
        var boundaryNodeId = checkpoint.BoundaryNodeId!;

        _logger.LogWarning(
            "External wait timed out. RunId={RunId} NodeId={NodeId} BoundaryNodeId={BoundaryNodeId} TimeoutAt={TimeoutAt}",
            runId, checkpoint.WaitingOnNodeId, boundaryNodeId, deadline.ToString("o"));

        await _store.AppendEventAsync(runId, "wait_timed_out",
            Serialize(new
            {
                runId,
                nodeId = checkpoint.WaitingOnNodeId,
                boundaryNodeId,
                messageName = checkpoint.ExternalMessageName,
                correlationKey = checkpoint.ExternalCorrelationKey,
                timeoutAt = deadline.ToString("o"),
                firedAt = DateTimeOffset.UtcNow.ToString("o")
            }),
            cancellationToken);

        await _store.AppendEventAsync(runId, "boundary_event_triggered",
            Serialize(new { runId, boundaryNodeId, sourceNodeId = checkpoint.WaitingOnNodeId, reason = "external_wait_timeout" }),
            cancellationToken);

        if (checkpoint.WaitingOnNodeId is not null &&
            graph.NodeById.TryGetValue(checkpoint.WaitingOnNodeId, out var waitNode))
        {
            await _store.AppendEventAsync(runId, "node_completed",
                Serialize(new { runId, nodeId = waitNode.Id, nodeType = waitNode.ElementName, reason = "external_wait_timeout" }),
                cancellationToken);
        }

        var escalationNodeId = graph.GetSingleSuccessorOrNull(boundaryNodeId);
        if (escalationNodeId is null)
        {
            // A boundary with no outgoing flow can only mean "give up here".
            await _store.UpdateRunStatusAsync(runId, FailedStatus, completedAt: null, cancellationToken);
            await SaveCheckpointAsync(runId, FailedStatus, null, null, null, cancellationToken);
            return new WorkflowExecutionState(runId, FailedStatus, null, null, null);
        }

        return await AdvanceAsync(runId, definition, escalationNodeId, cancellationToken);
    }

    private Task ExecuteParallelTimerAsync(
        string runId,
        BpmnNodeDefinition node,
        CancellationToken cancellationToken,
        IWorkflowRuntimeStore? storeOverride = null)
    {
        var store = storeOverride ?? _store;
        return ExecuteParallelTimerCoreAsync(runId, node, store, cancellationToken);
    }

    private static async Task ExecuteParallelTimerCoreAsync(
        string runId,
        BpmnNodeDefinition node,
        IWorkflowRuntimeStore store,
        CancellationToken cancellationToken)
    {
        var duration = ParseDuration(node.TimerDuration);
        var dueAt = DateTimeOffset.UtcNow.Add(duration);

        await store.AppendEventAsync(runId, "timer_scheduled",
            Serialize(new { runId, nodeId = node.Id, dueAt = dueAt.ToString("o"), duration = node.TimerDuration ?? "PT0S" }),
            cancellationToken);

        if (duration > TimeSpan.Zero)
            await Task.Delay(duration, cancellationToken);

        await store.AppendEventAsync(runId, "timer_fired",
            Serialize(new { runId, nodeId = node.Id, firedAt = DateTime.UtcNow.ToString("o") }),
            cancellationToken);

        await store.AppendEventAsync(runId, "node_completed",
            Serialize(new { runId, nodeId = node.Id, nodeType = node.ElementName }),
            cancellationToken);
    }

    // ── Checkpoint helpers ────────────────────────────────────────────────────

    private async Task SaveCheckpointAsync(
        string runId,
        string status,
        string? nextNodeId,
        string? waitingOnNodeId,
        string? completedAt,
        CancellationToken cancellationToken,
        string? externalCorrelationKey = null,
        string? externalMessageName = null,
        string? externalTimeoutAt = null,
        string? boundaryNodeId = null)
    {
        await _store.AppendEventAsync(runId, "checkpoint_saved",
            Serialize(new CheckpointPayload(
                status, nextNodeId, waitingOnNodeId, completedAt,
                externalCorrelationKey, externalMessageName, externalTimeoutAt, boundaryNodeId)),
            cancellationToken);
    }

    private async Task<CheckpointPayload?> GetCheckpointAsync(string runId, CancellationToken cancellationToken)
    {
        var events = await _store.ListRunEventsAsync(runId, cancellationToken);
        var last = events
            .Where(static e => string.Equals(e.Type, "checkpoint_saved", StringComparison.Ordinal))
            .OrderBy(static e => e.CreatedAt)
            .LastOrDefault();

        return last is null ? null : JsonSerializer.Deserialize<CheckpointPayload>(last.Message, SerializerOptions);
    }

    private async Task CompleteNodeAsync(string runId, BpmnNodeDefinition node, CancellationToken cancellationToken)
    {
        await _store.AppendEventAsync(runId, "node_completed",
            Serialize(new { runId, nodeId = node.Id, nodeType = node.ElementName }),
            cancellationToken);
    }

    /// <summary>
    /// Finds the artifact the nearest preceding service task produced, so a userTask
    /// approval gate can carry it forward for the approval card to render (#134).
    /// </summary>
    private async Task<string?> FindWaitingApprovalArtifactNameAsync(
        string runId,
        BpmnWorkflowDefinition definition,
        string waitingOnNodeId,
        CancellationToken cancellationToken)
    {
        var precedingNode = definition.FindPrecedingServiceTaskNode(waitingOnNodeId);
        if (precedingNode is null)
        {
            return null;
        }

        var run = await _store.GetRunAsync(runId, cancellationToken);
        var precedingStep = run?.Steps.FirstOrDefault(s => s.RuntimeSnapshot?.NodeId == precedingNode.Id);
        return precedingStep?.RuntimeSnapshot?.Artifacts.FirstOrDefault()?.Name;
    }

    private async Task<Dictionary<string, string>> BuildRunVariableMapAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var entries = await _runContext.GetAllAsync(runId, cancellationToken);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            variables[entry.Key] = entry.Value;
            variables[$"run_context.{entry.Key}"] = entry.Value;
        }

        // The run's own id, so an external wait can be keyed on something the run always has and
        // that a dispatching task can derive independently (#210). A task's output only reaches
        // run context as the opaque blob "output.{nodeId}", which templating cannot index into, so
        // without this a correlation key has to be guessed up front — the "{{input.build_id}}"
        // problem. Named to match the prompt vocabulary in AgentPromptAssembler, where {{run_id}}
        // already means this; the docs promised it worked here too, which until now it did not.
        // Run context wins on collision: an explicit input stays authoritative.
        if (!variables.ContainsKey("run_id"))
        {
            variables["run_id"] = runId;
        }

        return variables;
    }

    private async Task<string> RenderCorrelationKeyAsync(
        string runId,
        string template,
        CancellationToken cancellationToken)
    {
        var variables = await BuildRunVariableMapAsync(runId, cancellationToken);

        return TemplateVariablePattern.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

    private async Task MergeExternalPayloadAsync(
        string runId,
        IReadOnlyDictionary<string, string> payload,
        CancellationToken cancellationToken)
    {
        foreach (var pair in payload)
        {
            await _runContext.SetAsync(runId, $"event.{pair.Key}", pair.Value, RunContextKinds.External, cancellationToken);
        }
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static bool IsSupportedRuntimeNode(string elementName) =>
        elementName is "startEvent" or "serviceTask" or "userTask" or "receiveTask" or "endEvent" or
                       "exclusiveGateway" or "parallelGateway" or "intermediateCatchEvent" or "boundaryEvent";

    private static void ValidateDefinition(BpmnWorkflowDefinition definition)
    {
        if (definition.Nodes.Count == 0)
            throw new InvalidOperationException("Workflow definition must include at least one node.");

        var hasStart = definition.Nodes.Any(static n => n.ElementName == "startEvent");
        var hasEnd = definition.Nodes.Any(static n => n.ElementName == "endEvent");

        if (!hasStart || !hasEnd)
            throw new InvalidOperationException("Workflow definition must include both startEvent and endEvent nodes.");

        if (definition.Nodes.Any(static n => (n.ElementName is "serviceTask" or "scriptTask") && n.Metadata is null))
            throw new InvalidOperationException("Service/script tasks must include parsed agentwerke:agentTask metadata.");

        if (definition.Nodes.Any(static n => n.ElementName == "userTask" && n.ApprovalMetadata is null))
            throw new InvalidOperationException("User tasks must include parsed agentwerke:approvalTask metadata.");
    }

    private static TimeSpan ParseDuration(string? isoDuration)
    {
        if (string.IsNullOrWhiteSpace(isoDuration))
            return TimeSpan.Zero;

        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(isoDuration);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    private sealed record CheckpointPayload(
        string Status,
        string? NextNodeId,
        string? WaitingOnNodeId,
        string? CompletedAt,
        string? ExternalCorrelationKey,
        string? ExternalMessageName,
        /// <summary>When an external wait's interrupting boundary timer expires (#208). Null when the wait has no boundary timer.</summary>
        string? ExternalTimeoutAt = null,
        /// <summary>Id of the boundary event whose flow the run follows once <see cref="ExternalTimeoutAt"/> passes.</summary>
        string? BoundaryNodeId = null);

    private enum ServiceExecutionResult { Completed, Failed, WaitingUser }

    private sealed record GatewayConditionResult(
        string FlowId, string TargetRef, string Expression, bool Result, string? Detail);

    private sealed record GatewayDecision(
        string FlowId, string TargetRef, bool UsedDefaultFlow, IReadOnlyList<GatewayConditionResult> EvaluatedConditions);

    // ── Flow graph ────────────────────────────────────────────────────────────

    private sealed class FlowGraph
    {
        public IReadOnlyDictionary<string, BpmnNodeDefinition> NodeById { get; }
        private readonly IReadOnlyDictionary<string, IReadOnlyList<BpmnSequenceFlow>> _outgoing;
        private readonly IReadOnlyDictionary<string, BpmnNodeDefinition> _timerBoundaryByHost;

        private FlowGraph(
            IReadOnlyDictionary<string, BpmnNodeDefinition> nodeById,
            IReadOnlyDictionary<string, IReadOnlyList<BpmnSequenceFlow>> outgoing,
            IReadOnlyDictionary<string, BpmnNodeDefinition> timerBoundaryByHost)
        {
            NodeById = nodeById;
            _outgoing = outgoing;
            _timerBoundaryByHost = timerBoundaryByHost;
        }

        /// <summary>
        /// The interrupting timer boundary event attached (via BPMN <c>attachedToRef</c>) to the
        /// given activity, if any. Used to bound how long a run may park on an external wait (#208).
        /// </summary>
        public BpmnNodeDefinition? GetAttachedTimerBoundary(string nodeId) =>
            _timerBoundaryByHost.TryGetValue(nodeId, out var boundary) ? boundary : null;

        public static FlowGraph Build(BpmnWorkflowDefinition definition)
        {
            var nodeById = definition.Nodes.ToDictionary(static n => n.Id, StringComparer.Ordinal);

            IReadOnlyDictionary<string, IReadOnlyList<BpmnSequenceFlow>> outgoing;

            if (definition.SequenceFlows is { Count: > 0 })
            {
                outgoing = definition.SequenceFlows
                    .GroupBy(static f => f.SourceRef, StringComparer.Ordinal)
                    .ToDictionary(
                        static g => g.Key,
                        static g => (IReadOnlyList<BpmnSequenceFlow>)g.ToList(),
                        StringComparer.Ordinal);
            }
            else
            {
                outgoing = InferFlows(definition.Nodes);
            }

            var timerBoundaryByHost = definition.Nodes
                .Where(static n => n.ElementName == "boundaryEvent"
                    && n.AttachedToRef is not null
                    && n.CancelActivity
                    && n.TimerDuration is not null)
                .GroupBy(static n => n.AttachedToRef!, StringComparer.Ordinal)
                .ToDictionary(
                    static g => g.Key,
                    static g => g.First(),
                    StringComparer.Ordinal);

            return new FlowGraph(nodeById, outgoing, timerBoundaryByHost);
        }

        public IReadOnlyList<BpmnSequenceFlow> GetOutgoing(string nodeId) =>
            _outgoing.TryGetValue(nodeId, out var flows) ? flows : [];

        public string GetSingleSuccessor(string nodeId)
        {
            var flows = GetOutgoing(nodeId);
            return flows.Count > 0
                ? flows[0].TargetRef
                : throw new InvalidOperationException($"Node '{nodeId}' has no outgoing sequence flow.");
        }

        public string? GetSingleSuccessorOrNull(string nodeId)
        {
            var flows = GetOutgoing(nodeId);
            return flows.Count > 0 ? flows[0].TargetRef : null;
        }

        public GatewayDecision GetConditionalSuccessor(string nodeId, Func<string, string?> resolveVariable)
        {
            var flows = GetOutgoing(nodeId);
            if (flows.Count == 0)
                throw new InvalidOperationException($"Exclusive gateway '{nodeId}' has no outgoing sequence flows.");

            // First flow whose condition evaluates to true wins; unconditional flows are the default path
            var evaluated = new List<GatewayConditionResult>();
            foreach (var flow in flows)
            {
                if (flow.ConditionExpression is null)
                    continue;

                var evaluation = ConditionExpressionEvaluator.Evaluate(flow.ConditionExpression, resolveVariable);
                evaluated.Add(new GatewayConditionResult(
                    flow.Id, flow.TargetRef, flow.ConditionExpression, evaluation.Result, evaluation.Detail));

                if (evaluation.Result)
                    return new GatewayDecision(flow.Id, flow.TargetRef, UsedDefaultFlow: false, evaluated);
            }

            var defaultFlow = flows.FirstOrDefault(static f => f.ConditionExpression is null)
                ?? flows[0];
            return new GatewayDecision(defaultFlow.Id, defaultFlow.TargetRef, UsedDefaultFlow: true, evaluated);
        }

        public string FindJoin(IReadOnlyList<string> branchStartIds)
        {
            // Trace each branch forward until they converge on the same node (a parallelGateway join)
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var startId in branchStartIds)
            {
                var nodeId = startId;
                while (nodeId is not null)
                {
                    if (NodeById.TryGetValue(nodeId, out var n) && n.ElementName == "parallelGateway")
                    {
                        if (!visited.Add(nodeId))
                            return nodeId; // second branch reached same join
                        break;
                    }
                    nodeId = GetSingleSuccessorOrNull(nodeId);
                }
            }

            // Single-branch path or fallback: find the next parallelGateway reachable from branchStartIds[0]
            var candidate = branchStartIds[0];
            while (candidate is not null)
            {
                if (NodeById.TryGetValue(candidate, out var cn) && cn.ElementName == "parallelGateway")
                    return candidate;
                candidate = GetSingleSuccessorOrNull(candidate)!;
            }

            throw new InvalidOperationException("Could not locate parallel join gateway.");
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<BpmnSequenceFlow>> InferFlows(
            IReadOnlyList<BpmnNodeDefinition> nodes)
        {
            var result = new Dictionary<string, List<BpmnSequenceFlow>>(StringComparer.Ordinal);

            var i = 0;
            while (i < nodes.Count - 1)
            {
                var node = nodes[i];

                if (node.ElementName == "parallelGateway")
                {
                    var joinIdx = FindParallelJoinIndex(nodes, i);
                    if (joinIdx > i)
                    {
                        // Branches: fork → each node between fork and join
                        for (var b = i + 1; b < joinIdx; b++)
                        {
                            AddFlow(result, node.Id, nodes[b].Id);
                            AddFlow(result, nodes[b].Id, nodes[joinIdx].Id);
                        }
                        // After join
                        if (joinIdx + 1 < nodes.Count)
                            AddFlow(result, nodes[joinIdx].Id, nodes[joinIdx + 1].Id);
                        i = joinIdx + 1;
                        continue;
                    }
                }

                AddFlow(result, node.Id, nodes[i + 1].Id);
                i++;
            }

            return result.ToDictionary(
                static kv => kv.Key,
                static kv => (IReadOnlyList<BpmnSequenceFlow>)kv.Value.AsReadOnly(),
                StringComparer.Ordinal);
        }

        private static void AddFlow(
            Dictionary<string, List<BpmnSequenceFlow>> dict, string src, string tgt)
        {
            if (!dict.TryGetValue(src, out var list)) { list = []; dict[src] = list; }
            list.Add(new BpmnSequenceFlow($"inf_{src}_{tgt}", src, tgt, null));
        }

        private static int FindParallelJoinIndex(IReadOnlyList<BpmnNodeDefinition> nodes, int forkIndex)
        {
            for (var k = forkIndex + 1; k < nodes.Count; k++)
            {
                if (nodes[k].ElementName == "parallelGateway")
                    return k;
            }
            return -1;
        }
    }
}
