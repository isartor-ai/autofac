using Agentwerke.Domain.AgentRuntime;

namespace Agentwerke.Workflows.Bpmn;

public sealed record BpmnWorkflowDefinition(
    string ProcessId,
    string? ProcessName,
    IReadOnlyList<BpmnNodeDefinition> Nodes,
    IReadOnlyList<BpmnSequenceFlow>? SequenceFlows = null)
{
    /// <summary>
    /// Walks backward in document order from <paramref name="fromNodeId"/> to find the
    /// nearest preceding agent-driven service task — used to attribute a userTask approval
    /// gate (agent name, produced artifact) to the step that triggered it.
    /// </summary>
    public BpmnNodeDefinition? FindPrecedingServiceTaskNode(string fromNodeId)
    {
        var fromIndex = -1;
        for (var i = 0; i < Nodes.Count; i++)
        {
            if (string.Equals(Nodes[i].Id, fromNodeId, StringComparison.Ordinal))
            {
                fromIndex = i;
                break;
            }
        }

        for (var i = fromIndex - 1; i >= 0; i--)
        {
            var node = Nodes[i];
            if ((node.ElementName is "serviceTask" or "scriptTask") && node.Metadata?.Agent is not null)
            {
                return node;
            }
        }

        return null;
    }
}

public sealed record BpmnSequenceFlow(
    string Id,
    string SourceRef,
    string TargetRef,
    string? ConditionExpression = null);

public sealed record BpmnNodeDefinition(
    string Id,
    string? Name,
    string ElementName,
    AgentwerkeTaskMetadata? Metadata,
    AgentwerkeApprovalMetadata? ApprovalMetadata = null,
    string? TimerDuration = null,
    AgentwerkeExternalEventMetadata? ExternalEventMetadata = null,
    /// <summary>
    /// For boundaryEvent nodes: the id of the activity this event is attached to
    /// (BPMN <c>attachedToRef</c>). Null for the legacy convention where a boundary event
    /// is modelled as the sequence-flow successor of the activity it guards.
    /// </summary>
    string? AttachedToRef = null,
    /// <summary>
    /// For boundaryEvent nodes: whether firing interrupts the activity it is attached to
    /// (BPMN <c>cancelActivity</c>, default true).
    /// </summary>
    bool CancelActivity = true);

public sealed record AgentwerkeTaskMetadata(
    string Agent,
    string Action,
    string? Environment,
    string PurposeType,
    string PolicyTag,
    IReadOnlyList<string> RequiresEvidence,
    int MaxRetries = 0,
    int RetryBackoffSeconds = 0,
    int FailUntilAttempt = 0,
    bool SimulateTimeout = false,
    int? TimeoutSeconds = null,
    AgentRuntimeContract? RuntimeContract = null,
    string? ExecutionMode = null,
    /// <summary>
    /// Named sandbox profile (e.g. "repo-write", "deployment") this task requests. Null
    /// defaults to the most restrictive "offline" profile. Validated against the agent's
    /// declared allow-list and policy risk level before the sandbox is created.
    /// </summary>
    string? SandboxProfile = null);

public sealed record AgentwerkeApprovalMetadata(
    string PurposeType,
    string PolicyTag);

public sealed record AgentwerkeExternalEventMetadata(
    string MessageName,
    string CorrelationKeyTemplate);
