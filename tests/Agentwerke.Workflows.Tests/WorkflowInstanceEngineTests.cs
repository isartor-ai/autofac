using Agentwerke.Domain.AgentRuntime;
using Agentwerke.Domain.Persistence;
using Agentwerke.Application.Workflows;
using Agentwerke.Workflows.Bpmn;
using Agentwerke.Workflows.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentwerke.Workflows.Tests;

public sealed class WorkflowInstanceEngineTests
{
    [Fact]
    public async Task StartAsync_ForReferenceWorkflow_ProgressesDeterministicallyToUserTaskCheckpoint()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var state = await engine.StartAsync(
            workflowDefinitionId: Guid.NewGuid().ToString(),
            definition: CreateReferenceDefinition(),
            initiator: "system",
            cancellationToken: CancellationToken.None);

        Assert.Equal("waiting_user", state.Status);
        Assert.Equal("HumanApproval", state.WaitingOnNodeId);
        Assert.Equal("Finalize", state.NextNodeId);
        Assert.Null(state.WaitingApprovalArtifactName);

        var events = await store.ListRunEventsAsync(state.RunId, CancellationToken.None);
        Assert.Contains(events, static e => e.Type == "checkpoint_saved" && e.Message.Contains("\"status\":\"running\"", StringComparison.Ordinal));
        Assert.Contains(events, static e => e.Type == "checkpoint_saved" && e.Message.Contains("\"status\":\"waiting_user\"", StringComparison.Ordinal));

        var eventTypes = events.Select(static e => e.Type).ToList();
        Assert.Contains("run_started", eventTypes);
        Assert.Contains("user_task_waiting", eventTypes);
        Assert.Contains(events, static e =>
            e.Type == "user_task_waiting" &&
            e.Message.Contains("\"purposeType\":\"manual_review\"", StringComparison.Ordinal) &&
            e.Message.Contains("\"policyTag\":\"human_approval_required\"", StringComparison.Ordinal));

        var firstRunStarted = eventTypes.FindIndex(static t => string.Equals(t, "run_started", StringComparison.Ordinal));
        var firstUserTaskWaiting = eventTypes.FindIndex(static t => string.Equals(t, "user_task_waiting", StringComparison.Ordinal));

        Assert.True(firstRunStarted >= 0);
        Assert.True(firstUserTaskWaiting > firstRunStarted);
    }

    [Fact]
    public async Task ResumeAsync_AfterUserTaskApproval_CompletesRun()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var definition = CreateReferenceDefinition();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var started = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);
        var resumed = await engine.ResumeAsync(started.RunId, definition, "reviewer", CancellationToken.None);

        Assert.Equal("completed", resumed.Status);
        Assert.Null(resumed.WaitingOnNodeId);
        Assert.NotNull(resumed.CompletedAt);

        var persistedRun = await store.GetRunAsync(started.RunId, CancellationToken.None);
        Assert.NotNull(persistedRun);
        Assert.Equal("completed", persistedRun!.Status);
        Assert.NotNull(persistedRun.CompletedAt);

        var events = await store.ListRunEventsAsync(started.RunId, CancellationToken.None);
        Assert.Contains(events, static e => e.Type == "user_task_completed");
        Assert.Contains(events, static e => e.Type == "run_completed");
    }

    [Fact]
    public async Task RecoverAsync_WhenRestartOccursAtUserTask_RestoresCheckpointState()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var definition = CreateReferenceDefinition();

        var engine1 = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);
        var started = await engine1.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        var engine2 = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);
        var recovered = await engine2.RecoverAsync(started.RunId, definition, CancellationToken.None);

        Assert.Equal(started.RunId, recovered.RunId);
        Assert.Equal("waiting_user", recovered.Status);
        Assert.Equal("HumanApproval", recovered.WaitingOnNodeId);
        Assert.Equal("Finalize", recovered.NextNodeId);
    }

    [Fact]
    public async Task StartAsync_WhenPrecedingServiceTaskProducedArtifact_CarriesArtifactNameToWaitingUserState()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new ArtifactProducingServiceTaskExecutor("requirements.md"), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), CreateReferenceDefinition(), "system", CancellationToken.None);

        Assert.Equal("waiting_user", state.Status);
        Assert.Equal("HumanApproval", state.WaitingOnNodeId);
        Assert.Equal("requirements.md", state.WaitingApprovalArtifactName);
    }

    [Fact]
    public async Task RecoverAsync_WhenRestartOccursAtUserTask_AlsoRestoresArtifactName()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var definition = CreateReferenceDefinition();

        var engine1 = new WorkflowInstanceEngine(store, new ArtifactProducingServiceTaskExecutor("architecture.md"), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);
        var started = await engine1.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        var engine2 = new WorkflowInstanceEngine(store, new ArtifactProducingServiceTaskExecutor("architecture.md"), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);
        var recovered = await engine2.RecoverAsync(started.RunId, definition, CancellationToken.None);

        Assert.Equal("waiting_user", recovered.Status);
        Assert.Equal("architecture.md", recovered.WaitingApprovalArtifactName);
    }

    [Fact]
    public async Task StartAsync_WhenTimerCatchEventIsReached_ReturnsWaitingTimerWithDueAt()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "TimerFlow",
            ProcessName: "Timer Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("Wait", "Wait", "intermediateCatchEvent", null, TimerDuration: "PT5S"),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var before = DateTimeOffset.UtcNow;
        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("waiting_timer", state.Status);
        Assert.Equal("Wait", state.WaitingOnNodeId);
        Assert.Equal("End", state.NextNodeId);
        Assert.NotNull(state.TimerDueAt);
        Assert.True(state.TimerDueAt >= before.AddSeconds(4));
    }

    [Fact]
    public async Task StartAsync_WhenMessageCatchEventIsReached_ReturnsWaitingExternalWithRenderedCorrelationKey()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var preCreatedRun = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(preCreatedRun.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        var state = await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", CreateExternalWaitDefinition(), "system", ExistingRunId: preCreatedRun.Id),
            CancellationToken.None);

        Assert.Equal("waiting_external", state.Status);
        Assert.Equal("WaitForMerge", state.WaitingOnNodeId);
        Assert.Equal("End", state.NextNodeId);
        Assert.Equal("feature/external-wait", state.WaitingExternalCorrelationKey);
        Assert.Equal("github.pull_request.merged", state.WaitingExternalMessageName);
    }

    /// <summary>
    /// {{run_id}} is the other half of the dispatch tool's correlation contract (#210): the tool
    /// defaults its key to the run id, so a wait templated on {{run_id}} resolves to the same value
    /// without either node passing data to the other.
    /// </summary>
    [Fact]
    public async Task StartAsync_WhenCorrelationTemplateUsesRunId_RendersTheRunsOwnId()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var preCreatedRun = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);

        var state = await engine.StartAsync(
            new WorkflowEngineStartRequest(
                "ExternalFlow",
                CreateExternalWaitDefinition("{{run_id}}:unit"),
                "system",
                ExistingRunId: preCreatedRun.Id),
            CancellationToken.None);

        Assert.Equal("waiting_external", state.Status);
        Assert.Equal($"{preCreatedRun.Id}:unit", state.WaitingExternalCorrelationKey);
    }

    [Fact]
    public async Task StartAsync_WhenRunContextDefinesRunId_ThatValueWinsOverTheRunsOwnId()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var preCreatedRun = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(preCreatedRun.Id, "run_id", "explicit-override", RunContextKinds.Input, CancellationToken.None);

        var state = await engine.StartAsync(
            new WorkflowEngineStartRequest(
                "ExternalFlow",
                CreateExternalWaitDefinition("{{run_id}}"),
                "system",
                ExistingRunId: preCreatedRun.Id),
            CancellationToken.None);

        Assert.Equal("explicit-override", state.WaitingExternalCorrelationKey);
    }

    [Fact]
    public async Task ResumeAsync_WhenWaitingExternalAndCorrelationKeyMatches_CompletesRunAndMergesPayload()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var definition = CreateExternalWaitDefinition();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var preCreatedRun = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(preCreatedRun.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        var started = await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", definition, "system", ExistingRunId: preCreatedRun.Id),
            CancellationToken.None);

        var resumed = await engine.ResumeAsync(
            new WorkflowEngineResumeRequest(
                preCreatedRun.Id,
                definition,
                ApprovedBy: null,
                ExternalCorrelationKey: "feature/external-wait",
                ExternalPayload: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pull_number"] = "42",
                    ["merge_sha"] = "abc123"
                },
                ResumedBy: "operator"),
            CancellationToken.None);

        Assert.Equal("completed", resumed.Status);

        var entries = await runContext.GetAllAsync(preCreatedRun.Id, CancellationToken.None);
        Assert.Contains(entries, static entry => entry.Key == "event.pull_number" && entry.Value == "42");
        Assert.Contains(entries, static entry => entry.Key == "event.merge_sha" && entry.Value == "abc123");
    }

    [Fact]
    public async Task ResumeAsync_WhenWaitingExternalAndCorrelationKeyDoesNotMatch_Throws()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var definition = CreateExternalWaitDefinition();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var preCreatedRun = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(preCreatedRun.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", definition, "system", ExistingRunId: preCreatedRun.Id),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.ResumeAsync(
                new WorkflowEngineResumeRequest(
                    preCreatedRun.Id,
                    definition,
                    ApprovedBy: null,
                    ExternalCorrelationKey: "feature/other"),
                CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_WhenExternalWaitHasBoundaryTimer_ArmsDeadline()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var run = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(run.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        var before = DateTimeOffset.UtcNow;
        var state = await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", CreateBoundedExternalWaitDefinition("PT1H"), "system", ExistingRunId: run.Id),
            CancellationToken.None);

        Assert.Equal("waiting_external", state.Status);
        Assert.NotNull(state.TimerDueAt);
        Assert.True(state.TimerDueAt >= before.AddMinutes(59));

        var events = await store.ListRunEventsAsync(run.Id, CancellationToken.None);
        Assert.Contains(events, static e => e.Type == "timer_scheduled");
    }

    [Fact]
    public async Task ResumeAsync_WhenExternalEventArrivesBeforeBoundaryTimer_CompletesWithoutTimingOut()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var definition = CreateBoundedExternalWaitDefinition("PT1H");
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var run = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(run.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", definition, "system", ExistingRunId: run.Id),
            CancellationToken.None);

        var resumed = await engine.ResumeAsync(
            new WorkflowEngineResumeRequest(
                run.Id,
                definition,
                ApprovedBy: null,
                ExternalCorrelationKey: "feature/external-wait",
                ExternalPayload: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["status"] = "passed" },
                ResumedBy: "ci"),
            CancellationToken.None);

        Assert.Equal("completed", resumed.Status);

        var events = await store.ListRunEventsAsync(run.Id, CancellationToken.None);
        Assert.DoesNotContain(events, static e => e.Type == "wait_timed_out");
        Assert.Contains(events, static e => e.Type == "external_event_received");
        // The happy path continues past the wait, not down the boundary's escalation flow.
        Assert.DoesNotContain(events, static e => e.Message.Contains("\"nodeId\":\"Escalate\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverAsync_WhenBoundaryTimerExpired_FollowsBoundaryFlowInsteadOfParking()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        // A zero duration makes the wait's deadline "now", so recovery sees it as expired —
        // the same path a real PT4H timer takes once its outbox entry fires.
        var definition = CreateBoundedExternalWaitDefinition("PT0S");
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var run = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(run.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        var started = await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", definition, "system", ExistingRunId: run.Id),
            CancellationToken.None);
        Assert.Equal("waiting_external", started.Status);

        var recovered = await engine.RecoverAsync(
            new WorkflowEngineRecoverRequest(run.Id, definition),
            CancellationToken.None);

        Assert.Equal("completed", recovered.Status);

        var events = await store.ListRunEventsAsync(run.Id, CancellationToken.None);
        var timedOut = Assert.Single(events, static e => e.Type == "wait_timed_out");
        Assert.Contains("WaitForMerge", timedOut.Message, StringComparison.Ordinal);
        Assert.Contains("MergeTimeout", timedOut.Message, StringComparison.Ordinal);
        Assert.Contains(events, static e => e.Type == "boundary_event_triggered");
        // The escalation branch ran; the wait's own successor did not.
        Assert.Contains(events, static e => e.Type == "node_entered" && e.Message.Contains("\"nodeId\":\"Escalate\"", StringComparison.Ordinal));
        Assert.DoesNotContain(events, static e => e.Type == "node_entered" && e.Message.Contains("\"nodeId\":\"Deploy\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverAsync_WhenBoundaryTimerHasNotExpired_StaysParkedAndKeepsDeadline()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var definition = CreateBoundedExternalWaitDefinition("PT1H");
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var run = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(run.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", definition, "system", ExistingRunId: run.Id),
            CancellationToken.None);

        var recovered = await engine.RecoverAsync(
            new WorkflowEngineRecoverRequest(run.Id, definition),
            CancellationToken.None);

        Assert.Equal("waiting_external", recovered.Status);
        Assert.Equal("WaitForMerge", recovered.WaitingOnNodeId);
        Assert.NotNull(recovered.TimerDueAt);
        Assert.True(recovered.TimerDueAt > DateTimeOffset.UtcNow);

        var events = await store.ListRunEventsAsync(run.Id, CancellationToken.None);
        Assert.DoesNotContain(events, static e => e.Type == "wait_timed_out");
    }

    [Fact]
    public async Task StartAsync_WhenExternalWaitHasNoBoundaryTimer_ParksWithoutDeadline()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var runContext = new InMemoryRunContextRepository();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), runContext, NullLogger<WorkflowInstanceEngine>.Instance);
        var run = await store.CreateRunAsync("ExternalFlow", "system", CancellationToken.None);
        await runContext.SetAsync(run.Id, "branch_name", "feature/external-wait", RunContextKinds.Input, CancellationToken.None);

        var state = await engine.StartAsync(
            new WorkflowEngineStartRequest("ExternalFlow", CreateExternalWaitDefinition(), "system", ExistingRunId: run.Id),
            CancellationToken.None);

        Assert.Equal("waiting_external", state.Status);
        Assert.Null(state.TimerDueAt);
    }

    [Fact]
    public async Task StartAsync_WhenExistingRunIdIsProvided_ReusesPreCreatedRun()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);
        var definition = CreateReferenceDefinition();
        var preCreatedRun = await store.CreateRunAsync("wf-existing", "system", CancellationToken.None);

        var state = await engine.StartAsync(
            new WorkflowEngineStartRequest("wf-existing", definition, "system", ExistingRunId: preCreatedRun.Id),
            CancellationToken.None);

        Assert.Equal(preCreatedRun.Id, state.RunId);
        var persistedRun = await store.GetRunAsync(preCreatedRun.Id, CancellationToken.None);
        Assert.NotNull(persistedRun);
        Assert.Equal(preCreatedRun.Id, persistedRun!.Id);
    }

    [Fact]
    public async Task StartAsync_WhenServiceTaskHasRetries_CompletesAfterRetrySequence()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "RetryFlow",
            ProcessName: "Retry Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition(
                    "Deploy",
                    "Deploy",
                    "serviceTask",
                    new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [], MaxRetries: 2, RetryBackoffSeconds: 1, FailUntilAttempt: 2)),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("completed", state.Status);
        var events = await store.ListRunEventsAsync(state.RunId, CancellationToken.None);
        Assert.Equal(3, events.Count(static e => e.Type == "service_task_attempted"));
        Assert.Equal(2, events.Count(static e => e.Type == "retry_scheduled"));
        Assert.Contains(events, static e =>
            e.Type == "service_task_attempted" &&
            e.Message.Contains("\"agent\":\"agent\"", StringComparison.Ordinal) &&
            e.Message.Contains("\"action\":\"action\"", StringComparison.Ordinal) &&
            e.Message.Contains("\"purposeType\":\"purpose\"", StringComparison.Ordinal) &&
            e.Message.Contains("\"policyTag\":\"policy\"", StringComparison.Ordinal));
        Assert.Contains(events, static e => e.Type == "run_completed");
    }

    [Fact]
    public async Task ServiceTask_WhenAgentAsksHuman_SuspendsWaitingUser_AndReRunsStepOnResume()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var executor = new AsksHumanOnceServiceTaskExecutor();
        var engine = new WorkflowInstanceEngine(store, executor, new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "AskFlow",
            ProcessName: "Ask Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition(
                    "Ask",
                    "Ask human",
                    "serviceTask",
                    new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        // First pass: the agent asks a human, so the run parks at waiting_user.
        var started = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);
        Assert.Equal("waiting_user", started.Status);
        // No approval was requested — WaitingOnNodeId is null so the executor won't create one.
        Assert.Null(started.WaitingOnNodeId);
        Assert.Null(started.WaitingApprovalArtifactName);

        var events = await store.ListRunEventsAsync(started.RunId, CancellationToken.None);
        Assert.Contains(events, static e => e.Type == "service_task_waiting_user");

        // Second pass: the human answered, so the step re-runs and the run completes.
        var resumed = await engine.ResumeAsync(started.RunId, definition, "human", CancellationToken.None);
        Assert.Equal("completed", resumed.Status);
        Assert.Equal(2, executor.Calls);
    }

    [Fact]
    public async Task StartAsync_WhenServiceTaskTimeoutTriggersBoundary_ContinuesToCompletion()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "TimeoutFlow",
            ProcessName: "Timeout Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition(
                    "LongTask",
                    "Long Task",
                    "serviceTask",
                    new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [], SimulateTimeout: true, TimeoutSeconds: 5)),
                new BpmnNodeDefinition("TaskTimeoutBoundary", "Task Timeout", "boundaryEvent", null),
                new BpmnNodeDefinition("RecoveryTask", "Recover", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("completed", state.Status);
        var events = await store.ListRunEventsAsync(state.RunId, CancellationToken.None);
        Assert.Contains(events, static e => e.Type == "timeout_triggered");
        Assert.Contains(events, static e => e.Type == "boundary_event_triggered");
        Assert.Contains(events, static e => e.Type == "run_completed");
    }

    [Fact]
    public async Task StartAsync_WhenParallelGatewayExists_ExecutesParallelSectionAndJoins()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "ParallelFlow",
            ProcessName: "Parallel Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("Fork", "Fork", "parallelGateway", null),
                new BpmnNodeDefinition("BranchA", "Branch A", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("BranchB", "Branch B", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("Join", "Join", "parallelGateway", null),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("completed", state.Status);
        var events = await store.ListRunEventsAsync(state.RunId, CancellationToken.None);
        Assert.Contains(events, static e => e.Type == "parallel_forked");
        Assert.Equal(2, events.Count(static e => e.Type == "parallel_branch_entered"));
        Assert.Contains(events, static e => e.Type == "parallel_joined");
        Assert.Contains(events, static e => e.Type == "run_completed");
    }

    [Fact]
    public async Task StartAsync_WhenServiceTaskReturnsRuntimeSnapshot_PersistsSnapshotOnStep()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new RuntimeSnapshotServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "RuntimeSnapshotFlow",
            ProcessName: "Runtime Snapshot Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("Deploy", "Deploy", "serviceTask", new AgentwerkeTaskMetadata("deploy-agent", "deploy", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        var persistedRun = await store.GetRunAsync(state.RunId, CancellationToken.None);
        var serviceStep = Assert.Single(persistedRun!.Steps);
        Assert.Equal("completed", serviceStep.Status);
        Assert.NotNull(serviceStep.RuntimeSnapshot);
        Assert.Equal("deploy-agent", serviceStep.RuntimeSnapshot!.AgentName);
        Assert.Equal(AgentPermissionLevels.ReadWrite, serviceStep.RuntimeSnapshot.Contract.Permissions.Level);
    }

    [Fact]
    public async Task StartAsync_WhenServiceTaskFails_SetsStepErrorNotOutput()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new AlwaysFailingServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "FailFlow",
            ProcessName: "Fail Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("FailTask", "Fail Task", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("failed", state.Status);
        var persistedRun = await store.GetRunAsync(state.RunId, CancellationToken.None);
        var failedStep = Assert.Single(persistedRun!.Steps);
        Assert.Equal("failed", failedStep.Status);
        Assert.Null(failedStep.Output);
        Assert.NotNull(failedStep.Error);
        Assert.Contains("always fails", failedStep.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_WhenServiceTaskNeedsModelConfiguration_CompletesRunAndMarksStepNeedsConfig()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NeedsConfigServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "NeedsConfigFlow",
            ProcessName: "Needs Config Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("AgentTask", "Agent Task", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("completed", state.Status);
        var persistedRun = await store.GetRunAsync(state.RunId, CancellationToken.None);
        Assert.Equal("completed", persistedRun!.Status);

        var step = Assert.Single(persistedRun.Steps);
        Assert.Equal(AgentTaskOutcomeStatuses.NeedsConfig, step.Status);
        Assert.Null(step.Output);
        Assert.Contains("Anthropic:ApiKey", step.Error, StringComparison.OrdinalIgnoreCase);

        var events = await store.ListRunEventsAsync(state.RunId, CancellationToken.None);
        Assert.Contains(events, static e => e.Type == "service_task_needs_config");
        Assert.DoesNotContain(events, static e => e.Type == "service_task_failed");
    }

    [Fact]
    public async Task StartAsync_WhenServiceTaskSucceeds_SetsStepOutputNotError()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new NoOpServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "SucceedFlow",
            ProcessName: "Succeed Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("Task", "Task", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("completed", state.Status);
        var persistedRun = await store.GetRunAsync(state.RunId, CancellationToken.None);
        var step = Assert.Single(persistedRun!.Steps);
        Assert.Equal("completed", step.Status);
        Assert.NotNull(step.Output);
        Assert.Null(step.Error);
        var events = await store.ListRunEventsAsync(state.RunId, CancellationToken.None);
        Assert.Contains(events, static e =>
            e.Type == "agent_reasoning_started" &&
            e.Message.Contains("preparing the model/tool loop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenAgentReportsProgress_PersistsReasoningAndToolLifecycleEvents()
    {
        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(store, new ProgressReportingServiceTaskExecutor(), new InMemoryRunContextRepository(), NullLogger<WorkflowInstanceEngine>.Instance);

        var definition = new BpmnWorkflowDefinition(
            ProcessId: "ProgressFlow",
            ProcessName: "Progress Flow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("Task", "Task", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);

        var state = await engine.StartAsync(Guid.NewGuid().ToString(), definition, "system", CancellationToken.None);

        Assert.Equal("completed", state.Status);
        var events = await store.ListRunEventsAsync(state.RunId, CancellationToken.None);
        Assert.Contains(events, static e =>
            e.Type == "agent_reasoning_delta" &&
            e.Message.Contains("Inspecting the issue context", StringComparison.Ordinal));
        Assert.Contains(events, static e =>
            e.Type == "agent_tool_call_started" &&
            e.Message.Contains("\"toolName\":\"github.read_issue\"", StringComparison.Ordinal));
        Assert.Contains(events, static e =>
            e.Type == "agent_tool_call_finished" &&
            e.Message.Contains("\"status\":\"completed\"", StringComparison.Ordinal));
        Assert.Contains(events, static e =>
            e.Type == "agent_sandbox_log" &&
            e.Message.Contains("git status --short", StringComparison.Ordinal));
    }

    private static BpmnWorkflowDefinition CreateReferenceDefinition()
    {
        return new BpmnWorkflowDefinition(
            ProcessId: "Reference",
            ProcessName: "Reference Workflow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition("Deploy", "Deploy", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition(
                    "HumanApproval",
                    "Approval",
                    "userTask",
                    null,
                    new AgentwerkeApprovalMetadata("manual_review", "human_approval_required")),
                new BpmnNodeDefinition("Finalize", "Finalize", "serviceTask", new AgentwerkeTaskMetadata("agent", "action", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);
    }

    /// <summary>
    /// External wait guarded by an interrupting boundary timer (#208): the happy path continues
    /// to Deploy, the boundary's flow escalates instead of leaving the run parked forever.
    /// </summary>
    private static BpmnWorkflowDefinition CreateBoundedExternalWaitDefinition(string timerDuration)
    {
        return new BpmnWorkflowDefinition(
            ProcessId: "ExternalFlow",
            ProcessName: "Bounded External Wait Workflow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition(
                    "WaitForMerge",
                    "Wait for Merge",
                    "intermediateCatchEvent",
                    null,
                    ExternalEventMetadata: new AgentwerkeExternalEventMetadata(
                        "github.pull_request.merged",
                        "{{run_context.branch_name}}")),
                new BpmnNodeDefinition(
                    "MergeTimeout",
                    "Merge Timed Out",
                    "boundaryEvent",
                    null,
                    TimerDuration: timerDuration,
                    AttachedToRef: "WaitForMerge"),
                new BpmnNodeDefinition(
                    "Deploy",
                    "Deploy",
                    "serviceTask",
                    new AgentwerkeTaskMetadata("agent", "deploy", null, "purpose", "policy", [])),
                new BpmnNodeDefinition(
                    "Escalate",
                    "Escalate",
                    "serviceTask",
                    new AgentwerkeTaskMetadata("agent", "escalate", null, "purpose", "policy", [])),
                new BpmnNodeDefinition("End", "End", "endEvent", null),
                new BpmnNodeDefinition("EndTimedOut", "End Timed Out", "endEvent", null)
            ],
            SequenceFlows:
            [
                new BpmnSequenceFlow("f1", "Start", "WaitForMerge"),
                new BpmnSequenceFlow("f2", "WaitForMerge", "Deploy"),
                new BpmnSequenceFlow("f3", "Deploy", "End"),
                new BpmnSequenceFlow("f4", "MergeTimeout", "Escalate"),
                new BpmnSequenceFlow("f5", "Escalate", "EndTimedOut")
            ]);
    }

    private static BpmnWorkflowDefinition CreateExternalWaitDefinition(
        string correlationKeyTemplate = "{{run_context.branch_name}}")
    {
        return new BpmnWorkflowDefinition(
            ProcessId: "ExternalFlow",
            ProcessName: "External Wait Workflow",
            Nodes:
            [
                new BpmnNodeDefinition("Start", "Start", "startEvent", null),
                new BpmnNodeDefinition(
                    "WaitForMerge",
                    "Wait for Merge",
                    "intermediateCatchEvent",
                    null,
                    TimerDuration: null,
                    ExternalEventMetadata: new AgentwerkeExternalEventMetadata(
                        "github.pull_request.merged",
                        correlationKeyTemplate)),
                new BpmnNodeDefinition("End", "End", "endEvent", null)
            ]);
    }

    private sealed class InMemoryWorkflowRuntimeStore : IWorkflowRuntimeStore
    {
        private readonly Dictionary<string, WorkflowRun> _runs = [];
        private readonly object _sync = new();

        public Task<WorkflowRun> CreateRunAsync(string workflowDefinitionId, string? initiator, CancellationToken cancellationToken, string? correlationId = null)
        {
            var run = new WorkflowRun
            {
                Id = Guid.NewGuid().ToString(),
                WorkflowId = workflowDefinitionId,
                Status = "created",
                RequestedBy = initiator ?? "unknown",
                StartedAt = DateTime.UtcNow.ToString("o")
            };

            lock (_sync)
            {
                _runs[run.Id] = run;
            }

            return Task.FromResult(run);
        }

        public Task<WorkflowRun?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _runs.TryGetValue(runId, out var run);
                return Task.FromResult(run);
            }
        }

        public Task<IReadOnlyList<WorkflowEvent>> ListRunEventsAsync(string runId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_runs.TryGetValue(runId, out var run))
                {
                    return Task.FromResult<IReadOnlyList<WorkflowEvent>>(run.Events.AsReadOnly());
                }
                return Task.FromResult<IReadOnlyList<WorkflowEvent>>(new List<WorkflowEvent>().AsReadOnly());
            }
        }

        public Task AppendEventAsync(string runId, string type, string message, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_runs.TryGetValue(runId, out var run))
                {
                    run.Events.Add(new WorkflowEvent
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = type,
                        Message = message,
                        CreatedAt = DateTime.UtcNow.ToString("o")
                    });
                }
            }

            return Task.CompletedTask;
        }

        public Task UpdateRunStatusAsync(string runId, string status, string? completedAt, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (!_runs.TryGetValue(runId, out var run))
                {
                    throw new InvalidOperationException($"Run {runId} does not exist.");
                }

                run.Status = status;
                run.CompletedAt = completedAt;
            }

            return Task.CompletedTask;
        }

        public Task<WorkflowRunStep> CreateStepAsync(
            string runId, string nodeId, string? nodeName, string nodeType,
            string? agentName, CancellationToken cancellationToken)
        {
            var step = new WorkflowRunStep
            {
                Id = $"step_{Guid.NewGuid():N}",
                Name = nodeName ?? nodeId,
                Type = nodeType,
                Status = "running",
                StartedAt = DateTime.UtcNow.ToString("o"),
                AgentName = agentName
            };

            lock (_sync)
            {
                if (_runs.TryGetValue(runId, out var run))
                {
                    run.Steps.Add(step);
                }
            }

            return Task.FromResult(step);
        }

        public Task UpdateStepStatusAsync(
            string stepId, string status, string? output, string? error, string? completedAt,
            PolicyDecision? policyDecision,
            AgentRuntimeSnapshot? runtimeSnapshot,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                var step = _runs.Values
                    .SelectMany(static run => run.Steps)
                    .FirstOrDefault(step => string.Equals(step.Id, stepId, StringComparison.Ordinal));

                if (step is not null)
                {
                    step.Status = status;
                    step.Output = output;
                    step.Error = error;
                    step.CompletedAt = completedAt;
                    step.PolicyDecision = policyDecision;
                    step.RuntimeSnapshot = runtimeSnapshot;
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailingServiceTaskExecutor : IServiceTaskExecutor
    {
        public Task<AgentTaskOutcome> ExecuteAsync(
            string runId, string stepId, BpmnNodeDefinition node,
            int attempt, CancellationToken cancellationToken, AgentExecutionProgressReporter? progressReporter = null)
        {
            return Task.FromResult(new AgentTaskOutcome(
                Succeeded: false,
                Output: null,
                FailureReason: "This executor always fails."));
        }
    }

    private sealed class NeedsConfigServiceTaskExecutor : IServiceTaskExecutor
    {
        public Task<AgentTaskOutcome> ExecuteAsync(
            string runId, string stepId, BpmnNodeDefinition node,
            int attempt, CancellationToken cancellationToken, AgentExecutionProgressReporter? progressReporter = null)
        {
            return Task.FromResult(new AgentTaskOutcome(
                Succeeded: false,
                Output: null,
                FailureReason: "No language model client is configured. Set 'Anthropic:ApiKey' in configuration.",
                StepStatus: AgentTaskOutcomeStatuses.NeedsConfig));
        }
    }

    private sealed class ArtifactProducingServiceTaskExecutor(string artifactName) : IServiceTaskExecutor
    {
        public Task<AgentTaskOutcome> ExecuteAsync(
            string runId, string stepId, BpmnNodeDefinition node,
            int attempt, CancellationToken cancellationToken, AgentExecutionProgressReporter? progressReporter = null)
        {
            return Task.FromResult(new AgentTaskOutcome(
                Succeeded: true,
                Output: "artifact-producing executor",
                FailureReason: null,
                RuntimeSnapshot: new AgentRuntimeSnapshot
                {
                    RunId = runId,
                    StepId = stepId,
                    NodeId = node.Id,
                    AgentName = node.Metadata?.Agent,
                    Action = node.Metadata?.Action,
                    Artifacts = [new AgentArtifactRecord { Name = artifactName }]
                }));
        }
    }

    private sealed class NoOpServiceTaskExecutor : IServiceTaskExecutor
    {
        public Task<AgentTaskOutcome> ExecuteAsync(
            string runId, string stepId, BpmnNodeDefinition node,
            int attempt, CancellationToken cancellationToken, AgentExecutionProgressReporter? progressReporter = null)
        {
            var metadata = node.Metadata;

            if (metadata is not null && attempt <= metadata.FailUntilAttempt)
            {
                return Task.FromResult(new AgentTaskOutcome(
                    Succeeded: false,
                    Output: null,
                    FailureReason: $"Simulated failure on attempt {attempt}"));
            }

            return Task.FromResult(new AgentTaskOutcome(
                Succeeded: true,
                Output: "no-op executor",
                FailureReason: null));
        }
    }

    private sealed class AsksHumanOnceServiceTaskExecutor : IServiceTaskExecutor
    {
        public int Calls { get; private set; }

        public Task<AgentTaskOutcome> ExecuteAsync(
            string runId, string stepId, BpmnNodeDefinition node,
            int attempt, CancellationToken cancellationToken, AgentExecutionProgressReporter? progressReporter = null)
        {
            Calls++;

            // First execution: the agent asks a human and the run must suspend (#192).
            if (Calls == 1)
            {
                return Task.FromResult(new AgentTaskOutcome(
                    Succeeded: false,
                    Output: null,
                    FailureReason: "Awaiting response to: which auth scheme?",
                    StepStatus: AgentTaskOutcomeStatuses.WaitingUser));
            }

            // Re-run after the answer is available: complete normally.
            return Task.FromResult(new AgentTaskOutcome(
                Succeeded: true,
                Output: "used SessionAuth",
                FailureReason: null));
        }
    }

    private sealed class RuntimeSnapshotServiceTaskExecutor : IServiceTaskExecutor
    {
        public Task<AgentTaskOutcome> ExecuteAsync(
            string runId, string stepId, BpmnNodeDefinition node,
            int attempt, CancellationToken cancellationToken, AgentExecutionProgressReporter? progressReporter = null)
        {
            return Task.FromResult(new AgentTaskOutcome(
                Succeeded: true,
                Output: "runtime snapshot executor",
                FailureReason: null,
                RuntimeSnapshot: new AgentRuntimeSnapshot
                {
                    RunId = runId,
                    StepId = stepId,
                    NodeId = node.Id,
                    AgentName = node.Metadata?.Agent,
                    Action = node.Metadata?.Action,
                    Contract = new AgentRuntimeContract
                    {
                        Permissions = new AgentPermissionContract
                        {
                            Level = AgentPermissionLevels.ReadWrite
                        }
                    }
                }));
        }
    }

    private sealed class ProgressReportingServiceTaskExecutor : IServiceTaskExecutor
    {
        public async Task<AgentTaskOutcome> ExecuteAsync(
            string runId,
            string stepId,
            BpmnNodeDefinition node,
            int attempt,
            CancellationToken cancellationToken,
            AgentExecutionProgressReporter? progressReporter = null)
        {
            if (progressReporter is not null)
            {
                await progressReporter(
                    new AgentExecutionProgressUpdate(
                        AgentExecutionProgressKinds.Reasoning,
                        "Inspecting the issue context before opening repo tools."),
                    cancellationToken);
                await progressReporter(
                    new AgentExecutionProgressUpdate(
                        AgentExecutionProgressKinds.ToolStarted,
                        "Calling tool 'github.read_issue'.",
                        ToolName: "github.read_issue",
                        ToolCallId: "call-1",
                        Status: "started"),
                    cancellationToken);
                await progressReporter(
                    new AgentExecutionProgressUpdate(
                        AgentExecutionProgressKinds.ToolFinished,
                        "Tool 'github.read_issue' completed.",
                        ToolName: "github.read_issue",
                        ToolCallId: "call-1",
                        Status: "completed"),
                    cancellationToken);
                await progressReporter(
                    new AgentExecutionProgressUpdate(
                        AgentExecutionProgressKinds.SandboxLog,
                        "git status --short",
                        Status: "stdout"),
                    cancellationToken);
            }

            return new AgentTaskOutcome(
                Succeeded: true,
                Output: "progress executor",
                FailureReason: null);
        }
    }
}
