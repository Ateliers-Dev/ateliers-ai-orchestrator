using System.Text.Json;
using Ateliers.Ai.Orchestrator.Cli;
using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class AgentConfigurationLoaderTests
{
    [Fact]
    public void Load_KeepsAgentRoleAndSlackIdentitySeparateAndCombinesInstructionsInPriorityOrder()
    {
        using var files = new AgentConfigurationFiles();
        files.WriteValidConfiguration();

        var snapshot = AgentConfigurationLoader.Load(files.ConfigurationPath);
        var implementer = snapshot.ForRole("Implementer");
        var reviewer = snapshot.ForRole("Reviewer");

        Assert.Equal("builder-v2", implementer.Id);
        Assert.Equal("Implementer", implementer.Role);
        Assert.Equal("implementer-bot", implementer.SlackBotId);
        Assert.Equal("specialist", reviewer.Id);
        Assert.Equal("Reviewer", reviewer.Role);
        Assert.Equal("reviewer-bot", reviewer.SlackBotId);
        Assert.Equal("security review", reviewer.Specialty);
        Assert.True(implementer.Instructions.IndexOf("Common instructions", StringComparison.Ordinal) < implementer.Instructions.IndexOf("Slack report instructions", StringComparison.Ordinal));
        Assert.True(implementer.Instructions.IndexOf("Slack report instructions", StringComparison.Ordinal) < implementer.Instructions.IndexOf("Agent-specific instructions", StringComparison.Ordinal));
        Assert.Equal("implementer-bot", snapshot.SlackBotRoutes["Implementer"]);
        Assert.Equal("reviewer-bot", snapshot.SlackBotRoutes["Reviewer"]);
    }

    [Fact]
    public void ToAuditSnapshot_StoresInstructionFingerprintButNotInstructionBody()
    {
        using var files = new AgentConfigurationFiles();
        files.WriteValidConfiguration();

        var audit = JsonSerializer.Serialize(AgentConfigurationLoader.Load(files.ConfigurationPath).ToAuditSnapshot());

        Assert.Contains("InstructionsSha256", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("system-only text", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("Slack-only text", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("builder-only text", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsMultipleAssignmentsForOneRole()
    {
        using var files = new AgentConfigurationFiles();
        files.WriteValidConfiguration();
        File.WriteAllText(files.ConfigurationPath,
            """{"assignments":{"Implementer":[{"agentId":"builder-v2"},{"agentId":"builder-v2"}],"Reviewer":[{"agentId":"specialist","specialty":"security review"}]}}""");

        var exception = Assert.Throws<InvalidOperationException>(() => AgentConfigurationLoader.Load(files.ConfigurationPath));

        Assert.Contains("1件", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsProfileWhoseRoleDoesNotMatchAssignment()
    {
        using var files = new AgentConfigurationFiles();
        files.WriteValidConfiguration();
        var path = Path.Combine(files.AgentsDirectory, "implementer-builder-v2.json");
        File.WriteAllText(path, """{"id":"builder-v2","name":"Builder","role":"Reviewer","slackBotId":"implementer-bot"}""");

        var exception = Assert.Throws<InvalidOperationException>(() => AgentConfigurationLoader.Load(files.ConfigurationPath));

        Assert.Contains("ID、Role", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsInstructionReferenceOutsideAgentsDirectory()
    {
        using var files = new AgentConfigurationFiles();
        files.WriteValidConfiguration(customReference: "../outside.md");

        var exception = Assert.Throws<InvalidOperationException>(() => AgentConfigurationLoader.Load(files.ConfigurationPath));

        Assert.Contains("agentsフォルダー外", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyDefaults_UseStableAgentIdsDistinctFromRolesAndSlackBotIds()
    {
        var defaults = Ateliers.Ai.Orchestrator.Core.AgentConfigurationSnapshot.LegacyDefaults();

        Assert.Equal("default-implementer", defaults.ForRole("Implementer").Id);
        Assert.Equal("Implementer", defaults.ForRole("Implementer").Role);
        Assert.Equal("implementer", defaults.ForRole("Implementer").SlackBotId);
        Assert.Equal("default-reviewer", defaults.ForRole("Reviewer").Id);
        Assert.Equal("Reviewer", defaults.ForRole("Reviewer").Role);
        Assert.Equal("reviewer", defaults.ForRole("Reviewer").SlackBotId);
    }

    private sealed class AgentConfigurationFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"agent-config-tests-{Guid.NewGuid():N}");
        public string AgentsDirectory => Path.Combine(root, "agents");
        public string ConfigurationPath => Path.Combine(root, "agents.json");

        public AgentConfigurationFiles()
        {
            Directory.CreateDirectory(Path.Combine(AgentsDirectory, "instructions"));
        }

        public void WriteValidConfiguration(string customReference = "instructions/builder.md")
        {
            File.WriteAllText(ConfigurationPath,
                """{"assignments":{"Implementer":[{"agentId":"builder-v2"}],"Reviewer":[{"agentId":"specialist","specialty":"security review"}]}}""");
            File.WriteAllText(Path.Combine(AgentsDirectory, "common-instructions.md"), "system-only text");
            File.WriteAllText(Path.Combine(AgentsDirectory, "instructions", "slack-report.md"), "Slack-only text");
            File.WriteAllText(Path.Combine(AgentsDirectory, "instructions", "builder.md"), "builder-only text");
            File.WriteAllText(Path.Combine(AgentsDirectory, "implementer-builder-v2.json"), JsonSerializer.Serialize(new
            {
                id = "builder-v2", name = "Builder", role = "Implementer", slackBotId = "implementer-bot", customs = new[] { customReference }
            }));
            File.WriteAllText(Path.Combine(AgentsDirectory, "reviewer-specialist.json"),
                """{"id":"specialist","name":"Specialist","role":"Reviewer","slackBotId":"reviewer-bot"}""");
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
