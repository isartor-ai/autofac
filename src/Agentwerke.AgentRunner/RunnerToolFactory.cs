using Agentwerke.Agents.Models;
using Agentwerke.Agents.Tools;
using Agentwerke.AgentSecOps;
using Agentwerke.Application.Observability;
using Agentwerke.Application.Secrets;
using Agentwerke.Domain.Persistence;
using Agentwerke.Integrations;
using Microsoft.Extensions.Options;

namespace Agentwerke.AgentRunner;

internal static class RunnerToolFactory
{
    public static IToolRegistry CreateRegistry(SandboxedAgentRunEnvelope envelope)
    {
        var tools = new List<IAgentTool>();
        if (envelope.ResolvedTools.Any(static tool => IsGitHubTool(tool.Name)))
        {
            var gitHubConnector = CreateGitHubConnector();
            if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "github.read_issue", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new GitHubReadIssueTool(gitHubConnector));
            }

            if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "github.comment_issue", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new GitHubCommentIssueTool(gitHubConnector));
            }

            if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "github.close_issue", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new GitHubCloseIssueTool(gitHubConnector));
            }

            if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "github.create_branch", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new GitHubCreateBranchTool(gitHubConnector));
            }

            if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "github.create_pull_request", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new GitHubCreatePullRequestTool(gitHubConnector));
            }

            if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "github.request_review", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new GitHubRequestReviewTool(gitHubConnector));
            }

            if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "github.post_review", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new GitHubPostReviewTool(gitHubConnector));
            }
        }

        if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "sandbox.file_read", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add(new SandboxFileReadTool());
        }

        if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "sandbox.file_write", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add(new SandboxFileWriteTool());
        }

        if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "sandbox.file_edit", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add(new SandboxFileEditTool());
        }

        if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "sandbox.git", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add(new SandboxGitTool(
                Environment.GetEnvironmentVariable("Integrations__GitHub__RepositoryOwner"),
                Environment.GetEnvironmentVariable("Integrations__GitHub__RepositoryName"),
                Environment.GetEnvironmentVariable("Integrations__GitHub__PersonalAccessToken")));
        }

        if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "sandbox.shell", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add(new SandboxShellTool());
        }

        if (envelope.ResolvedTools.Any(static tool => string.Equals(tool.Name, "sandbox.run_tests", StringComparison.OrdinalIgnoreCase)))
        {
            tools.Add(new SandboxRunTestsTool());
        }

        return new ToolRegistry(tools);
    }

    private static bool IsGitHubTool(string toolName) =>
        string.Equals(toolName, "github.read_issue", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "github.comment_issue", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "github.close_issue", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "github.create_branch", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "github.create_pull_request", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "github.request_review", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "github.post_review", StringComparison.OrdinalIgnoreCase);

    private static IGitHubConnector CreateGitHubConnector()
    {
        var apiBaseUrl = Environment.GetEnvironmentVariable("Integrations__GitHub__ApiBaseUrl")
            ?? "https://api.github.com/";
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute)
        };

        return new GitHubConnector(
            httpClient,
            Options.Create(new IntegrationOptions
            {
                GitHub = new GitHubOptions
                {
                    Enabled = ReadBool("Integrations__GitHub__Enabled"),
                    ApiBaseUrl = apiBaseUrl,
                    RepositoryOwner = Environment.GetEnvironmentVariable("Integrations__GitHub__RepositoryOwner") ?? string.Empty,
                    RepositoryName = Environment.GetEnvironmentVariable("Integrations__GitHub__RepositoryName") ?? string.Empty,
                    PersonalAccessToken = Environment.GetEnvironmentVariable("Integrations__GitHub__PersonalAccessToken") ?? string.Empty,
                    DefaultBaseBranch = Environment.GetEnvironmentVariable("Integrations__GitHub__DefaultBaseBranch") ?? "main",
                    BranchPrefix = Environment.GetEnvironmentVariable("Integrations__GitHub__BranchPrefix") ?? "agentwerke/run-",
                    CreateDraftPullRequests = ReadBool("Integrations__GitHub__CreateDraftPullRequests", defaultValue: true)
                }
            }),
            new EnvironmentSecretStore(),
            new PolicyEvaluationService(),
            new NullAuditRepository(),
            new NullWorkflowMetrics(),
            new StaticCorrelationContext(),
            new NullWorkflowTracer());
    }

    private static bool ReadBool(string name, bool defaultValue = false) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

    private sealed class EnvironmentSecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            var envName = key.Replace(":", "__", StringComparison.Ordinal);
            return Task.FromResult(Environment.GetEnvironmentVariable(envName));
        }
    }

    private sealed class NullAuditRepository : IAuditRepository
    {
        public Task AddAsync(AuditRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullWorkflowMetrics : IWorkflowMetrics
    {
        public void RunStarted(string workflowId, string workflowName) { }
        public void RunCompleted(string workflowId, string workflowName, double durationMs) { }
        public void RunFailed(string workflowId, string workflowName, string reason) { }
        public void StepCompleted(string stepType, string agentName, double durationMs, bool succeeded) { }
        public void ApprovalCreated(string riskLevel) { }
        public void ApprovalDecided(string decision, string riskLevel) { }
        public void InteractionTransition(string toStatus, string channel, bool won) { }
        public void WebhookReceived(string source, bool triggered) { }
        public void ConnectorInvoked(string connectorId, string operation, double durationMs, bool succeeded) { }
        public void ModelInvoked(string agentName, string modelId, int inputTokens, int outputTokens, double latencyMs, double costUsd, bool succeeded) { }
        public void ToolPolicyDenied(string agentName, string policyTag, string kind) { }
        public void RecordWaitingExternalRuns(int total, int stale, double oldestAgeSeconds) { }
    }

    private sealed class StaticCorrelationContext : ICorrelationContext
    {
        public string CorrelationId { get; } = $"sandbox-{Guid.NewGuid():N}";
    }

    private sealed class NullWorkflowTracer : IWorkflowTracer
    {
        public ISpan StartSpan(string name) => new NullSpan();
    }

    private sealed class NullSpan : ISpan
    {
        public void Dispose() { }
        public void SetError(Exception ex) { }
        public void SetTag(string key, string value) { }
    }
}
