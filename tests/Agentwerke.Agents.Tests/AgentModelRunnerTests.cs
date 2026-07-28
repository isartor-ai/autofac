using Agentwerke.Agents.Models;
using Agentwerke.Agents.Tools;
using Agentwerke.Application.Observability;
using Agentwerke.Domain.AgentRuntime;
using Agentwerke.Workflows.Runtime;
using Microsoft.Extensions.Options;

namespace Agentwerke.Agents.Tests;

/// <summary>
/// Covers the four acceptance criteria scenarios for issue #119:
/// success, tool denial, model failure, and redaction.
/// All tests use stub collaborators to avoid real network calls.
/// </summary>
public sealed class AgentModelRunnerTests
{
    // -----------------------------------------------------------------------
    // Stubs
    // -----------------------------------------------------------------------

    private sealed class StubLanguageModelClient : ILanguageModelClient
    {
        private readonly LanguageModelResponse _response;

        public StubLanguageModelClient(LanguageModelResponse response) => _response = response;

        public Task<LanguageModelResponse> RunAsync(
            LanguageModelRequest request,
            Func<LanguageModelToolCall, CancellationToken, Task<LanguageModelToolResult>> toolExecutor,
            CancellationToken cancellationToken,
            AgentExecutionProgressReporter? progressReporter = null) =>
            Task.FromResult(_response);
    }

    /// <summary>
    /// A client that emits a single tool call before returning the final response.
    /// </summary>
    private sealed class StubToolCallingClient : ILanguageModelClient
    {
        private readonly string _toolName;
        private readonly LanguageModelResponse _finalResponse;
        private readonly string? _progressSummary;

        public StubToolCallingClient(
            string toolName,
            LanguageModelResponse finalResponse,
            string? progressSummary = null)
        {
            _toolName = toolName;
            _finalResponse = finalResponse;
            _progressSummary = progressSummary;
        }

        public async Task<LanguageModelResponse> RunAsync(
            LanguageModelRequest request,
            Func<LanguageModelToolCall, CancellationToken, Task<LanguageModelToolResult>> toolExecutor,
            CancellationToken cancellationToken,
            AgentExecutionProgressReporter? progressReporter = null)
        {
            if (progressReporter is not null && !string.IsNullOrWhiteSpace(_progressSummary))
            {
                await progressReporter(
                    new AgentExecutionProgressUpdate(
                        AgentExecutionProgressKinds.Reasoning,
                        _progressSummary),
                    cancellationToken);
            }

            var call = new LanguageModelToolCall("call-1", _toolName, new Dictionary<string, string>());
            await toolExecutor(call, cancellationToken);
            return _finalResponse;
        }
    }

    private sealed class StubToolGateway : IToolGateway
    {
        private readonly ToolGatewayResult _result;
        public ToolGatewayRequest? LastRequest { get; private set; }

        public StubToolGateway(ToolGatewayResult result) => _result = result;

        public Task<ToolGatewayResult> ExecuteAsync(
            ToolGatewayRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class StubToolRegistry : IToolRegistry
    {
        public IReadOnlyList<IAgentTool> All() => [];
        public IAgentTool? Find(string name) => null;
        public void Register(IAgentTool tool) { }
        public void RegisterRange(IEnumerable<IAgentTool> tools) { }
    }

    private sealed class CapturingMetrics : IWorkflowMetrics
    {
        public List<(string AgentName, string ModelId, int InputTokens, int OutputTokens, double LatencyMs, double CostUsd, bool Succeeded)> ModelInvocations { get; } = [];
        public List<(string AgentName, string PolicyTag, string Kind)> PolicyDenials { get; } = [];

        public void ModelInvoked(string agentName, string modelId, int inputTokens, int outputTokens, double latencyMs, double costUsd, bool succeeded) =>
            ModelInvocations.Add((agentName, modelId, inputTokens, outputTokens, latencyMs, costUsd, succeeded));

        public void ToolPolicyDenied(string agentName, string policyTag, string kind) =>
            PolicyDenials.Add((agentName, policyTag, kind));

        // Unused in these tests:
        public void RunStarted(string workflowId, string workflowName) { }
        public void RecordWaitingExternalRuns(int total, int stale, double oldestAgeSeconds) { }
        public void RunCompleted(string workflowId, string workflowName, double durationMs) { }
        public void RunFailed(string workflowId, string workflowName, string reason) { }
        public void StepCompleted(string stepType, string agentName, double durationMs, bool succeeded) { }
        public void ApprovalCreated(string riskLevel) { }
        public void ApprovalDecided(string decision, string riskLevel) { }
        public void InteractionTransition(string toStatus, string channel, bool won) { }
        public void WebhookReceived(string source, bool triggered) { }
        public void ConnectorInvoked(string connectorId, string operation, double durationMs, bool succeeded) { }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AgentModelRunner BuildRunner(
        ILanguageModelClient client,
        IToolGateway gateway,
        CapturingMetrics metrics,
        LanguageModelOptions? options = null)
    {
        options ??= new LanguageModelOptions();
        return new AgentModelRunner(client, gateway, new StubToolRegistry(), metrics, new ModelRunBudget(), Options.Create(options));
    }

    private static ModelRunRequest MakeRequest(string agentName = "test-agent") =>
        new(
            RunId: "run-1",
            StepId: "step-1",
            AgentName: agentName,
            Action: "spec.generate",
            Environment: "test",
            PurposeType: "specification",
            PolicyTag: "sdlc-spec",
            RequiresEvidence: [],
            Attempt: 1,
            PromptSnapshot: new AgentPromptSnapshot(
                FinalPrompt: "Describe the feature.",
                RenderedAt: "2026-06-18T00:00:00Z",
                Sections: [],
                Variables: new Dictionary<string, string>(),
                SourceFiles: []),
            Contract: new AgentRuntimeContract());

    private static LanguageModelResponse SuccessResponse(int inputTokens = 100, int outputTokens = 200) =>
        new(
            Succeeded: true,
            Output: "Agent output text.",
            FailureReason: null,
            AllToolCalls: [],
            Usage: new LanguageModelTokenUsage(inputTokens, outputTokens),
            ModelId: "claude-sonnet-4-6");

    private static LanguageModelResponse SuccessResponseWithReasoning() =>
        new(
            Succeeded: true,
            Output: "Final agent output.",
            FailureReason: null,
            AllToolCalls: [],
            Usage: new LanguageModelTokenUsage(42, 24),
            ModelId: "claude-sonnet-4-6",
            ReasoningSummary: "Checked the issue context, selected a safe implementation path, and verified the result.");

    // -----------------------------------------------------------------------
    // 1. Success: model returns output; snapshot includes token usage + latency
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_SuccessfulModel_ReturnsOutputAndEmitsMetrics()
    {
        var metrics = new CapturingMetrics();
        var runner = BuildRunner(
            new StubLanguageModelClient(SuccessResponse(inputTokens: 50, outputTokens: 80)),
            new StubToolGateway(new ToolGatewayResult(true, null, null, null, new AgentToolInvocationRecord { ToolName = "noop" })),
            metrics);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Agent output text.", result.Output);
        Assert.NotNull(result.TokenUsage);
        Assert.Equal(50, result.TokenUsage.InputTokens);
        Assert.Equal(80, result.TokenUsage.OutputTokens);
        Assert.Equal("claude-sonnet-4-6", result.TokenUsage.ModelId);
        Assert.True(result.TokenUsage.ElapsedMs >= 0);
        Assert.True(result.ElapsedMs >= 0);
        Assert.NotNull(result.ModelTrace);
        Assert.Equal("completed", result.ModelTrace!.Status);
        Assert.Equal("Agent output text.", result.ModelTrace.Output);
        Assert.Equal(50, result.ModelTrace.InputTokens);
        Assert.Equal(80, result.ModelTrace.OutputTokens);
        Assert.True(result.ModelTrace.ElapsedMs >= 0);

        // Metric emitted with correct data
        Assert.Single(metrics.ModelInvocations);
        var m = metrics.ModelInvocations[0];
        Assert.Equal("test-agent", m.AgentName);
        Assert.Equal("claude-sonnet-4-6", m.ModelId);
        Assert.Equal(50, m.InputTokens);
        Assert.Equal(80, m.OutputTokens);
        Assert.True(m.Succeeded);
        Assert.True(m.CostUsd > 0);
    }

    [Fact]
    public async Task RunAsync_WhenModelProvidesReasoningSummary_StoresVisibleReasoningInTrace()
    {
        var metrics = new CapturingMetrics();
        var runner = BuildRunner(
            new StubLanguageModelClient(SuccessResponseWithReasoning()),
            new StubToolGateway(new ToolGatewayResult(true, null, null, null, new AgentToolInvocationRecord { ToolName = "noop" })),
            metrics);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.ModelTrace);
        Assert.Equal(
            "Checked the issue context, selected a safe implementation path, and verified the result.",
            result.ModelTrace!.ReasoningSummary);
    }

    // -----------------------------------------------------------------------
    // 2. Tool denial: policy rejects tool call; metric is emitted
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ToolCallDeniedByPolicy_EmitsPolicyDenialMetric()
    {
        var metrics = new CapturingMetrics();
        var deniedInvocation = new AgentToolInvocationRecord
        {
            ToolName = "github.create_branch",
            Status = "denied",
            PolicyDecisionKind = "reject"
        };
        var gatewayResult = new ToolGatewayResult(
            Succeeded: false,
            Output: null,
            FailureReason: "Policy denied: action blocked.",
            PolicyDecision: null,
            Invocation: deniedInvocation);

        // Client that calls the tool once, then returns a successful text response.
        var finalResponse = SuccessResponse();
        var runner = BuildRunner(
            new StubToolCallingClient("github.create_branch", finalResponse),
            new StubToolGateway(gatewayResult),
            metrics);

        await runner.RunAsync(MakeRequest(), CancellationToken.None);

        // Policy denial metric must have been emitted
        Assert.Single(metrics.PolicyDenials);
        var denial = metrics.PolicyDenials[0];
        Assert.Equal("test-agent", denial.AgentName);
        Assert.Equal("sdlc-spec", denial.PolicyTag);
        Assert.Equal("reject", denial.Kind);

        // Tool invocation appears in the result
    }

    [Fact]
    public async Task RunAsync_ToolCallInjectsRunContextIntoGatewayInput()
    {
        var metrics = new CapturingMetrics();
        var gateway = new StubToolGateway(new ToolGatewayResult(
            Succeeded: true,
            Output: "created",
            FailureReason: null,
            PolicyDecision: null,
            Invocation: new AgentToolInvocationRecord
            {
                ToolName = "github.create_pull_request",
                Status = "completed"
            }));

        var runner = BuildRunner(
            new StubToolCallingClient("github.create_pull_request", SuccessResponse()),
            gateway,
            metrics);

        await runner.RunAsync(MakeRequest(), CancellationToken.None);

        Assert.NotNull(gateway.LastRequest);
        Assert.Equal("run-1", gateway.LastRequest!.Input["run_id"]);
        Assert.Equal("step-1", gateway.LastRequest.Input["step_id"]);
        Assert.Equal("1", gateway.LastRequest.Input["attempt"]);
    }

    [Fact]
    public async Task RunAsync_WhenProgressReporterIsProvided_ForwardsReasoningAndToolLifecycle()
    {
        var metrics = new CapturingMetrics();
        var gateway = new StubToolGateway(new ToolGatewayResult(
            Succeeded: true,
            Output: "found the issue",
            FailureReason: null,
            PolicyDecision: null,
            Invocation: new AgentToolInvocationRecord
            {
                ToolName = "github.read_issue",
                Status = "completed"
            }));
        var runner = BuildRunner(
            new StubToolCallingClient(
                "github.read_issue",
                SuccessResponse(),
                "Inspecting the issue context before calling the repo tool."),
            gateway,
            metrics);
        var updates = new List<AgentExecutionProgressUpdate>();

        await runner.RunAsync(
            MakeRequest(),
            CancellationToken.None,
            (update, _) =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            });

        Assert.Collection(
            updates,
            update =>
            {
                Assert.Equal(AgentExecutionProgressKinds.Reasoning, update.Kind);
                Assert.Equal("Inspecting the issue context before calling the repo tool.", update.Summary);
            },
            update =>
            {
                Assert.Equal(AgentExecutionProgressKinds.ToolStarted, update.Kind);
                Assert.Equal("github.read_issue", update.ToolName);
            },
            update =>
            {
                Assert.Equal(AgentExecutionProgressKinds.ToolFinished, update.Kind);
                Assert.Equal("github.read_issue", update.ToolName);
                Assert.Equal("completed", update.Status);
                Assert.Equal("Tool 'github.read_issue' completed.", update.Summary);
            });
    }

    // -----------------------------------------------------------------------
    // 3. Model failure: LLM returns failure; metric emitted with succeeded=false
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ModelReturnsFailure_EmitsFailedMetricAndPropagatesReason()
    {
        var metrics = new CapturingMetrics();
        var failureResponse = new LanguageModelResponse(
            Succeeded: false,
            Output: null,
            FailureReason: "LLM call failed: API error 429",
            AllToolCalls: [],
            Usage: new LanguageModelTokenUsage(10, 0),
            ModelId: "claude-sonnet-4-6");

        var runner = BuildRunner(
            new StubLanguageModelClient(failureResponse),
            new StubToolGateway(new ToolGatewayResult(true, null, null, null, new AgentToolInvocationRecord { ToolName = "noop" })),
            metrics);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("LLM call failed: API error 429", result.FailureReason);
        Assert.NotNull(result.ModelTrace);
        Assert.Equal("failed", result.ModelTrace!.Status);
        Assert.Equal("LLM call failed: API error 429", result.ModelTrace.FailureReason);

        Assert.Single(metrics.ModelInvocations);
        Assert.False(metrics.ModelInvocations[0].Succeeded);
    }

    [Fact]
    public async Task RunAsync_ModelTrace_RedactsToolCallInputs()
    {
        var metrics = new CapturingMetrics();
        var response = new LanguageModelResponse(
            Succeeded: true,
            Output: "ok",
            FailureReason: null,
            AllToolCalls:
            [
                new LanguageModelToolCall(
                    "call-1",
                    "github.create_pull_request",
                    new Dictionary<string, string>
                    {
                        ["title"] = "Demo PR",
                        ["token"] = "secret-token-123"
                    })
            ],
            Usage: new LanguageModelTokenUsage(10, 5),
            ModelId: "claude-sonnet-4-6");

        var runner = BuildRunner(
            new StubLanguageModelClient(response),
            new StubToolGateway(new ToolGatewayResult(true, null, null, null, new AgentToolInvocationRecord { ToolName = "noop" })),
            metrics);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        var call = Assert.Single(result.ModelTrace!.ToolCalls);
        Assert.Equal("github.create_pull_request", call.Name);
        Assert.DoesNotContain("secret-token-123", call.InputSummary, StringComparison.Ordinal);
        Assert.Contains("[redacted]", call.InputSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WhenNoModelClientIsConfigured_ReturnsNeedsConfigStatus()
    {
        var metrics = new CapturingMetrics();
        var runner = BuildRunner(
            new NullLanguageModelClient(),
            new StubToolGateway(new ToolGatewayResult(true, null, null, null, new AgentToolInvocationRecord { ToolName = "noop" })),
            metrics);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTaskOutcomeStatuses.NeedsConfig, result.StepStatus);
        Assert.Contains("Anthropic:ApiKey", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // 4. Cost calculation uses configured rates
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_TokensConsumed_CostReflectsConfiguredRates()
    {
        var metrics = new CapturingMetrics();
        // 1000 input + 500 output at $3/$15 per MTok => $0.003 + $0.0075 = $0.0105
        var options = new LanguageModelOptions
        {
            InputCostPerMillionTokens = 3.00m,
            OutputCostPerMillionTokens = 15.00m
        };
        var runner = BuildRunner(
            new StubLanguageModelClient(SuccessResponse(inputTokens: 1000, outputTokens: 500)),
            new StubToolGateway(new ToolGatewayResult(true, null, null, null, new AgentToolInvocationRecord { ToolName = "noop" })),
            metrics,
            options);

        await runner.RunAsync(MakeRequest(), CancellationToken.None);

        var cost = metrics.ModelInvocations[0].CostUsd;
        Assert.Equal(0.0105, cost, precision: 6);
    }

    [Fact]
    public async Task RunAsync_PromptCacheTokens_ReflectedInCostMetric()
    {
        var metrics = new CapturingMetrics();
        // 1000 uncached input + 500 output + 4000 cache-read + 2000 cache-write
        // at $3 / $15 / $0.30 / $3.75 per MTok =>
        // 0.003 + 0.0075 + 0.0012 + 0.0075 = 0.0192
        var options = new LanguageModelOptions
        {
            InputCostPerMillionTokens = 3.00m,
            OutputCostPerMillionTokens = 15.00m,
            CacheReadCostPerMillionTokens = 0.30m,
            CacheWriteCostPerMillionTokens = 3.75m
        };
        var response = new LanguageModelResponse(
            Succeeded: true,
            Output: "ok",
            FailureReason: null,
            AllToolCalls: [],
            Usage: new LanguageModelTokenUsage(
                InputTokens: 1000,
                OutputTokens: 500,
                CacheCreationInputTokens: 2000,
                CacheReadInputTokens: 4000),
            ModelId: "claude-sonnet-4-6");
        var runner = BuildRunner(
            new StubLanguageModelClient(response),
            new StubToolGateway(new ToolGatewayResult(true, null, null, null, new AgentToolInvocationRecord { ToolName = "noop" })),
            metrics,
            options);

        await runner.RunAsync(MakeRequest(), CancellationToken.None);

        var cost = metrics.ModelInvocations[0].CostUsd;
        Assert.Equal(0.0192, cost, precision: 6);
    }
}
