using Agentwerke.Domain.Persistence;

namespace Agentwerke.Application.Workflows;

/// <summary>
/// The built-in SDLC template catalog. All templates use only the Agentwerke runtime's governed
/// BPMN subset, so they run without Camunda and validate cleanly out of the box.
/// </summary>
public static class SdlcTemplateSeeds
{
    public static readonly SdlcTemplate IssueToPr = new()
    {
        Id = "issue-to-pr",
        Name = "Issue to Pull Request",
        Description = "Full specification, planning, implementation, and code-review cycle that ends by opening a pull request.",
        Trigger = "manual",
        RequiredInputs = ["issue_url", "repository"],
        AgentRoles = ["specification-agent", "planning-agent", "implementation-agent", "github-agent"],
        ApprovalRoles = ["developer"],
        EvidenceExpectations = ["spec_document", "implementation_plan", "code_changes"],
        PolicyLevel = "standard",
        Tags = ["sdlc", "feature", "github"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="IssueToPr" name="Issue to PR">
                <bpmn:startEvent id="Start" name="Issue Received" />
                <bpmn:serviceTask id="Specify" name="Specify Requirements">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="specification-agent"
                      action="spec.generate"
                      purposeType="specification"
                      policyTag="sdlc-spec" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:serviceTask id="Plan" name="Plan Implementation">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="planning-agent"
                      action="plan.generate"
                      purposeType="planning"
                      policyTag="sdlc-plan" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:serviceTask id="Implement" name="Implement Changes">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="implementation-agent"
                      action="code.generate"
                      purposeType="implementation"
                      policyTag="repo-change" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:userTask id="CodeReview" name="Code Review Approval">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="code_review"
                      policyTag="human-code-review" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:serviceTask id="OpenPR" name="Open Pull Request">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="github-agent"
                      action="github.open_pr"
                      purposeType="pull_request"
                      policyTag="repo-write" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:endEvent id="End" name="PR Opened" />
                <bpmn:sequenceFlow id="sf1" sourceRef="Start" targetRef="Specify" />
                <bpmn:sequenceFlow id="sf2" sourceRef="Specify" targetRef="Plan" />
                <bpmn:sequenceFlow id="sf3" sourceRef="Plan" targetRef="Implement" />
                <bpmn:sequenceFlow id="sf4" sourceRef="Implement" targetRef="CodeReview" />
                <bpmn:sequenceFlow id="sf5" sourceRef="CodeReview" targetRef="OpenPR" />
                <bpmn:sequenceFlow id="sf6" sourceRef="OpenPR" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    public static readonly SdlcTemplate Bugfix = new()
    {
        Id = "bugfix",
        Name = "Bugfix",
        Description = "Root-cause diagnosis with configured retries, fix implementation, and test/merge approval gate.",
        Trigger = "manual",
        RequiredInputs = ["bug_report_url", "repository"],
        AgentRoles = ["analysis-agent", "implementation-agent"],
        ApprovalRoles = ["developer"],
        EvidenceExpectations = ["diagnosis_report", "fix_diff"],
        PolicyLevel = "standard",
        Tags = ["sdlc", "bugfix"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="Bugfix" name="Bugfix">
                <bpmn:startEvent id="Start" name="Bug Reported" />
                <bpmn:serviceTask id="Diagnose" name="Diagnose Root Cause">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="analysis-agent"
                      action="bug.diagnose"
                      purposeType="diagnosis"
                      policyTag="read-only"
                      maxRetries="2"
                      retryBackoffSeconds="5" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:serviceTask id="Fix" name="Implement Fix">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="implementation-agent"
                      action="code.fix"
                      purposeType="bugfix"
                      policyTag="repo-change" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:userTask id="TestApproval" name="Test and Merge Approval">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="bugfix_merge"
                      policyTag="human-merge-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:endEvent id="End" name="Fix Merged" />
                <bpmn:sequenceFlow id="sf1" sourceRef="Start" targetRef="Diagnose" />
                <bpmn:sequenceFlow id="sf2" sourceRef="Diagnose" targetRef="Fix" />
                <bpmn:sequenceFlow id="sf3" sourceRef="Fix" targetRef="TestApproval" />
                <bpmn:sequenceFlow id="sf4" sourceRef="TestApproval" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    public static readonly SdlcTemplate Hotfix = new()
    {
        Id = "hotfix",
        Name = "Hotfix (Emergency)",
        Description = "Expedited emergency fix flow with a mandatory human approval gate before deploying directly to production.",
        Trigger = "manual",
        RequiredInputs = ["incident_url", "repository"],
        AgentRoles = ["implementation-agent", "deployment-agent"],
        ApprovalRoles = ["on-call-engineer"],
        EvidenceExpectations = ["fix_diff", "human_approval"],
        PolicyLevel = "critical",
        Tags = ["sdlc", "hotfix", "incident"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="Hotfix" name="Hotfix (Emergency)">
                <bpmn:startEvent id="Start" name="Incident Declared" />
                <bpmn:serviceTask id="EmergencyFix" name="Implement Emergency Fix">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="implementation-agent"
                      action="code.fix"
                      purposeType="hotfix"
                      policyTag="repo-change" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:userTask id="ExpeditedApproval" name="Expedited Approval">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="emergency_deployment"
                      policyTag="human-emergency-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:serviceTask id="EmergencyDeploy" name="Deploy Emergency Fix">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="deployment-agent"
                      action="cloud.deploy"
                      purposeType="hotfix_deployment"
                      policyTag="production-write"
                      requiresEvidence="fix_diff,human_approval" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:endEvent id="End" name="Incident Resolved" />
                <bpmn:sequenceFlow id="sf1" sourceRef="Start" targetRef="EmergencyFix" />
                <bpmn:sequenceFlow id="sf2" sourceRef="EmergencyFix" targetRef="ExpeditedApproval" />
                <bpmn:sequenceFlow id="sf3" sourceRef="ExpeditedApproval" targetRef="EmergencyDeploy" />
                <bpmn:sequenceFlow id="sf4" sourceRef="EmergencyDeploy" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    public static readonly SdlcTemplate DeploymentApproval = new()
    {
        Id = "deployment-approval",
        Name = "Parallel Build, Test and Deploy",
        Description = "Parallel quality gate (tests + security scan) followed by a deploy-approval gate and production deployment.",
        Trigger = "manual",
        RequiredInputs = ["build_artifact", "target_environment"],
        AgentRoles = ["testing-agent", "security-agent", "deployment-agent"],
        ApprovalRoles = ["release-manager"],
        EvidenceExpectations = ["tests_passed", "security_cleared", "human_approval"],
        PolicyLevel = "elevated",
        Tags = ["sdlc", "deployment", "quality-gate"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="ParallelBuildAndTest" name="Parallel Build and Test">
                <bpmn:startEvent id="Start" name="Build Triggered" />
                <bpmn:parallelGateway id="Fork" name="Quality Gate Fork" />
                <bpmn:serviceTask id="RunTests" name="Run Test Suite">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="testing-agent"
                      action="tests.run"
                      purposeType="quality_assurance"
                      policyTag="read-only" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:serviceTask id="SecurityScan" name="Run Security Scan">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="security-agent"
                      action="security.scan"
                      purposeType="security_review"
                      policyTag="read-only" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:parallelGateway id="Join" name="Quality Gate Join" />
                <bpmn:userTask id="DeployApproval" name="Deploy Approval">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="production_deployment"
                      policyTag="human-deploy-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:serviceTask id="Deploy" name="Deploy to Production">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="deployment-agent"
                      action="cloud.deploy"
                      purposeType="production_deployment"
                      policyTag="production-write"
                      requiresEvidence="tests_passed,security_cleared,human_approval" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:endEvent id="End" name="Deployed" />
                <bpmn:sequenceFlow id="sf1" sourceRef="Start" targetRef="Fork" />
                <bpmn:sequenceFlow id="sf2" sourceRef="Fork" targetRef="RunTests" />
                <bpmn:sequenceFlow id="sf3" sourceRef="Fork" targetRef="SecurityScan" />
                <bpmn:sequenceFlow id="sf4" sourceRef="RunTests" targetRef="Join" />
                <bpmn:sequenceFlow id="sf5" sourceRef="SecurityScan" targetRef="Join" />
                <bpmn:sequenceFlow id="sf6" sourceRef="Join" targetRef="DeployApproval" />
                <bpmn:sequenceFlow id="sf7" sourceRef="DeployApproval" targetRef="Deploy" />
                <bpmn:sequenceFlow id="sf8" sourceRef="Deploy" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    public static readonly SdlcTemplate SecurityReview = new()
    {
        Id = "security-review",
        Name = "Security Review",
        Description = "Automated security scan with a timer-gated report wait, conditional remediation branch, and sign-off approval.",
        Trigger = "manual",
        RequiredInputs = ["repository", "scan_scope"],
        AgentRoles = ["security-agent"],
        ApprovalRoles = ["security-engineer"],
        EvidenceExpectations = ["scan_report", "remediation_evidence"],
        PolicyLevel = "elevated",
        Tags = ["sdlc", "security", "compliance"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="SecurityReview" name="Security Review">
                <bpmn:startEvent id="Start" name="Review Requested" />
                <bpmn:serviceTask id="Scan" name="Run Security Scan">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="security-agent"
                      action="security.scan"
                      purposeType="security_review"
                      policyTag="read-only" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:intermediateCatchEvent id="WaitForReport" name="Wait for Scan Report">
                  <bpmn:timerEventDefinition>
                    <bpmn:timeDuration>PT30S</bpmn:timeDuration>
                  </bpmn:timerEventDefinition>
                </bpmn:intermediateCatchEvent>
                <bpmn:exclusiveGateway id="SeverityGate" name="Findings Severity Gate" />
                <bpmn:serviceTask id="Remediate" name="Remediate Findings">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="security-agent"
                      action="security.remediate"
                      purposeType="remediation"
                      policyTag="repo-change" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:userTask id="VerifyApproval" name="Security Sign-Off">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="security_sign_off"
                      policyTag="human-security-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:endEvent id="End" name="Security Cleared" />
                <bpmn:sequenceFlow id="sf1" sourceRef="Start" targetRef="Scan" />
                <bpmn:sequenceFlow id="sf2" sourceRef="Scan" targetRef="WaitForReport" />
                <bpmn:sequenceFlow id="sf3" sourceRef="WaitForReport" targetRef="SeverityGate" />
                <bpmn:sequenceFlow id="sf4" sourceRef="SeverityGate" targetRef="Remediate">
                  <bpmn:conditionExpression>true</bpmn:conditionExpression>
                </bpmn:sequenceFlow>
                <bpmn:sequenceFlow id="sf5" sourceRef="SeverityGate" targetRef="VerifyApproval" />
                <bpmn:sequenceFlow id="sf6" sourceRef="Remediate" targetRef="VerifyApproval" />
                <bpmn:sequenceFlow id="sf7" sourceRef="VerifyApproval" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    public static readonly SdlcTemplate ReleaseApproval = new()
    {
        Id = "release-approval",
        Name = "Release Approval",
        Description = "Packages a release, generates release notes, and gates deployment behind dual QA and executive approval.",
        Trigger = "manual",
        RequiredInputs = ["version_tag", "repository", "changelog"],
        AgentRoles = ["build-agent", "documentation-agent", "deployment-agent"],
        ApprovalRoles = ["qa-lead", "executive-sponsor"],
        EvidenceExpectations = ["tests_passed", "security_cleared", "release_notes_generated"],
        PolicyLevel = "critical",
        Tags = ["sdlc", "release", "compliance"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="ReleaseApproval" name="Release Approval">
                <bpmn:startEvent id="Start" name="Release Initiated" />
                <bpmn:serviceTask id="Package" name="Package Release Artifact">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="build-agent"
                      action="build.package"
                      purposeType="release_packaging"
                      policyTag="build-write" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:serviceTask id="Notes" name="Generate Release Notes">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="documentation-agent"
                      action="docs.release_notes"
                      purposeType="documentation"
                      policyTag="read-only" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:userTask id="QAApproval" name="QA Sign-Off">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="qa_sign_off"
                      policyTag="human-qa-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:userTask id="ExecApproval" name="Executive Approval">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="executive_sign_off"
                      policyTag="human-exec-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:serviceTask id="DeployRelease" name="Deploy Release">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="deployment-agent"
                      action="cloud.deploy"
                      purposeType="release_deployment"
                      policyTag="production-write"
                      requiresEvidence="tests_passed,security_cleared,release_notes_generated" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:endEvent id="End" name="Release Deployed" />
                <bpmn:sequenceFlow id="sf1" sourceRef="Start" targetRef="Package" />
                <bpmn:sequenceFlow id="sf2" sourceRef="Package" targetRef="Notes" />
                <bpmn:sequenceFlow id="sf3" sourceRef="Notes" targetRef="QAApproval" />
                <bpmn:sequenceFlow id="sf4" sourceRef="QAApproval" targetRef="ExecApproval" />
                <bpmn:sequenceFlow id="sf5" sourceRef="ExecApproval" targetRef="DeployRelease" />
                <bpmn:sequenceFlow id="sf6" sourceRef="DeployRelease" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    /// <summary>
    /// The full autonomous SDLC scenario from issue #89: every phase B–E3 sub-issue (#134–#140)
    /// wired into one workflow, end to end — two human approval gates, two real GitHub webhook
    /// waits (PR merge, CI green), and three agent_sandboxed code-writing stages. See
    /// docs/manual-test-sdlc-e2e.md for how to walk a run through this template by hand.
    /// </summary>
    public static readonly SdlcTemplate AutonomousSdlc = new()
    {
        Id = "autonomous-sdlc",
        Name = "Autonomous SDLC: Issue to Deployed and Tested",
        Description = "Requirement design and architecture design (each with a human approval gate), technical analysis, sandboxed implementation and senior review, a wait for the resulting PR to merge, a CI/CD deploy trigger with a wait for CI to go green, and a final test run — the complete BA-to-Tester pipeline from issue #89.",
        Trigger = "manual",
        RequiredInputs = ["issue_url", "repository", "branch_name"],
        AgentRoles =
        [
            "business-analyst",
            "solution-architect",
            "technical-analyst",
            "implementation-engineer",
            "senior-code-reviewer",
            "deploy-agent",
            "tester"
        ],
        ApprovalRoles = ["product-owner", "tech-lead"],
        EvidenceExpectations =
        [
            "requirements_spec",
            "architecture_spec",
            "implementation_plan",
            "pull_request_opened",
            "code_review_approved",
            "pr_merged",
            "ci_green",
            "tests_passed"
        ],
        PolicyLevel = "elevated",
        Tags = ["sdlc", "autonomous", "end-to-end", "github"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="AutonomousSdlc" name="Autonomous SDLC">
                <bpmn:startEvent id="Start" name="Issue Filed" />
                <bpmn:serviceTask id="RequirementDesign" name="Design Requirements">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="business-analyst"
                      action="requirement-design"
                      environment="sdlc"
                      purposeType="requirement_design"
                      policyTag="requirement-design" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:userTask id="RequirementApproval" name="Requirements Approval">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="requirement_design"
                      policyTag="human-requirement-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:serviceTask id="ArchitectureDesign" name="Design Architecture">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="solution-architect"
                      action="architecture-design"
                      environment="sdlc"
                      purposeType="architecture_design"
                      policyTag="architecture-design" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:userTask id="ArchitectureApproval" name="Architecture Approval">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="architecture_design"
                      policyTag="human-architecture-approval" />
                  </bpmn:extensionElements>
                </bpmn:userTask>
                <bpmn:serviceTask id="TechnicalAnalysis" name="Technical Analysis">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="technical-analyst"
                      action="technical-analysis"
                      environment="sdlc"
                      purposeType="technical_analysis"
                      policyTag="implementation-plan" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:serviceTask id="Implementation" name="Implement">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="implementation-engineer"
                      action="implement"
                      environment="sandbox"
                      purposeType="implementation"
                      policyTag="implementation"
                      executionMode="agent_sandboxed"
                      sandboxProfile="repo-write" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:serviceTask id="SeniorReview" name="Senior Code Review">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="senior-code-reviewer"
                      action="review-code"
                      environment="sandbox"
                      purposeType="code_review"
                      policyTag="code-review"
                      executionMode="agent_sandboxed"
                      sandboxProfile="repo-read" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:intermediateCatchEvent id="WaitForMerge" name="Wait for PR Merge">
                  <bpmn:extensionElements>
                    <agentwerke:externalEvent
                      messageName="github.pull_request.merged"
                      correlationKeyTemplate="{{input.branch_name}}" />
                  </bpmn:extensionElements>
                  <bpmn:messageEventDefinition />
                </bpmn:intermediateCatchEvent>
                <bpmn:serviceTask id="TriggerDeploy" name="Trigger Deploy">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="deploy-agent"
                      action="cicd.trigger_deploy"
                      environment="ci"
                      purposeType="cicd_deployment"
                      policyTag="deploy-staging" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:intermediateCatchEvent id="WaitForCiGreen" name="Wait for CI Green">
                  <bpmn:extensionElements>
                    <agentwerke:externalEvent
                      messageName="github.workflow_run.completed"
                      correlationKeyTemplate="{{input.branch_name}}" />
                  </bpmn:extensionElements>
                  <bpmn:messageEventDefinition />
                </bpmn:intermediateCatchEvent>
                <bpmn:serviceTask id="Test" name="Run Tests">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="tester"
                      action="run-tests"
                      environment="sandbox"
                      purposeType="test_execution"
                      policyTag="test-gate"
                      executionMode="agent_sandboxed"
                      sandboxProfile="repo-read" />
                  </bpmn:extensionElements>
                </bpmn:serviceTask>
                <bpmn:endEvent id="End" name="Done" />
                <bpmn:sequenceFlow id="sf1" sourceRef="Start" targetRef="RequirementDesign" />
                <bpmn:sequenceFlow id="sf2" sourceRef="RequirementDesign" targetRef="RequirementApproval" />
                <bpmn:sequenceFlow id="sf3" sourceRef="RequirementApproval" targetRef="ArchitectureDesign" />
                <bpmn:sequenceFlow id="sf4" sourceRef="ArchitectureDesign" targetRef="ArchitectureApproval" />
                <bpmn:sequenceFlow id="sf5" sourceRef="ArchitectureApproval" targetRef="TechnicalAnalysis" />
                <bpmn:sequenceFlow id="sf6" sourceRef="TechnicalAnalysis" targetRef="Implementation" />
                <bpmn:sequenceFlow id="sf7" sourceRef="Implementation" targetRef="SeniorReview" />
                <bpmn:sequenceFlow id="sf8" sourceRef="SeniorReview" targetRef="WaitForMerge" />
                <bpmn:sequenceFlow id="sf9" sourceRef="WaitForMerge" targetRef="TriggerDeploy" />
                <bpmn:sequenceFlow id="sf10" sourceRef="TriggerDeploy" targetRef="WaitForCiGreen" />
                <bpmn:sequenceFlow id="sf11" sourceRef="WaitForCiGreen" targetRef="Test" />
                <bpmn:sequenceFlow id="sf12" sourceRef="Test" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    public static readonly SdlcTemplate VModel = new()
    {
        Id = "v-model",
        Name = "V-model Verification & Validation",
        Description =
            "Decomposition phases (requirements, architecture, component design) mirrored by "
            + "verification phases (unit, integration, system, acceptance) with explicit traceability "
            + "gates and external test-result waits. Two human gates: requirements baseline and "
            + "acceptance sign-off.",
        Trigger = "manual",
        RequiredInputs = ["change_id", "build_id", "repository"],
        AgentRoles = ["analyst", "architect", "developer", "tester"],
        ApprovalRoles = ["requirements-owner", "release-manager"],
        EvidenceExpectations =
        [
            "requirements_baseline",
            "architecture_baseline",
            "component_design_baseline",
            "unit_test_results",
            "integration_test_results",
            "system_test_results",
            "acceptance_signoff",
        ],
        PolicyLevel = "elevated",
        Tags = ["sdlc", "v-model", "verification", "traceability"],
        BpmnXml = """
            <bpmn:definitions
                xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                xmlns:agentwerke="https://agentwerke.de/bpmn/extensions/v1">
              <bpmn:process id="VModelProcessEvaluation" name="V-model Process Evaluation" isExecutable="true">
                <bpmn:startEvent id="Start" name="Change Request">
                  <bpmn:outgoing>Flow_Start_DraftRequirements</bpmn:outgoing>
                </bpmn:startEvent>

                <bpmn:serviceTask id="DraftRequirements" name="Requirements Analysis">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="analyst"
                      action="vmodel.requirements.draft"
                      environment="github"
                      purposeType="requirements_analysis"
                      policyTag="vmodel-requirements"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="github.read_issue,artifact.read">
                      <agentwerke:metadata key="phase" value="requirements" />
                      <agentwerke:metadata key="traceability.produces" value="requirements_baseline" />
                      <agentwerke:prompt><![CDATA[
            Draft the V-model requirements baseline for change {{input.change_id}}.

            Include:
            - functional requirements
            - non-functional requirements
            - acceptance criteria
            - requirement IDs suitable for traceability

            Return Markdown with a "## Requirements Baseline" heading.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_Start_DraftRequirements</bpmn:incoming>
                  <bpmn:outgoing>Flow_DraftRequirements_ApproveRequirementsBaseline</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:userTask id="ApproveRequirementsBaseline" name="Approve Requirements Baseline">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="requirements_baseline"
                      policyTag="vmodel-requirements-approval" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_DraftRequirements_ApproveRequirementsBaseline</bpmn:incoming>
                  <bpmn:outgoing>Flow_ApproveRequirementsBaseline_DraftSystemArchitecture</bpmn:outgoing>
                </bpmn:userTask>

                <bpmn:serviceTask id="DraftSystemArchitecture" name="System Architecture Design">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="architect"
                      action="vmodel.architecture.draft"
                      environment="github"
                      purposeType="system_architecture"
                      policyTag="vmodel-architecture"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read">
                      <agentwerke:metadata key="phase" value="system_architecture" />
                      <agentwerke:metadata key="traceability.consumes" value="requirements_baseline" />
                      <agentwerke:metadata key="traceability.produces" value="architecture_baseline" />
                      <agentwerke:prompt><![CDATA[
            Create the system architecture for change {{input.change_id}}.

            Use the approved requirements:
            {{output.DraftRequirements}}

            Map architecture decisions back to requirement IDs and identify the system tests
            that should verify each architectural requirement.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_ApproveRequirementsBaseline_DraftSystemArchitecture</bpmn:incoming>
                  <bpmn:outgoing>Flow_DraftSystemArchitecture_DraftComponentDesign</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:serviceTask id="DraftComponentDesign" name="Component Design">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="architect"
                      action="vmodel.component_design.draft"
                      environment="github"
                      purposeType="component_design"
                      policyTag="vmodel-component-design"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read">
                      <agentwerke:metadata key="phase" value="component_design" />
                      <agentwerke:metadata key="traceability.consumes" value="architecture_baseline" />
                      <agentwerke:metadata key="traceability.produces" value="component_design_baseline" />
                      <agentwerke:prompt><![CDATA[
            Decompose the system architecture into component designs.

            Architecture:
            {{output.DraftSystemArchitecture}}

            For each component, list design obligations and the unit/integration tests that
            will verify them.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_DraftSystemArchitecture_DraftComponentDesign</bpmn:incoming>
                  <bpmn:outgoing>Flow_DraftComponentDesign_ImplementComponents</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:serviceTask id="ImplementComponents" name="Implementation">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="developer"
                      action="vmodel.implementation.build"
                      environment="sandbox"
                      purposeType="implementation"
                      policyTag="vmodel-implementation"
                      executionMode="agent_sandboxed"
                      sandboxProfile="repo-write"
                      permissionLevel="read-write"
                      allowedTools="sandbox.git,sandbox.file_read,sandbox.file_write,sandbox.file_edit,sandbox.shell"
                      requiresEvidence="requirements_baseline,architecture_baseline,component_design_baseline">
                      <agentwerke:metadata key="phase" value="implementation" />
                      <agentwerke:prompt><![CDATA[
            Implement the approved component design for change {{input.change_id}}.

            Requirements:
            {{output.DraftRequirements}}

            Architecture:
            {{output.DraftSystemArchitecture}}

            Component design:
            {{output.DraftComponentDesign}}

            Return the branch name, changed files, and local validation commands executed.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_DraftComponentDesign_ImplementComponents</bpmn:incoming>
                  <bpmn:outgoing>Flow_ImplementComponents_GenerateUnitTestPlan</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:serviceTask id="GenerateUnitTestPlan" name="Generate Unit Test Plan">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="tester"
                      action="vmodel.tests.unit.generate"
                      environment="sandbox"
                      purposeType="unit_verification"
                      policyTag="vmodel-unit-tests"
                      executionMode="agent_sandboxed"
                      sandboxProfile="repo-read"
                      permissionLevel="read-only"
                      allowedTools="sandbox.file_read,sandbox.shell"
                      requiresEvidence="component_design_baseline">
                      <agentwerke:metadata key="phase" value="unit_test" />
                      <agentwerke:metadata key="traceability.verifies" value="component_design_baseline" />
                      <agentwerke:prompt><![CDATA[
            Generate the unit-test plan for the implemented components.

            Implementation summary:
            {{output.ImplementComponents}}

            Map each unit test back to component design obligations.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_ImplementComponents_GenerateUnitTestPlan</bpmn:incoming>
                  <bpmn:outgoing>Flow_GenerateUnitTestPlan_WaitUnitTestResults</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:intermediateCatchEvent id="WaitUnitTestResults" name="Wait Unit Test Results">
                  <bpmn:extensionElements>
                    <agentwerke:externalEvent
                      messageName="test.unit.completed"
                      correlationKeyTemplate="{{input.build_id}}:unit" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_GenerateUnitTestPlan_WaitUnitTestResults</bpmn:incoming>
                  <bpmn:outgoing>Flow_WaitUnitTestResults_ValidateComponentTraceability</bpmn:outgoing>
                  <bpmn:messageEventDefinition />
                </bpmn:intermediateCatchEvent>

                <bpmn:boundaryEvent id="UnitTestResultsTimeout" name="Unit Tests Timed Out"
                                    attachedToRef="WaitUnitTestResults" cancelActivity="true">
                  <bpmn:outgoing>Flow_UnitTestResultsTimeout_EscalateVerificationTimeout</bpmn:outgoing>
                  <bpmn:timerEventDefinition>
                    <bpmn:timeDuration>PT2H</bpmn:timeDuration>
                  </bpmn:timerEventDefinition>
                </bpmn:boundaryEvent>

                <bpmn:serviceTask id="ValidateComponentTraceability" name="Component Traceability Gate">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="tester"
                      action="vmodel.traceability.component_gate"
                      environment="sandbox"
                      purposeType="traceability_gate"
                      policyTag="vmodel-component-traceability"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read"
                      requiresEvidence="component_design_baseline,unit_test_results">
                      <agentwerke:metadata key="phase" value="component_traceability" />
                      <agentwerke:metadata key="traceability.connects" value="component_design_baseline:unit_test_results" />
                      <agentwerke:prompt><![CDATA[
            Check that every component design obligation has a matching passing unit test.

            Unit test event:
            {{event.payload}}
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_WaitUnitTestResults_ValidateComponentTraceability</bpmn:incoming>
                  <bpmn:outgoing>Flow_ValidateComponentTraceability_GenerateIntegrationTestPlan</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:serviceTask id="GenerateIntegrationTestPlan" name="Generate Integration Test Plan">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="tester"
                      action="vmodel.tests.integration.generate"
                      environment="sandbox"
                      purposeType="integration_verification"
                      policyTag="vmodel-integration-tests"
                      executionMode="agent_sandboxed"
                      sandboxProfile="repo-read"
                      permissionLevel="read-only"
                      allowedTools="sandbox.file_read,sandbox.shell"
                      requiresEvidence="architecture_baseline,component_design_baseline,unit_test_results">
                      <agentwerke:metadata key="phase" value="integration_test" />
                      <agentwerke:metadata key="traceability.verifies" value="component_interfaces" />
                      <agentwerke:prompt><![CDATA[
            Generate integration tests for component interfaces and data contracts.

            Component traceability gate:
            {{output.ValidateComponentTraceability}}
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_ValidateComponentTraceability_GenerateIntegrationTestPlan</bpmn:incoming>
                  <bpmn:outgoing>Flow_GenerateIntegrationTestPlan_WaitIntegrationTestResults</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:intermediateCatchEvent id="WaitIntegrationTestResults" name="Wait Integration Test Results">
                  <bpmn:extensionElements>
                    <agentwerke:externalEvent
                      messageName="test.integration.completed"
                      correlationKeyTemplate="{{input.build_id}}:integration" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_GenerateIntegrationTestPlan_WaitIntegrationTestResults</bpmn:incoming>
                  <bpmn:outgoing>Flow_WaitIntegrationTestResults_ValidateArchitectureTraceability</bpmn:outgoing>
                  <bpmn:messageEventDefinition />
                </bpmn:intermediateCatchEvent>

                <bpmn:boundaryEvent id="IntegrationTestResultsTimeout" name="Integration Tests Timed Out"
                                    attachedToRef="WaitIntegrationTestResults" cancelActivity="true">
                  <bpmn:outgoing>Flow_IntegrationTestResultsTimeout_EscalateVerificationTimeout</bpmn:outgoing>
                  <bpmn:timerEventDefinition>
                    <bpmn:timeDuration>PT4H</bpmn:timeDuration>
                  </bpmn:timerEventDefinition>
                </bpmn:boundaryEvent>

                <bpmn:serviceTask id="ValidateArchitectureTraceability" name="Architecture Traceability Gate">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="architect"
                      action="vmodel.traceability.architecture_gate"
                      environment="sandbox"
                      purposeType="traceability_gate"
                      policyTag="vmodel-architecture-traceability"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read"
                      requiresEvidence="architecture_baseline,integration_test_results">
                      <agentwerke:metadata key="phase" value="architecture_traceability" />
                      <agentwerke:metadata key="traceability.connects" value="architecture_baseline:integration_test_results" />
                      <agentwerke:prompt><![CDATA[
            Check that integration test results verify the architecture and component interfaces.

            Integration test event:
            {{event.payload}}
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_WaitIntegrationTestResults_ValidateArchitectureTraceability</bpmn:incoming>
                  <bpmn:outgoing>Flow_ValidateArchitectureTraceability_AnalyzeSystemTestResults</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:serviceTask id="AnalyzeSystemTestResults" name="Analyze System Test Scope">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="tester"
                      action="vmodel.tests.system.analyze"
                      environment="sandbox"
                      purposeType="system_verification"
                      policyTag="vmodel-system-tests"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read"
                      requiresEvidence="requirements_baseline,architecture_baseline,integration_test_results">
                      <agentwerke:metadata key="phase" value="system_test" />
                      <agentwerke:metadata key="traceability.verifies" value="system_requirements" />
                      <agentwerke:prompt><![CDATA[
            Prepare the system-test execution checklist from requirements and architecture.

            Architecture traceability:
            {{output.ValidateArchitectureTraceability}}
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_ValidateArchitectureTraceability_AnalyzeSystemTestResults</bpmn:incoming>
                  <bpmn:outgoing>Flow_AnalyzeSystemTestResults_WaitSystemTestResults</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:intermediateCatchEvent id="WaitSystemTestResults" name="Wait System Test Results">
                  <bpmn:extensionElements>
                    <agentwerke:externalEvent
                      messageName="test.system.completed"
                      correlationKeyTemplate="{{input.build_id}}:system" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_AnalyzeSystemTestResults_WaitSystemTestResults</bpmn:incoming>
                  <bpmn:outgoing>Flow_WaitSystemTestResults_SummarizeVerificationFailures</bpmn:outgoing>
                  <bpmn:messageEventDefinition />
                </bpmn:intermediateCatchEvent>

                <bpmn:boundaryEvent id="SystemTestResultsTimeout" name="System Tests Timed Out"
                                    attachedToRef="WaitSystemTestResults" cancelActivity="true">
                  <bpmn:outgoing>Flow_SystemTestResultsTimeout_EscalateVerificationTimeout</bpmn:outgoing>
                  <bpmn:timerEventDefinition>
                    <bpmn:timeDuration>PT8H</bpmn:timeDuration>
                  </bpmn:timerEventDefinition>
                </bpmn:boundaryEvent>

                <bpmn:serviceTask id="SummarizeVerificationFailures" name="Summarize Verification Failures">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="tester"
                      action="vmodel.failures.summarize"
                      environment="sandbox"
                      purposeType="verification_summary"
                      policyTag="vmodel-failure-summary"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read"
                      requiresEvidence="unit_test_results,integration_test_results,system_test_results">
                      <agentwerke:metadata key="phase" value="failure_summary" />
                      <agentwerke:prompt><![CDATA[
            Summarize failed or flaky verification results across unit, integration, and system tests.

            System test event:
            {{event.payload}}

            Return a concise remediation list and trace failed tests back to requirements.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_WaitSystemTestResults_SummarizeVerificationFailures</bpmn:incoming>
                  <bpmn:outgoing>Flow_SummarizeVerificationFailures_PrepareAcceptanceTest</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:serviceTask id="PrepareAcceptanceTest" name="Prepare Acceptance Test">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="analyst"
                      action="vmodel.tests.acceptance.prepare"
                      environment="github"
                      purposeType="acceptance_verification"
                      policyTag="vmodel-acceptance-tests"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read"
                      requiresEvidence="requirements_baseline,system_test_results">
                      <agentwerke:metadata key="phase" value="acceptance_test" />
                      <agentwerke:metadata key="traceability.verifies" value="requirements_baseline" />
                      <agentwerke:prompt><![CDATA[
            Prepare the acceptance-test evidence package for human sign-off.

            Requirements:
            {{output.DraftRequirements}}

            System-test summary:
            {{output.SummarizeVerificationFailures}}
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_SummarizeVerificationFailures_PrepareAcceptanceTest</bpmn:incoming>
                  <bpmn:outgoing>Flow_PrepareAcceptanceTest_ApproveAcceptanceSignoff</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:userTask id="ApproveAcceptanceSignoff" name="Acceptance Sign-off">
                  <bpmn:extensionElements>
                    <agentwerke:approvalTask
                      purposeType="acceptance_signoff"
                      policyTag="vmodel-acceptance-signoff" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_PrepareAcceptanceTest_ApproveAcceptanceSignoff</bpmn:incoming>
                  <bpmn:outgoing>Flow_ApproveAcceptanceSignoff_PrepareTraceabilityReport</bpmn:outgoing>
                </bpmn:userTask>

                <bpmn:serviceTask id="PrepareTraceabilityReport" name="Prepare Traceability Report">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="analyst"
                      action="vmodel.traceability.report"
                      environment="github"
                      purposeType="traceability_report"
                      policyTag="vmodel-traceability-report"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read,github.comment_issue"
                      requiresEvidence="requirements_baseline,component_design_baseline,unit_test_results,integration_test_results,system_test_results,acceptance_signoff">
                      <agentwerke:metadata key="phase" value="traceability_report" />
                      <agentwerke:metadata key="traceability.produces" value="vmodel_traceability_matrix" />
                      <agentwerke:prompt><![CDATA[
            Prepare the final V-model traceability report.

            Include a matrix that maps:
            - requirements to acceptance tests
            - architecture decisions to system and integration tests
            - component designs to unit tests

            Also include approval and test-event evidence references.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_ApproveAcceptanceSignoff_PrepareTraceabilityReport</bpmn:incoming>
                  <bpmn:outgoing>Flow_PrepareTraceabilityReport_End</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:endEvent id="End" name="Evaluated">
                  <bpmn:incoming>Flow_PrepareTraceabilityReport_End</bpmn:incoming>
                </bpmn:endEvent>

                <bpmn:serviceTask id="EscalateVerificationTimeout" name="Escalate Verification Timeout">
                  <bpmn:extensionElements>
                    <agentwerke:agentTask
                      agent="analyst"
                      action="vmodel.verification.escalate_timeout"
                      environment="github"
                      purposeType="verification_escalation"
                      policyTag="vmodel-verification-timeout"
                      executionMode="local"
                      permissionLevel="read-only"
                      allowedTools="artifact.read,github.comment_issue"
                      requiresEvidence="requirements_baseline">
                      <agentwerke:metadata key="phase" value="verification_timeout" />
                      <agentwerke:prompt><![CDATA[
            A verification stage never reported results and its boundary timer expired.

            Timed-out wait:
            {{event.payload}}

            Report which verification stage stalled, what evidence is missing, and who should be
            asked to re-run or cancel the build.
                      ]]></agentwerke:prompt>
                    </agentwerke:agentTask>
                  </bpmn:extensionElements>
                  <bpmn:incoming>Flow_UnitTestResultsTimeout_EscalateVerificationTimeout</bpmn:incoming>
                  <bpmn:incoming>Flow_IntegrationTestResultsTimeout_EscalateVerificationTimeout</bpmn:incoming>
                  <bpmn:incoming>Flow_SystemTestResultsTimeout_EscalateVerificationTimeout</bpmn:incoming>
                  <bpmn:outgoing>Flow_EscalateVerificationTimeout_EndTimedOut</bpmn:outgoing>
                </bpmn:serviceTask>

                <bpmn:endEvent id="EndTimedOut" name="Verification Timed Out">
                  <bpmn:incoming>Flow_EscalateVerificationTimeout_EndTimedOut</bpmn:incoming>
                </bpmn:endEvent>

                <bpmn:sequenceFlow id="Flow_Start_DraftRequirements" sourceRef="Start" targetRef="DraftRequirements" />
                <bpmn:sequenceFlow id="Flow_DraftRequirements_ApproveRequirementsBaseline" sourceRef="DraftRequirements" targetRef="ApproveRequirementsBaseline" />
                <bpmn:sequenceFlow id="Flow_ApproveRequirementsBaseline_DraftSystemArchitecture" sourceRef="ApproveRequirementsBaseline" targetRef="DraftSystemArchitecture" />
                <bpmn:sequenceFlow id="Flow_DraftSystemArchitecture_DraftComponentDesign" sourceRef="DraftSystemArchitecture" targetRef="DraftComponentDesign" />
                <bpmn:sequenceFlow id="Flow_DraftComponentDesign_ImplementComponents" sourceRef="DraftComponentDesign" targetRef="ImplementComponents" />
                <bpmn:sequenceFlow id="Flow_ImplementComponents_GenerateUnitTestPlan" sourceRef="ImplementComponents" targetRef="GenerateUnitTestPlan" />
                <bpmn:sequenceFlow id="Flow_GenerateUnitTestPlan_WaitUnitTestResults" sourceRef="GenerateUnitTestPlan" targetRef="WaitUnitTestResults" />
                <bpmn:sequenceFlow id="Flow_WaitUnitTestResults_ValidateComponentTraceability" sourceRef="WaitUnitTestResults" targetRef="ValidateComponentTraceability" />
                <bpmn:sequenceFlow id="Flow_ValidateComponentTraceability_GenerateIntegrationTestPlan" sourceRef="ValidateComponentTraceability" targetRef="GenerateIntegrationTestPlan" />
                <bpmn:sequenceFlow id="Flow_GenerateIntegrationTestPlan_WaitIntegrationTestResults" sourceRef="GenerateIntegrationTestPlan" targetRef="WaitIntegrationTestResults" />
                <bpmn:sequenceFlow id="Flow_WaitIntegrationTestResults_ValidateArchitectureTraceability" sourceRef="WaitIntegrationTestResults" targetRef="ValidateArchitectureTraceability" />
                <bpmn:sequenceFlow id="Flow_ValidateArchitectureTraceability_AnalyzeSystemTestResults" sourceRef="ValidateArchitectureTraceability" targetRef="AnalyzeSystemTestResults" />
                <bpmn:sequenceFlow id="Flow_AnalyzeSystemTestResults_WaitSystemTestResults" sourceRef="AnalyzeSystemTestResults" targetRef="WaitSystemTestResults" />
                <bpmn:sequenceFlow id="Flow_WaitSystemTestResults_SummarizeVerificationFailures" sourceRef="WaitSystemTestResults" targetRef="SummarizeVerificationFailures" />
                <bpmn:sequenceFlow id="Flow_SummarizeVerificationFailures_PrepareAcceptanceTest" sourceRef="SummarizeVerificationFailures" targetRef="PrepareAcceptanceTest" />
                <bpmn:sequenceFlow id="Flow_PrepareAcceptanceTest_ApproveAcceptanceSignoff" sourceRef="PrepareAcceptanceTest" targetRef="ApproveAcceptanceSignoff" />
                <bpmn:sequenceFlow id="Flow_ApproveAcceptanceSignoff_PrepareTraceabilityReport" sourceRef="ApproveAcceptanceSignoff" targetRef="PrepareTraceabilityReport" />
                <bpmn:sequenceFlow id="Flow_PrepareTraceabilityReport_End" sourceRef="PrepareTraceabilityReport" targetRef="End" />

                <bpmn:sequenceFlow id="Flow_UnitTestResultsTimeout_EscalateVerificationTimeout" sourceRef="UnitTestResultsTimeout" targetRef="EscalateVerificationTimeout" />
                <bpmn:sequenceFlow id="Flow_IntegrationTestResultsTimeout_EscalateVerificationTimeout" sourceRef="IntegrationTestResultsTimeout" targetRef="EscalateVerificationTimeout" />
                <bpmn:sequenceFlow id="Flow_SystemTestResultsTimeout_EscalateVerificationTimeout" sourceRef="SystemTestResultsTimeout" targetRef="EscalateVerificationTimeout" />
                <bpmn:sequenceFlow id="Flow_EscalateVerificationTimeout_EndTimedOut" sourceRef="EscalateVerificationTimeout" targetRef="EndTimedOut" />
              </bpmn:process>
            </bpmn:definitions>
            """,
    };

    public static IReadOnlyList<SdlcTemplate> All { get; } =
    [
        IssueToPr,
        Bugfix,
        Hotfix,
        DeploymentApproval,
        SecurityReview,
        ReleaseApproval,
        AutonomousSdlc,
        VModel,
    ];
}
