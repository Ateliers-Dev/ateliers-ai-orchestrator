using System.Text.Json;
using Ateliers.Ai.Orchestrator.Core;
using Xunit;

namespace Ateliers.Ai.Orchestrator.FakeTests;

[Trait("Category", "Fake")]
public sealed class AgentConfigurationWorkflowFakeTests
{
    [Fact]
    public void Run_PassesResolvedProfilesToEachAgentAndPersistsSafeConfigurationAudit()
    {
        using var temp = new TemporaryDirectory();
        var runId = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(temp.Path, "run");
        Directory.CreateDirectory(runDirectory);
        var lifecyclePath = Path.Combine(temp.Path, "lifecycle.jsonl");
        var configuration = new AgentConfigurationSnapshot(
        [
            new AgentProfileSettings("builder-v2", "Builder", "Implementer", null, "implementer-bot", "gpt-example", "high", "implement-only secret text"),
            new AgentProfileSettings("specialist", "Security Reviewer", "Reviewer", "security review", "reviewer-bot", null, null, "review-only secret text")
        ]);
        var runner = new CapturingAgentRunner();

        using (var events = JsonlRunEventStore.CreateNew(Path.Combine(runDirectory, "events.jsonl"), runId))
        {
            var lifecycleWriter = new JsonlLifecycleEventWriter(lifecyclePath);
            var result = new FakeAgentWorkflow().Run(SampleContract(), new string('a', 64), runId, runDirectory,
                runner, events, lifecycleEvents: lifecycleWriter, agentConfiguration: configuration);

            Assert.Equal(RunState.NeedsHumanInput, result.State);
        }

        Assert.Equal("builder-v2", runner.ImplementRequest!.AgentId);
        Assert.Equal("Implementer", runner.ImplementRequest.Role);
        Assert.Equal("implementer-bot", runner.ImplementRequest.SlackBotId);
        Assert.Equal("gpt-example", runner.ImplementRequest.DefaultModel);
        Assert.Equal("high", runner.ImplementRequest.DefaultModelReasoningEffort);
        Assert.Equal("implement-only secret text", runner.ImplementRequest.Instructions);
        Assert.Equal("specialist", runner.ReviewRequest!.AgentId);
        Assert.Equal("Reviewer", runner.ReviewRequest.Role);
        Assert.Equal("reviewer-bot", runner.ReviewRequest.SlackBotId);
        Assert.Equal("security review", runner.ReviewRequest.Specialty);
        Assert.Equal("review-only secret text", runner.ReviewRequest.Instructions);

        var audit = File.ReadAllText(Path.Combine(runDirectory, "agent-configuration.json"));
        Assert.Contains("builder-v2", audit, StringComparison.Ordinal);
        Assert.Contains("reviewer-bot", audit, StringComparison.Ordinal);
        Assert.Contains("InstructionsSha256", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("implement-only secret text", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("review-only secret text", audit, StringComparison.Ordinal);
        var lifecycleDocuments = File.ReadAllLines(lifecyclePath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var agentEvents = lifecycleDocuments.Select(item => item.RootElement)
                .Where(item => item.GetProperty("eventType").GetString() is "started" or "completed")
                .Where(item => item.GetProperty("role").GetString() is "Implementer" or "Reviewer").ToArray();
            Assert.Contains(agentEvents, item => item.GetProperty("agentId").GetString() == "builder-v2" && item.GetProperty("slackBotId").GetString() == "implementer-bot");
            Assert.Contains(agentEvents, item => item.GetProperty("agentId").GetString() == "specialist" && item.GetProperty("slackBotId").GetString() == "reviewer-bot");
        }
        finally
        {
            foreach (var item in lifecycleDocuments) item.Dispose();
        }
    }

    private static TaskContract SampleContract() => new()
    {
        SchemaVersion = 1,
        TaskId = "agent-config-test",
        Purpose = "Run単位Agent設定を引き渡す",
        WorkingDirectory = Path.GetFullPath(Path.GetTempPath()),
        Scope = new TaskScope { Included = ["src"], Excluded = ["bin"] },
        Inputs = [new TaskInput { Description = "fixture", Reference = "fixture://agent-config", Revision = "1" }],
        Requirements = ["Resolved Agent settings reach each role"],
        AcceptanceCriteria = ["Agent settings are isolated and audited safely"],
        DelegatedOperations = [],
        Limits = new ExecutionLimits { MaxDurationMinutes = 1, MaxRevisionAttempts = 1, MaxRetries = 0 },
        StopConditions = ["time limit"],
        Reporting = new ReportingRequirements { PullRequestExpected = false, FinalReportFormat = "markdown" }
    };

    private sealed class CapturingAgentRunner : IAgentRunner
    {
        private readonly FakeAgentRunner inner = new(FakeScenario.Success);
        public AgentRequest? ImplementRequest { get; private set; }
        public AgentRequest? ReviewRequest { get; private set; }
        public AgentResponse Implement(AgentRequest request) { ImplementRequest = request; return inner.Implement(request); }
        public AgentResponse Review(AgentRequest request) { ReviewRequest = request; return inner.Review(request); }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agent-config-workflow-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
