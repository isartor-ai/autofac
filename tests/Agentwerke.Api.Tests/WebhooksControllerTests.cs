using System.Text;
using System.Text.Json;
using Agentwerke.Api.Controllers;
using Agentwerke.Application.Agents;
using Agentwerke.Application.Secrets;
using Agentwerke.Application.Workflows;
using Agentwerke.Domain.Persistence;
using Agentwerke.Integrations;
using Agentwerke.Integrations.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentwerke.Api.Tests;

public sealed class WebhooksControllerTests
{
    [Fact]
    public async Task GitHub_PullRequestMergedEvent_RecordsExternalWorkflowEventWithoutStartingARun()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateController(orchestration, eventRepository, eventHeader: "pull_request");

        var body = """
            {
              "action": "closed",
              "pull_request": {
                "number": 42,
                "html_url": "https://github.com/octo/agentwerke/pull/42",
                "merged": true,
                "merge_commit_sha": "feedface1234",
                "head": { "ref": "agentwerke/run-123", "sha": "abc123" },
                "base": { "ref": "main", "sha": "def456" }
              },
              "repository": { "full_name": "octo/agentwerke" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(orchestration.StartCommand);
        Assert.Null(orchestration.ResumeExternalCommand);

        var recorded = Assert.Single(eventRepository.Added);
        Assert.Equal("github.pull_request.merged", recorded.Kind);
        Assert.Equal("agentwerke/run-123", recorded.CorrelationHint);
        Assert.Contains("\"merged\":true", recorded.Payload, StringComparison.Ordinal);
        Assert.Contains("\"mergeCommitSha\":\"feedface1234\"", recorded.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_PullRequestMergedEvent_WhenAWaitingRunMatchesTheBranch_AutoResumesIt()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(runId: "run_waiting_123");
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "pull_request",
            waitingExternalCorrelationRepository: waitingRepository);

        var body = """
            {
              "action": "closed",
              "pull_request": {
                "number": 42,
                "merged": true,
                "merge_commit_sha": "feedface1234",
                "head": { "ref": "agentwerke/run-waiting-123", "sha": "abc123" },
                "base": { "ref": "main", "sha": "def456" }
              }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(orchestration.ResumeExternalCommand);
        Assert.Equal("run_waiting_123", orchestration.ResumeExternalCommand!.RunId);
        Assert.Equal("agentwerke/run-waiting-123", orchestration.ResumeExternalCommand.CorrelationKey);
        Assert.Equal("github-webhook", orchestration.ResumeExternalCommand.ResumedBy);
        Assert.Equal("42", orchestration.ResumeExternalCommand.Payload["pr_number"]);
        Assert.Equal("feedface1234", orchestration.ResumeExternalCommand.Payload["merge_commit_sha"]);
    }

    [Fact]
    public async Task GitHub_WorkflowRunCompletedEvent_WhenAWaitingRunMatchesTheBranch_AutoResumesIt()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(runId: "run_waiting_456");
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "workflow_run",
            waitingExternalCorrelationRepository: waitingRepository);

        var body = """
            {
              "action": "completed",
              "workflow_run": {
                "status": "completed",
                "conclusion": "success",
                "head_sha": "abc123",
                "head_branch": "agentwerke/run-waiting-456"
              }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(orchestration.ResumeExternalCommand);
        Assert.Equal("run_waiting_456", orchestration.ResumeExternalCommand!.RunId);
        Assert.Equal("agentwerke/run-waiting-456", orchestration.ResumeExternalCommand.CorrelationKey);
        Assert.Equal("success", orchestration.ResumeExternalCommand.Payload["conclusion"]);
    }

    [Fact]
    public async Task GitHub_IssueCommentApprovedEvent_WhenAWaitingRunMatchesTheIssueNumber_AutoResumesIt()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(
            runId: "run_waiting_issue",
            onlyMatchMessage: "github.issue_comment.approved");
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "issue_comment",
            waitingExternalCorrelationRepository: waitingRepository);

        var body = """
            {
              "action": "created",
              "issue": {
                "number": 42,
                "html_url": "https://github.com/isartor-ai/agentwerke-demo/issues/42",
                "title": "Build Todo app"
              },
              "comment": {
                "id": 1001,
                "html_url": "https://github.com/isartor-ai/agentwerke-demo/issues/42#issuecomment-1001",
                "body": "approved",
                "user": { "login": "human-reviewer" }
              },
              "repository": { "full_name": "isartor-ai/agentwerke-demo" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(orchestration.ResumeExternalCommand);
        Assert.Equal("run_waiting_issue", orchestration.ResumeExternalCommand!.RunId);
        Assert.Equal("42", orchestration.ResumeExternalCommand.CorrelationKey);
        Assert.Equal("github-webhook", orchestration.ResumeExternalCommand.ResumedBy);
        Assert.Equal("true", orchestration.ResumeExternalCommand.Payload["approved"]);
        Assert.Equal("approved", orchestration.ResumeExternalCommand.Payload["comment_body"]);
        Assert.Equal("human-reviewer", orchestration.ResumeExternalCommand.Payload["comment_author"]);

        var recorded = Assert.Single(eventRepository.Added);
        Assert.Equal("github.issue_comment.approved", recorded.Kind);
        Assert.Equal("42", recorded.CorrelationHint);
    }

    [Fact]
    public async Task GitHub_IssueCommentCreatedEvent_WithoutApprovalToken_RecordsButDoesNotResume()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(
            runId: "run_waiting_issue",
            onlyMatchMessage: "github.issue_comment.approved");
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "issue_comment",
            waitingExternalCorrelationRepository: waitingRepository);

        var body = """
            {
              "action": "created",
              "issue": {
                "number": 42,
                "html_url": "https://github.com/isartor-ai/agentwerke-demo/issues/42"
              },
              "comment": {
                "id": 1002,
                "body": "I have one question before approval.",
                "user": { "login": "human-reviewer" }
              },
              "repository": { "full_name": "isartor-ai/agentwerke-demo" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(orchestration.ResumeExternalCommand);
        var recorded = Assert.Single(eventRepository.Added);
        Assert.Equal("github.issue_comment.created", recorded.Kind);
        Assert.Equal("42", recorded.CorrelationHint);
        Assert.Contains("\"approved\":false", recorded.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_PullRequestMergedEvent_ForAnUnrelatedBranch_DoesNotResumeAnyRun()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(runId: null);
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "pull_request",
            waitingExternalCorrelationRepository: waitingRepository);

        var body = """
            {
              "action": "closed",
              "pull_request": {
                "number": 99,
                "merged": true,
                "head": { "ref": "some-other-branch", "sha": "zzz999" },
                "base": { "ref": "main", "sha": "def456" }
              }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(orchestration.ResumeExternalCommand);
        Assert.Single(eventRepository.Added);
    }

    [Fact]
    public async Task GitHub_PullRequestOpenedEvent_RecordsActionSpecificKind()
    {
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateController(new CapturingOrchestrationService(), eventRepository, eventHeader: "pull_request");

        var body = """
            {
              "action": "opened",
              "pull_request": {
                "number": 7,
                "merged": false,
                "head": { "ref": "feature/x", "sha": "sha1" },
                "base": { "ref": "main", "sha": "sha2" }
              }
            }
            """;
        SetBody(controller, body);

        await controller.GitHub(CancellationToken.None);

        var recorded = Assert.Single(eventRepository.Added);
        Assert.Equal("github.pull_request.opened", recorded.Kind);
        Assert.Equal("feature/x", recorded.CorrelationHint);
    }

    [Fact]
    public async Task GitHub_WorkflowRunCompletedEvent_RecordsExternalWorkflowEvent()
    {
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateController(new CapturingOrchestrationService(), eventRepository, eventHeader: "workflow_run");

        var body = """
            {
              "action": "completed",
              "workflow_run": {
                "status": "completed",
                "conclusion": "success",
                "head_sha": "abc123",
                "head_branch": "agentwerke/run-123"
              },
              "repository": { "full_name": "octo/agentwerke" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var recorded = Assert.Single(eventRepository.Added);
        Assert.Equal("github.workflow_run.completed", recorded.Kind);
        Assert.Equal("agentwerke/run-123", recorded.CorrelationHint);
        Assert.Contains("\"conclusion\":\"success\"", recorded.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_CheckSuiteCompletedEvent_RecordsExternalWorkflowEvent()
    {
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateController(new CapturingOrchestrationService(), eventRepository, eventHeader: "check_suite");

        var body = """
            {
              "action": "completed",
              "check_suite": {
                "status": "completed",
                "conclusion": "failure",
                "head_sha": "abc123",
                "head_branch": "agentwerke/run-123"
              }
            }
            """;
        SetBody(controller, body);

        await controller.GitHub(CancellationToken.None);

        var recorded = Assert.Single(eventRepository.Added);
        Assert.Equal("github.check_suite.completed", recorded.Kind);
        Assert.Equal("failure", System.Text.Json.JsonDocument.Parse(recorded.Payload).RootElement.GetProperty("conclusion").GetString());
    }

    [Fact]
    public async Task GitHub_UnhandledEventType_IsSkippedWithoutRecordingOrStartingARun()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateController(orchestration, eventRepository, eventHeader: "push");
        SetBody(controller, "{}");

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(eventRepository.Added);
        Assert.Null(orchestration.StartCommand);
    }

    [Fact]
    public async Task GitHub_IssuesEventStillTriggersARun()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "issues",
            triggerRouter: new StubTriggerRouter("wf_1"));

        var body = """
            {
              "action": "opened",
              "issue": { "number": 5, "html_url": "https://github.com/octo/agentwerke/issues/5", "title": "Idea", "body": "Do the thing", "state": "open", "labels": [ { "name": "agentwerke" } ] },
              "repository": { "full_name": "octo/agentwerke" },
              "sender": { "login": "alice" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(orchestration.StartCommand);
        Assert.Equal("wf_1", orchestration.StartCommand!.WorkflowId);
        Assert.Empty(eventRepository.Added);
    }

    [Fact]
    public async Task GitHub_IssuesEvent_MissingRequiredLabel_IsSkippedWithoutStartingARun()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "issues",
            triggerRouter: new StubTriggerRouter("wf_1"));

        var body = """
            {
              "action": "opened",
              "issue": { "number": 6, "html_url": "https://github.com/octo/agentwerke/issues/6", "title": "Unrelated", "body": "Not for Agentwerke", "state": "open", "labels": [ { "name": "bug" } ] },
              "repository": { "full_name": "octo/agentwerke" },
              "sender": { "login": "alice" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(orchestration.StartCommand);
        Assert.Empty(eventRepository.Added);
    }

    [Fact]
    public async Task GitHub_IssuesEvent_NoLabelsAtAll_IsSkippedWithoutStartingARun()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            eventHeader: "issues",
            triggerRouter: new StubTriggerRouter("wf_1"));

        var body = """
            {
              "action": "opened",
              "issue": { "number": 7, "html_url": "https://github.com/octo/agentwerke/issues/7", "title": "No labels", "body": "Do the thing", "state": "open" },
              "repository": { "full_name": "octo/agentwerke" },
              "sender": { "login": "alice" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(orchestration.StartCommand);
    }

    [Fact]
    public async Task GitHub_IssuesEvent_RequiredLabelMatchIsCaseInsensitive()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            eventHeader: "issues",
            triggerRouter: new StubTriggerRouter("wf_1"));

        var body = """
            {
              "action": "opened",
              "issue": { "number": 8, "html_url": "https://github.com/octo/agentwerke/issues/8", "title": "Casing", "body": "Do the thing", "state": "open", "labels": [ { "name": "AgentWerke" } ] },
              "repository": { "full_name": "octo/agentwerke" },
              "sender": { "login": "alice" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(orchestration.StartCommand);
    }

    [Fact]
    public async Task GitHub_IssuesEvent_WhenRequiredLabelIsBlank_TriggersRegardlessOfLabels()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            eventHeader: "issues",
            triggerRouter: new StubTriggerRouter("wf_1"),
            requiredLabel: string.Empty);

        var body = """
            {
              "action": "opened",
              "issue": { "number": 9, "html_url": "https://github.com/octo/agentwerke/issues/9", "title": "Opt out", "body": "Do the thing", "state": "open" },
              "repository": { "full_name": "octo/agentwerke" },
              "sender": { "login": "alice" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(orchestration.StartCommand);
    }

    [Fact]
    public async Task GitHub_IssuesEvent_PassesRepositoryAndIssueUrlAsTriggerInputs()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            eventHeader: "issues",
            triggerRouter: new StubTriggerRouter("wf_1"));

        var body = """
            {
              "action": "opened",
              "issue": { "number": 142, "html_url": "https://github.com/isartor-ai/agentwerke/issues/142", "title": "Seed inputs", "body": "Do the thing", "state": "open", "labels": [ { "name": "agentwerke" } ] },
              "repository": { "full_name": "isartor-ai/agentwerke" },
              "sender": { "login": "alice" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(orchestration.StartCommand?.Trigger?.Inputs);
        Assert.Equal("isartor-ai/agentwerke", orchestration.StartCommand!.Trigger!.Inputs!["repository"]);
        Assert.Equal("142", orchestration.StartCommand.Trigger.Inputs["issue_number"]);
        Assert.Equal(
            "https://github.com/isartor-ai/agentwerke/issues/142",
            orchestration.StartCommand.Trigger.Inputs["issue_url"]);
    }

    [Fact]
    public async Task Jira_IssueCreated_SeedsEnrichedTicketContextAsInputs()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            eventHeader: "issues",
            triggerRouter: new StubTriggerRouter("wf_1"));

        var body = """
            {
              "webhookEvent": "jira:issue_created",
              "issue": {
                "id": "10001",
                "key": "ENG-42",
                "self": "https://acme.atlassian.net/rest/api/2/issue/10001",
                "fields": {
                  "summary": "Add dark mode",
                  "description": "As a user I want dark mode.",
                  "issuetype": { "name": "Story" },
                  "project": { "key": "ENG", "name": "Engineering" },
                  "priority": { "name": "High" },
                  "status": { "name": "To Do" },
                  "labels": ["frontend", "ux"],
                  "assignee": { "displayName": "Alice Dev" },
                  "reporter": { "displayName": "Bob PM" }
                }
              },
              "user": { "displayName": "Bob PM" }
            }
            """;
        SetBody(controller, body);

        var result = await controller.Jira(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        var trigger = orchestration.StartCommand?.Trigger;
        Assert.NotNull(trigger?.Inputs);
        Assert.Equal("Add dark mode", trigger!.Title);
        Assert.Equal("As a user I want dark mode.", trigger.Body);
        var inputs = trigger.Inputs!;
        Assert.Equal("ENG-42", inputs["issue_key"]);
        Assert.Equal("Story", inputs["issue_type"]);
        Assert.Equal("ENG", inputs["project_key"]);
        Assert.Equal("Engineering", inputs["project_name"]);
        Assert.Equal("High", inputs["priority"]);
        Assert.Equal("To Do", inputs["status"]);
        Assert.Equal("frontend, ux", inputs["labels"]);
        Assert.Equal("Alice Dev", inputs["assignee"]);
        Assert.Equal("Bob PM", inputs["reporter"]);
        Assert.Equal("https://acme.atlassian.net/rest/api/2/issue/10001", inputs["issue_url"]);
    }

    [Fact]
    public async Task GitHub_WorkflowRunCompletedEvent_WhenOnlyTheCommitShaMatchesAWaitingRun_StillAutoResumesIt()
    {
        // Simulates the #139 "wait for CI green after a deploy dispatch" gate: the message-catch
        // node was keyed on the commit sha (not the branch), since a branch can have many runs.
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(runId: "run_sha_match", onlyMatchKey: "sha789");
        var controller = CreateController(
            orchestration,
            eventRepository,
            eventHeader: "workflow_run",
            waitingExternalCorrelationRepository: waitingRepository);

        var body = """
            {
              "action": "completed",
              "workflow_run": {
                "status": "completed",
                "conclusion": "success",
                "head_sha": "sha789",
                "head_branch": "main"
              }
            }
            """;
        SetBody(controller, body);

        var result = await controller.GitHub(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(orchestration.ResumeExternalCommand);
        Assert.Equal("run_sha_match", orchestration.ResumeExternalCommand!.RunId);
        Assert.Equal("sha789", orchestration.ResumeExternalCommand.CorrelationKey);
    }

    [Fact]
    public async Task SlackInteractions_ValidSignature_AppliesApprovalDecision()
    {
        var orchestration = new CapturingOrchestrationService();
        const string secret = "slack-signing-secret";
        var controller = CreateController(
            orchestration, new CapturingExternalWorkflowEventRepository(), eventHeader: "", slackSigningSecret: secret,
            timeProvider: new FixedTimeProvider(SlackSignedAt));

        var json = "{\"actions\":[{\"action_id\":\"approve\",\"value\":\"apr_1:run_42\"}],\"user\":{\"username\":\"alice\"}}";
        SetSignedBody(controller, "payload=" + Uri.EscapeDataString(json), secret);

        var result = await controller.SlackInteractions(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(orchestration.ResumeCommand);
        Assert.Equal("run_42", orchestration.ResumeCommand!.RunId);
        Assert.Equal("apr_1", orchestration.ResumeCommand.ApprovalId);
        Assert.Equal("approve", orchestration.ResumeCommand.Decision);
        Assert.Contains("alice", orchestration.ResumeCommand.DecidedBy!);
    }

    [Fact]
    public async Task SlackInteractions_BadSignature_ReturnsUnauthorized()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateController(
            orchestration, new CapturingExternalWorkflowEventRepository(), eventHeader: "", slackSigningSecret: "secret",
            timeProvider: new FixedTimeProvider(SlackSignedAt));

        var bytes = Encoding.UTF8.GetBytes("payload=%7B%7D");
        var request = controller.ControllerContext.HttpContext.Request;
        request.Body = new MemoryStream(bytes);
        request.Headers["X-Slack-Signature"] = "v0=deadbeef";
        request.Headers["X-Slack-Request-Timestamp"] = "1700000000";

        var result = await controller.SlackInteractions(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(orchestration.ResumeCommand);
    }

    /// <summary>
    /// The instant SetSignedBody signs at. Slack requests are now rejected outside a five-minute
    /// window (#225), so these tests run on a clock anchored here — they exercise the signature, not
    /// the wall clock.
    /// </summary>
    private static readonly DateTimeOffset SlackSignedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    // ── Slack interaction dispatch (#225) ─────────────────────────────────────

    /// <summary>
    /// The regression that matters: interactions share Slack's callback URL, so an approval click must
    /// still reach the approval path untouched.
    /// </summary>
    [Fact]
    public async Task SlackInteractions_ApprovalAction_StillRoutesToTheApprovalPath()
    {
        var orchestration = new CapturingOrchestrationService();
        const string secret = "slack-signing-secret";
        var controller = CreateController(
            orchestration, new CapturingExternalWorkflowEventRepository(), eventHeader: "", slackSigningSecret: secret,
            interactionRepository: new StubInteractionRepository(PendingInteraction()),
            timeProvider: new FixedTimeProvider(SlackSignedAt));

        var json = "{\"actions\":[{\"action_id\":\"approve\",\"value\":\"apr_1:run_42\"}],\"user\":{\"username\":\"alice\"}}";
        SetSignedBody(controller, "payload=" + Uri.EscapeDataString(json), secret);

        var result = await controller.SlackInteractions(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(orchestration.ResumeCommand);
        Assert.Null(orchestration.AnswerCommand);
    }

    [Fact]
    public async Task SlackInteractions_ChoiceAction_AnswersTheInteractionViaSlack()
    {
        var orchestration = new CapturingOrchestrationService();
        const string secret = "slack-signing-secret";
        var controller = CreateController(
            orchestration, new CapturingExternalWorkflowEventRepository(), eventHeader: "", slackSigningSecret: secret,
            interactionRepository: new StubInteractionRepository(PendingInteraction()),
            timeProvider: new FixedTimeProvider(SlackSignedAt));

        var json = "{\"actions\":[{\"action_id\":\"interaction_choice:approve\",\"value\":\"int_1:approve\"}],\"user\":{\"username\":\"dana\"}}";
        SetSignedBody(controller, "payload=" + Uri.EscapeDataString(json), secret);

        var result = await controller.SlackInteractions(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(orchestration.ResumeCommand);
        Assert.NotNull(orchestration.AnswerCommand);
        Assert.Equal("int_1", orchestration.AnswerCommand!.InteractionId);
        Assert.Equal("approve", orchestration.AnswerCommand.Answer);
        Assert.Equal("slack", orchestration.AnswerCommand.Channel);
        Assert.Equal("slack:dana", orchestration.AnswerCommand.AnsweredBy);
    }

    /// <summary>A rejected confirmation fails the step, so it must take the reject verb (#219).</summary>
    [Fact]
    public async Task SlackInteractions_RejectOnAConfirm_UsesTheRejectVerb()
    {
        var orchestration = new CapturingOrchestrationService();
        const string secret = "slack-signing-secret";
        var confirm = PendingInteraction();
        confirm.Kind = AgentInteractionKinds.Confirm;
        var controller = CreateController(
            orchestration, new CapturingExternalWorkflowEventRepository(), eventHeader: "", slackSigningSecret: secret,
            interactionRepository: new StubInteractionRepository(confirm),
            timeProvider: new FixedTimeProvider(SlackSignedAt));

        var json = "{\"actions\":[{\"action_id\":\"interaction_choice:reject\",\"value\":\"int_1:reject\"}],\"user\":{\"username\":\"dana\"}}";
        SetSignedBody(controller, "payload=" + Uri.EscapeDataString(json), secret);

        await controller.SlackInteractions(CancellationToken.None);

        Assert.NotNull(orchestration.RejectCommand);
        Assert.Null(orchestration.AnswerCommand);
        Assert.Equal("slack", orchestration.RejectCommand!.Channel);
    }

    /// <summary>
    /// Slack renders the body to the clicker, so losing a race must read as information rather than an
    /// error. The 409 in the API contract is for API clients (#227), not for a person.
    /// </summary>
    [Fact]
    public async Task SlackInteractions_AlreadyAnswered_ShowsAFriendlyNoticeNotAnError()
    {
        var orchestration = new CapturingOrchestrationService
        {
            AnswerThrows = new InteractionNotPendingException("int_1", "answered", "ui"),
        };
        const string secret = "slack-signing-secret";
        var controller = CreateController(
            orchestration, new CapturingExternalWorkflowEventRepository(), eventHeader: "", slackSigningSecret: secret,
            interactionRepository: new StubInteractionRepository(PendingInteraction()),
            timeProvider: new FixedTimeProvider(SlackSignedAt));

        var json = "{\"actions\":[{\"action_id\":\"interaction_choice:approve\",\"value\":\"int_1:approve\"}],\"user\":{\"username\":\"dana\"}}";
        SetSignedBody(controller, "payload=" + Uri.EscapeDataString(json), secret);

        var result = await controller.SlackInteractions(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Already answered via ui", JsonSerializer.Serialize(ok.Value));
    }

    /// <summary>AC 11 / #225: a replayed Slack payload is refused once it falls outside the window.</summary>
    [Fact]
    public async Task SlackInteractions_ReplayedOutsideTheWindow_IsRejected()
    {
        var orchestration = new CapturingOrchestrationService();
        const string secret = "slack-signing-secret";
        var controller = CreateController(
            orchestration, new CapturingExternalWorkflowEventRepository(), eventHeader: "", slackSigningSecret: secret,
            timeProvider: new FixedTimeProvider(SlackSignedAt.AddHours(2)));

        var json = "{\"actions\":[{\"action_id\":\"approve\",\"value\":\"apr_1:run_42\"}],\"user\":{\"username\":\"alice\"}}";
        SetSignedBody(controller, "payload=" + Uri.EscapeDataString(json), secret);

        var result = await controller.SlackInteractions(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(orchestration.ResumeCommand);
    }

    private static AgentInteraction PendingInteraction() => new()
    {
        Id = "int_1",
        RunId = "run_42",
        FromAgent = "reviewer",
        Kind = AgentInteractionKinds.Question,
        AddresseeType = AgentInteractionAddresseeTypes.Human,
        Blocking = true,
        Prompt = "Deploy?",
        Status = AgentInteractionStatuses.Pending,
        CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
    };

    private static void SetSignedBody(WebhooksController controller, string rawBody, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(rawBody);
        const string ts = "1700000000";
        var basestring = Encoding.UTF8.GetBytes($"v0:{ts}:").Concat(bytes).ToArray();
        var hash = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), basestring);
        var request = controller.ControllerContext.HttpContext.Request;
        request.Body = new MemoryStream(bytes);
        request.ContentLength = bytes.Length;
        request.Headers["X-Slack-Signature"] = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
        request.Headers["X-Slack-Request-Timestamp"] = ts;
    }

    [Fact]
    public async Task Events_SignedEventMatchingAWaitingRun_AutoResumesIt()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(
            runId: "run_waiting_unit",
            onlyMatchKey: "build-vmodel-001:unit",
            onlyMatchMessage: "test.unit.completed");
        var controller = CreateEventsController(orchestration, eventRepository, waitingRepository);

        // The wait armed by examples/v-model-process.bpmn: messageName "test.unit.completed",
        // correlationKeyTemplate "{{input.build_id}}:unit". Neither dimension was reachable
        // through the GitHub-taxonomy ingress (#206).
        var body = """
            {
              "messageName": "test.unit.completed",
              "correlationKey": "build-vmodel-001:unit",
              "payload": {
                "conclusion": "success",
                "total": 42,
                "failed": 0,
                "report_url": "https://ci.example/runs/9/junit.xml"
              }
            }
            """;
        SetSignedEventBody(controller, body);

        var result = await controller.Events(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(orchestration.ResumeExternalCommand);
        Assert.Equal("run_waiting_unit", orchestration.ResumeExternalCommand!.RunId);
        Assert.Equal("build-vmodel-001:unit", orchestration.ResumeExternalCommand.CorrelationKey);
        Assert.Equal("event-ingress:ci", orchestration.ResumeExternalCommand.ResumedBy);
        Assert.Equal("success", orchestration.ResumeExternalCommand.Payload["conclusion"]);
        Assert.Equal("42", orchestration.ResumeExternalCommand.Payload["total"]);
        Assert.Equal(
            "https://ci.example/runs/9/junit.xml",
            orchestration.ResumeExternalCommand.Payload["report_url"]);

        var recorded = Assert.Single(eventRepository.Added);
        Assert.Equal("test.unit.completed", recorded.Kind);
        Assert.Equal("build-vmodel-001:unit", recorded.CorrelationHint);
        Assert.Equal("ci", recorded.Source);
    }

    [Fact]
    public async Task Events_RedeliveredEvent_IsRecordedOnceAndResumesOnce()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit");
        var controller = CreateEventsController(orchestration, eventRepository, waitingRepository);

        var body = """
            {
              "messageName": "test.unit.completed",
              "correlationKey": "build-vmodel-001:unit",
              "payload": { "conclusion": "success" }
            }
            """;

        SetSignedEventBody(controller, body, deliveryId: "delivery-1");
        var first = await controller.Events(CancellationToken.None);

        SetSignedEventBody(controller, body, deliveryId: "delivery-1");
        var second = await controller.Events(CancellationToken.None);

        Assert.IsType<OkObjectResult>(first);
        Assert.IsType<OkObjectResult>(second);
        Assert.Single(eventRepository.Added);
        Assert.Equal(1, orchestration.ResumeExternalCount);
    }

    [Fact]
    public async Task Events_RedeliveredEventWithoutADeliveryHeader_IsStillDeduplicatedByBodyDigest()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var waitingRepository = new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit");
        var controller = CreateEventsController(orchestration, eventRepository, waitingRepository);

        var body = """
            {
              "messageName": "test.unit.completed",
              "correlationKey": "build-vmodel-001:unit",
              "payload": { "conclusion": "success" }
            }
            """;

        SetSignedEventBody(controller, body);
        await controller.Events(CancellationToken.None);
        SetSignedEventBody(controller, body);
        await controller.Events(CancellationToken.None);

        Assert.Single(eventRepository.Added);
        Assert.Equal(1, orchestration.ResumeExternalCount);
    }

    [Fact]
    public async Task Events_WrongSignature_IsRejectedAndNotRecorded()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateEventsController(
            orchestration,
            eventRepository,
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"));

        var body = """
            { "messageName": "test.unit.completed", "correlationKey": "build-vmodel-001:unit" }
            """;
        SetBody(controller, body);
        controller.ControllerContext.HttpContext.Request.Headers["X-Agentwerke-Source"] = "ci";
        controller.ControllerContext.HttpContext.Request.Headers["X-Agentwerke-Signature-256"] =
            "sha256=0000000000000000000000000000000000000000000000000000000000000000";

        var result = await controller.Events(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(orchestration.ResumeExternalCommand);
        Assert.Empty(eventRepository.Added);
    }

    [Fact]
    public async Task Events_UnsignedRequest_IsRejected()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateEventsController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"));

        SetBody(controller, """{ "messageName": "test.unit.completed", "correlationKey": "k" }""");
        controller.ControllerContext.HttpContext.Request.Headers["X-Agentwerke-Source"] = "ci";

        var result = await controller.Events(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(orchestration.ResumeExternalCommand);
    }

    [Fact]
    public async Task Events_UnknownSource_IsRejected()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateEventsController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"));

        SetSignedEventBody(controller, """{ "messageName": "test.unit.completed", "correlationKey": "k" }""");
        controller.ControllerContext.HttpContext.Request.Headers["X-Agentwerke-Source"] = "not-registered";

        var result = await controller.Events(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(orchestration.ResumeExternalCommand);
    }

    /// <summary>
    /// The connector webhooks treat an empty secret as "skip validation" (dev convenience). This
    /// endpoint can resume a verification gate, so a misconfigured source must fail closed instead.
    /// </summary>
    [Fact]
    public async Task Events_SourceWithNoConfiguredSecret_IsRejectedRatherThanSkippingValidation()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateEventsController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"),
            sources: [new EventIngressSourceOptions { Id = "ci", Secret = string.Empty }]);

        SetBody(controller, """{ "messageName": "test.unit.completed", "correlationKey": "k" }""");
        controller.ControllerContext.HttpContext.Request.Headers["X-Agentwerke-Source"] = "ci";

        var result = await controller.Events(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(orchestration.ResumeExternalCommand);
    }

    [Fact]
    public async Task Events_MessageNameOutsideTheSourceAllowlist_IsForbidden()
    {
        var orchestration = new CapturingOrchestrationService();
        var controller = CreateEventsController(
            orchestration,
            new CapturingExternalWorkflowEventRepository(),
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"),
            sources:
            [
                new EventIngressSourceOptions
                {
                    Id = "ci",
                    Secret = IngressSecret,
                    AllowedMessageNames = ["test.unit.completed"],
                }
            ]);

        SetSignedEventBody(controller, """{ "messageName": "deploy.prod.approved", "correlationKey": "k" }""");

        var result = await controller.Events(CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
        Assert.Null(orchestration.ResumeExternalCommand);
    }

    [Fact]
    public async Task Events_WhenNoRunIsWaiting_RecordsTheEventAndReportsNotResumed()
    {
        var orchestration = new CapturingOrchestrationService();
        var eventRepository = new CapturingExternalWorkflowEventRepository();
        var controller = CreateEventsController(
            orchestration,
            eventRepository,
            new StubWaitingExternalCorrelationRepository(runId: null));

        SetSignedEventBody(controller, """
            { "messageName": "test.unit.completed", "correlationKey": "nobody-waits-on-this" }
            """);

        var result = await controller.Events(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("resumed = False", ok.Value!.ToString(), StringComparison.Ordinal);
        Assert.Null(orchestration.ResumeExternalCommand);
        Assert.Single(eventRepository.Added);
    }

    /// <summary>
    /// A form/urlencoded content type makes ASP.NET consume the body before the controller reads it,
    /// which would otherwise surface as a confusing "Signature mismatch" for a correctly signed event.
    /// </summary>
    [Fact]
    public async Task Events_EmptyBody_ReportsTheBodyRatherThanTheSignature()
    {
        var controller = CreateEventsController(
            new CapturingOrchestrationService(),
            new CapturingExternalWorkflowEventRepository(),
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"));

        SetSignedEventBody(controller, string.Empty);

        var result = await controller.Events(CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Empty request body", badRequest.Value!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_WhenIngressIsDisabled_TheEndpointIsNotFound()
    {
        var controller = CreateEventsController(
            new CapturingOrchestrationService(),
            new CapturingExternalWorkflowEventRepository(),
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"),
            enabled: false);

        SetSignedEventBody(controller, """{ "messageName": "test.unit.completed", "correlationKey": "k" }""");

        Assert.IsType<NotFoundObjectResult>(await controller.Events(CancellationToken.None));
    }

    [Theory]
    [InlineData("""{ "correlationKey": "k" }""")]
    [InlineData("""{ "messageName": "test.unit.completed" }""")]
    [InlineData("""{ "messageName": "", "correlationKey": "k" }""")]
    public async Task Events_MissingRequiredFields_IsRejected(string body)
    {
        var controller = CreateEventsController(
            new CapturingOrchestrationService(),
            new CapturingExternalWorkflowEventRepository(),
            new StubWaitingExternalCorrelationRepository(runId: "run_waiting_unit"));

        SetSignedEventBody(controller, body);

        Assert.IsType<BadRequestObjectResult>(await controller.Events(CancellationToken.None));
    }

    private const string IngressSecret = "ci-shared-secret";

    private static WebhooksController CreateEventsController(
        IWorkflowRunOrchestrationService orchestration,
        IExternalWorkflowEventRepository eventRepository,
        IWaitingExternalCorrelationRepository waitingExternalCorrelationRepository,
        IReadOnlyList<EventIngressSourceOptions>? sources = null,
        bool enabled = true)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Agentwerke-Source"] = "ci";

        return new WebhooksController(
            orchestration,
            new StubTriggerRouter(null),
            eventRepository,
            waitingExternalCorrelationRepository,
            new StubInteractionRepository(),
            new StubSecretStore(),
            TimeProvider.System,
            Options.Create(new IntegrationOptions
            {
                EventIngress = new EventIngressOptions
                {
                    Enabled = enabled,
                    Sources = [.. sources ?? [new EventIngressSourceOptions { Id = "ci", Secret = IngressSecret }]],
                },
            }),
            NullLogger<WebhooksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static void SetSignedEventBody(WebhooksController controller, string json, string? deliveryId = null)
    {
        SetBody(controller, json);

        var signature = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(IngressSecret),
                Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();

        var headers = controller.ControllerContext.HttpContext.Request.Headers;
        headers["X-Agentwerke-Signature-256"] = $"sha256={signature}";
        if (deliveryId is not null)
        {
            headers["X-Agentwerke-Delivery"] = deliveryId;
        }
    }

    private static WebhooksController CreateController(
        IWorkflowRunOrchestrationService orchestration,
        IExternalWorkflowEventRepository eventRepository,
        string eventHeader,
        ITriggerRouter? triggerRouter = null,
        IWaitingExternalCorrelationRepository? waitingExternalCorrelationRepository = null,
        string? slackSigningSecret = null,
        string requiredLabel = "agentwerke",
        IAgentInteractionRepository? interactionRepository = null,
        string? interactionWebhookSecret = null,
        TimeProvider? timeProvider = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-GitHub-Event"] = eventHeader;

        return new WebhooksController(
            orchestration,
            triggerRouter ?? new StubTriggerRouter(null),
            eventRepository,
            waitingExternalCorrelationRepository ?? new StubWaitingExternalCorrelationRepository(runId: null),
            interactionRepository ?? new StubInteractionRepository(),
            new StubSecretStore(),
            timeProvider ?? TimeProvider.System,
            Options.Create(new IntegrationOptions
            {
                GitHub = new GitHubOptions
                {
                    Enabled = true,
                    WebhookSecret = string.Empty,
                    TriggerActions = ["opened"],
                    RequiredLabel = requiredLabel,
                },
                Slack = new SlackOptions
                {
                    SigningSecret = slackSigningSecret ?? string.Empty,
                },
                InteractionWebhook = new InteractionWebhookOptions
                {
                    Enabled = interactionWebhookSecret is not null,
                    Endpoint = "https://receiver.example/interactions",
                    Secret = interactionWebhookSecret ?? string.Empty,
                },
            }),
            NullLogger<WebhooksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Returns the configured value as-is, mirroring an unresolved literal secret.</summary>
    private sealed class StubSecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(key);
    }

    private sealed class StubInteractionRepository : IAgentInteractionRepository
    {
        private readonly AgentInteraction? _interaction;

        public StubInteractionRepository(AgentInteraction? interaction = null) => _interaction = interaction;

        public Task AddAsync(AgentInteraction interaction, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentInteraction>> GetByRunAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentInteraction>>([]);
        public Task<IReadOnlyList<AgentInteraction>> GetPostsForRunAsync(string runId, string? fromFilter, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentInteraction>>([]);
        public Task<AgentInteraction?> GetByIdAsync(string interactionId, CancellationToken cancellationToken) =>
            Task.FromResult(_interaction is not null && _interaction.Id == interactionId ? _interaction : null);
        public Task<AgentInteraction?> GetPendingForRunAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<AgentInteraction?>(null);
        public Task<InteractionTransitionResult> TryTransitionAsync(string interactionId, string toStatus, string? response, string? respondedBy, string? respondedChannel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInteraction>> GetPendingAsync(string? runId, string? addresseeType, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentInteraction>>([]);
        public Task<IReadOnlyList<AgentInteraction>> GetDueForExpiryAsync(string nowIso, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentInteraction>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void SetBody(WebhooksController controller, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(bytes);
        controller.ControllerContext.HttpContext.Request.ContentLength = bytes.Length;
    }

    private sealed class CapturingOrchestrationService : IWorkflowRunOrchestrationService
    {
        public StartRunCommand? StartCommand { get; private set; }

        public ResumeExternalRunCommand? ResumeExternalCommand { get; private set; }

        /// <summary>How many times a run was resumed — the assertion that dedup (#206) actually holds.</summary>
        public int ResumeExternalCount { get; private set; }

        public ResumeRunCommand? ResumeCommand { get; private set; }

        public AnswerInteractionCommand? AnswerCommand { get; private set; }

        public RejectInteractionCommand? RejectCommand { get; private set; }

        /// <summary>Set to simulate losing a race to another channel (#219).</summary>
        public Exception? AnswerThrows { get; init; }

        public Task<StartRunResult> StartRunAsync(StartRunCommand command, CancellationToken cancellationToken = default)
        {
            StartCommand = command;
            return Task.FromResult(new StartRunResult("run_1", command.WorkflowId, "pending", null));
        }

        public Task<ResumeRunResult> ResumeRunAsync(ResumeRunCommand command, CancellationToken cancellationToken = default)
        {
            ResumeCommand = command;
            return Task.FromResult(new ResumeRunResult(command.RunId, "running", null));
        }

        public Task<ResumeExternalRunResult> ResumeExternalRunAsync(ResumeExternalRunCommand command, CancellationToken cancellationToken = default)
        {
            ResumeExternalCommand = command;
            ResumeExternalCount++;
            return Task.FromResult(new ResumeExternalRunResult(command.RunId, "pending"));
        }

        public Task<RecoverRunResult> RecoverRunAsync(string runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnswerInteractionResult> AnswerInteractionAsync(AnswerInteractionCommand command, CancellationToken cancellationToken = default)
        {
            if (AnswerThrows is not null)
            {
                throw AnswerThrows;
            }

            AnswerCommand = command;
            return Task.FromResult(
                new AnswerInteractionResult(command.RunId, command.InteractionId, "pending", command.Channel));
        }

        public Task<RejectInteractionResult> RejectInteractionAsync(RejectInteractionCommand command, CancellationToken cancellationToken = default)
        {
            RejectCommand = command;
            return Task.FromResult(
                new RejectInteractionResult(command.RunId, command.InteractionId, "pending", command.Channel));
        }

        public Task<CancelInteractionResult> CancelInteractionAsync(CancelInteractionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExpireInteractionResult> ExpireInteractionAsync(string interactionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingExternalWorkflowEventRepository : IExternalWorkflowEventRepository
    {
        public List<ExternalWorkflowEvent> Added { get; } = [];

        public Task AddAsync(ExternalWorkflowEvent @event, CancellationToken cancellationToken)
        {
            Added.Add(@event);
            return Task.CompletedTask;
        }

        /// <summary>Mirrors the real unique-index dedup on DeliveryId so redelivery tests are meaningful.</summary>
        public Task<bool> TryAddAsync(ExternalWorkflowEvent @event, CancellationToken cancellationToken)
        {
            if (@event.DeliveryId is { Length: > 0 }
                && Added.Any(e => e.DeliveryId == @event.DeliveryId))
            {
                return Task.FromResult(false);
            }

            Added.Add(@event);
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Matches any (messageName, correlationKey) lookup when constructed with a flat run id.
    /// Construct with <paramref name="onlyMatchKey"/> to simulate a run waiting on one *specific*
    /// correlation key (e.g. a commit sha) so multi-candidate lookups (#139) can be exercised.
    /// </summary>
    private sealed class StubWaitingExternalCorrelationRepository(
        string? runId,
        string? onlyMatchKey = null,
        string? onlyMatchMessage = null) : IWaitingExternalCorrelationRepository
    {
        public Task UpsertAsync(WaitingExternalCorrelation correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> FindWaitingRunIdAsync(string messageName, string correlationKey, CancellationToken cancellationToken)
        {
            var keyMatches = onlyMatchKey is null || string.Equals(onlyMatchKey, correlationKey, StringComparison.Ordinal);
            var messageMatches = onlyMatchMessage is null || string.Equals(onlyMatchMessage, messageName, StringComparison.Ordinal);
            return Task.FromResult(keyMatches && messageMatches ? runId : null);
        }

        public Task<IReadOnlyList<WaitingExternalCorrelation>> ListWaitingAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubTriggerRouter(string? workflowId) : ITriggerRouter
    {
        public Task<string?> ResolveWorkflowIdAsync(string triggerSource, CancellationToken cancellationToken) =>
            Task.FromResult(workflowId);
    }
}
