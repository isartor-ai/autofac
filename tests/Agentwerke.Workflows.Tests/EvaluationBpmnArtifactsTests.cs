using System.Xml.Linq;
using Agentwerke.Workflows.Bpmn;
using Agentwerke.Workflows.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentwerke.Workflows.Tests;

public sealed class EvaluationBpmnArtifactsTests
{
    [Fact]
    public void VModelProcessEvaluationArtifact_ValidatesAndCapturesRequiredAgentwerkeMetadata()
    {
        var bpmn = File.ReadAllText(FindRepositoryFile("examples", "v-model-process.bpmn"));

        var result = new BpmnWorkflowValidator().Validate(bpmn);

        Assert.True(
            result.IsValid,
            $"V-model evaluation BPMN failed validation: {string.Join("; ", result.Errors.Select(static e => e.Message))}");
        Assert.NotNull(result.Definition);

        var definition = result.Definition!;
        Assert.Equal("VModelProcessEvaluation", definition.ProcessId);
        Assert.Equal(
            new[]
            {
                "Start",
                "DraftRequirements",
                "ApproveRequirementsBaseline",
                "DraftSystemArchitecture",
                "DraftComponentDesign",
                "ImplementComponents",
                "GenerateUnitTestPlan",
                "WaitUnitTestResults",
                "UnitTestResultsTimeout",
                "ValidateComponentTraceability",
                "GenerateIntegrationTestPlan",
                "WaitIntegrationTestResults",
                "IntegrationTestResultsTimeout",
                "ValidateArchitectureTraceability",
                "AnalyzeSystemTestResults",
                "WaitSystemTestResults",
                "SystemTestResultsTimeout",
                "SummarizeVerificationFailures",
                "PrepareAcceptanceTest",
                "ApproveAcceptanceSignoff",
                "PrepareTraceabilityReport",
                "End",
                "EscalateVerificationTimeout",
                "EndTimedOut"
            },
            definition.Nodes.Select(static node => node.Id).ToArray());

        var requirements = definition.Nodes.Single(static node => node.Id == "DraftRequirements");
        Assert.Equal("analyst", requirements.Metadata!.Agent);
        Assert.Equal("vmodel.requirements.draft", requirements.Metadata.Action);
        Assert.Equal("requirements", requirements.Metadata.RuntimeContract!.Metadata["phase"]);

        var implementation = definition.Nodes.Single(static node => node.Id == "ImplementComponents");
        Assert.Equal("agent_sandboxed", implementation.Metadata!.ExecutionMode);
        Assert.Equal("repo-write", implementation.Metadata.SandboxProfile);

        var approvalNodes = definition.Nodes.Where(static node => node.ApprovalMetadata is not null).ToArray();
        Assert.Equal(["ApproveRequirementsBaseline", "ApproveAcceptanceSignoff"], approvalNodes.Select(static node => node.Id));

        AssertExternalWait(definition, "WaitUnitTestResults", "test.unit.completed", "{{input.build_id}}:unit");
        AssertExternalWait(definition, "WaitIntegrationTestResults", "test.integration.completed", "{{input.build_id}}:integration");
        AssertExternalWait(definition, "WaitSystemTestResults", "test.system.completed", "{{input.build_id}}:system");

        // Every external wait is bounded by an interrupting boundary timer, so a build that never
        // reports back escalates instead of parking the run in waiting_external forever (#208).
        AssertBoundaryTimer(definition, "UnitTestResultsTimeout", "WaitUnitTestResults", "PT2H");
        AssertBoundaryTimer(definition, "IntegrationTestResultsTimeout", "WaitIntegrationTestResults", "PT4H");
        AssertBoundaryTimer(definition, "SystemTestResultsTimeout", "WaitSystemTestResults", "PT8H");

        var traceability = definition.Nodes.Single(static node => node.Id == "PrepareTraceabilityReport");
        Assert.Equal("vmodel.traceability.report", traceability.Metadata!.Action);
        Assert.Contains("requirements_baseline", traceability.Metadata.RequiresEvidence);
        Assert.Contains("acceptance_signoff", traceability.Metadata.RequiresEvidence);
    }

    [Fact]
    public void VModelPilotThreadArtifact_ValidatesAndWiresOneRequirementToOneVerificationWait()
    {
        var bpmn = File.ReadAllText(FindRepositoryFile("examples", "v-model-pilot-thread.bpmn"));

        var result = new BpmnWorkflowValidator().Validate(bpmn);

        Assert.True(
            result.IsValid,
            $"V-model pilot BPMN failed validation: {string.Join("; ", result.Errors.Select(static e => e.Message))}");

        var definition = result.Definition!;
        Assert.Equal("VModelPilotThread", definition.ProcessId);

        // One thread, not eight phases (#210): anything more here and the artifact stops being the
        // smallest shape that can prove execution.
        Assert.Equal(
            new[]
            {
                "Start",
                "ReadRequirement",
                "ImplementChange",
                "DispatchVerificationCi",
                "WaitForVerificationResult",
                "End"
            },
            definition.Nodes.Select(static node => node.Id).ToArray());

        // No approval gates: an operator decision in the loop would mask whether the thread can run
        // unattended, which is the acceptance criterion.
        Assert.DoesNotContain(definition.Nodes, static node => node.ApprovalMetadata is not null);
    }

    /// <summary>
    /// The requirement is read straight from the system of record by a deterministic tool action,
    /// so what the thread traces to is the issue itself rather than an agent's paraphrase of it.
    /// </summary>
    [Fact]
    public void VModelPilotThreadArtifact_ReadsTheRequirementFromTheSystemOfRecord()
    {
        var definition = ValidatePilotThread();
        var read = definition.Nodes.Single(static node => node.Id == "ReadRequirement");

        Assert.Equal("github.read_issue", read.Metadata!.Action);
        Assert.Equal("read-only", read.Metadata.RuntimeContract!.Permissions.Level);

        var metadata = read.Metadata.RuntimeContract.Metadata;
        Assert.Equal("{{input.requirement_id}}", metadata["tool.input.issue_number"]);
        Assert.Equal("{{input.repository}}", metadata["tool.input.repository"]);
    }

    /// <summary>
    /// The dispatch's correlation key defaults to the run id, and the wait templates {{run_id}} —
    /// the two must agree, or the callback lands on nothing and the run hangs until timeout. This is
    /// the pairing that replaces the evaluation artifact's unresolvable {{input.build_id}}:unit.
    /// </summary>
    [Fact]
    public void VModelPilotThreadArtifact_CorrelatesTheDispatchWithTheWaitOnTheRunId()
    {
        var definition = ValidatePilotThread();

        var dispatch = definition.Nodes.Single(static node => node.Id == "DispatchVerificationCi");
        Assert.Equal("cicd.trigger_deploy", dispatch.Metadata!.Action);

        var metadata = dispatch.Metadata.RuntimeContract!.Metadata;
        // Without this the agentwerke_* inputs are never sent and the workflow cannot report back.
        Assert.Equal("true", metadata["tool.input.correlate"]);
        Assert.Equal("{{input.requirement_id}}", metadata["tool.input.requirement_id"]);
        Assert.Equal("{{input.ref}}", metadata["tool.input.ref"]);
        Assert.Equal("{{input.verification_workflow}}", metadata["tool.input.workflow_file"]);

        // No tool.input.correlation_key: the tool defaults it to the run id, which is what the wait
        // below resolves to. Setting it here would let the two drift apart.
        Assert.False(metadata.ContainsKey("tool.input.correlation_key"));

        AssertExternalWait(definition, "WaitForVerificationResult", "test.unit.completed", "{{run_id}}");
    }

    [Fact]
    public void VModelPilotThreadArtifact_ImplementsInASandboxWithRepoWriteOnly()
    {
        var definition = ValidatePilotThread();
        var implement = definition.Nodes.Single(static node => node.Id == "ImplementChange");

        Assert.Equal("agent_sandboxed", implement.Metadata!.ExecutionMode);
        Assert.Equal("repo-write", implement.Metadata.SandboxProfile);
    }

    [Fact]
    public void VModelPilotThreadArtifact_WhenAgentMetadataIsRemoved_ReturnsActionableValidationError()
    {
        var bpmn = File.ReadAllText(FindRepositoryFile("examples", "v-model-pilot-thread.bpmn"));
        var invalidBpmn = RemoveExtensionElements(bpmn, "DispatchVerificationCi");

        var result = new BpmnWorkflowValidator().Validate(invalidBpmn);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("DispatchVerificationCi", error.ElementId);
        Assert.Contains("requires agentwerke:agentTask metadata", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The artifact tests above compare literals; this runs the real artifact through the real engine
    /// and asserts the wait actually arms on the run's own id — the value
    /// <see cref="Agentwerke.Agents.Tools.CicdTriggerDeployTool"/> defaults its correlation key to.
    ///
    /// The pairing spans two resolvers that know nothing about each other (the engine's run-variable
    /// map, and the orchestrator's tool-input renderer), so it is asserted from both ends rather than
    /// in one place: this pins what the wait resolves to, the dispatch node is pinned to set no
    /// correlation_key of its own, and the tool's run-id default is pinned in Agents.Tests. Drift
    /// between them does not fail a run — it leaves it waiting for a callback that can never match.
    /// </summary>
    [Fact]
    public async Task VModelPilotThreadArtifact_ArmsItsWaitOnTheRunsOwnId()
    {
        var definition = ValidatePilotThread();

        var store = new InMemoryWorkflowRuntimeStore();
        var engine = new WorkflowInstanceEngine(
            store,
            new AlwaysSucceedsServiceTaskExecutor(),
            new InMemoryRunContextRepository(),
            NullLogger<WorkflowInstanceEngine>.Instance);

        var run = await store.CreateRunAsync("VModelPilotThread", "system", CancellationToken.None);
        var state = await engine.StartAsync(
            new WorkflowEngineStartRequest("VModelPilotThread", definition, "system", ExistingRunId: run.Id),
            CancellationToken.None);

        Assert.Equal("waiting_external", state.Status);
        Assert.Equal("WaitForVerificationResult", state.WaitingOnNodeId);
        Assert.Equal("test.unit.completed", state.WaitingExternalMessageName);
        Assert.Equal(run.Id, state.WaitingExternalCorrelationKey);
    }

    private static BpmnWorkflowDefinition ValidatePilotThread()
    {
        var bpmn = File.ReadAllText(FindRepositoryFile("examples", "v-model-pilot-thread.bpmn"));
        var result = new BpmnWorkflowValidator().Validate(bpmn);

        Assert.True(
            result.IsValid,
            $"V-model pilot BPMN failed validation: {string.Join("; ", result.Errors.Select(static e => e.Message))}");

        return result.Definition!;
    }

    [Fact]
    public void VModelProcessEvaluationArtifact_WhenAgentMetadataIsRemoved_ReturnsActionableValidationError()
    {
        var bpmn = File.ReadAllText(FindRepositoryFile("examples", "v-model-process.bpmn"));
        var invalidBpmn = RemoveExtensionElements(bpmn, "GenerateUnitTestPlan");

        var result = new BpmnWorkflowValidator().Validate(invalidBpmn);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("GenerateUnitTestPlan", error.ElementId);
        Assert.Contains("requires agentwerke:agentTask metadata", error.Message, StringComparison.Ordinal);
    }

    private static void AssertExternalWait(
        BpmnWorkflowDefinition definition,
        string nodeId,
        string messageName,
        string correlationKeyTemplate)
    {
        var node = definition.Nodes.Single(node => node.Id == nodeId);

        Assert.Equal("intermediateCatchEvent", node.ElementName);
        Assert.Equal(messageName, node.ExternalEventMetadata!.MessageName);
        Assert.Equal(correlationKeyTemplate, node.ExternalEventMetadata.CorrelationKeyTemplate);
    }

    private static void AssertBoundaryTimer(
        BpmnWorkflowDefinition definition,
        string nodeId,
        string attachedToRef,
        string timerDuration)
    {
        var node = definition.Nodes.Single(node => node.Id == nodeId);

        Assert.Equal("boundaryEvent", node.ElementName);
        Assert.Equal(attachedToRef, node.AttachedToRef);
        Assert.Equal(timerDuration, node.TimerDuration);
        Assert.True(node.CancelActivity);
    }

    private static string RemoveExtensionElements(string bpmn, string nodeId)
    {
        var document = XDocument.Parse(bpmn);
        var node = document
            .Descendants()
            .Single(element => string.Equals((string?)element.Attribute("id"), nodeId, StringComparison.Ordinal));
        node.Elements().Single(static element => element.Name.LocalName == "extensionElements").Remove();

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agentwerke.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not locate the repository root (Agentwerke.sln) from the test output directory.");
        }

        return Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
    }

    /// <summary>
    /// The pilot's service tasks are not under test here — only that the flow reaches the wait and
    /// arms it with the right key — so they all just succeed.
    /// </summary>
    private sealed class AlwaysSucceedsServiceTaskExecutor : IServiceTaskExecutor
    {
        public Task<AgentTaskOutcome> ExecuteAsync(
            string runId,
            string stepId,
            BpmnNodeDefinition node,
            int attempt,
            CancellationToken cancellationToken,
            AgentExecutionProgressReporter? progressReporter = null) =>
            Task.FromResult(new AgentTaskOutcome(
                Succeeded: true,
                Output: $"stub output for {node.Id}",
                FailureReason: null));
    }
}
