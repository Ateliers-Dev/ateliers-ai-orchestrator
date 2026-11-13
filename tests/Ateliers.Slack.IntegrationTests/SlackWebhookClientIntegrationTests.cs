using Ateliers.Slack;
using Xunit;

namespace Ateliers.Slack.IntegrationTests;

public sealed class SlackWebhookClientIntegrationTests
{
    private const string TestWebhookEnvironmentVariable = "ATELIERS_SLACK_TEST_WEBHOOK_URL";

    [SlackWebhookFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task SendMessageAsync_PostsIdentifiableTestNotification()
    {
        var webhookUrl = Environment.GetEnvironmentVariable(TestWebhookEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(webhookUrl));
        Assert.True(Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) &&
            webhookUri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(webhookUri.UserInfo),
            $"{TestWebhookEnvironmentVariable} must contain an absolute HTTPS URL.");

        var runId = Guid.NewGuid().ToString("N");
        var message = $"[TEST] Ateliers.Slack Integration Test\nRun ID: {runId}";
        using var client = new SlackWebhookClient(webhookUri!);

        await client.SendMessageAsync(message);
    }
}

public sealed class SlackWebhookFactAttribute : FactAttribute
{
    public SlackWebhookFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATELIERS_SLACK_TEST_WEBHOOK_URL")))
            Skip = "ATELIERS_SLACK_TEST_WEBHOOK_URL is not set; live Slack delivery is opt-in.";
    }
}

[Trait("Category", "ExternalIntegration")]
public sealed class SlackBotClientIntegrationTests
{
    private const string BotId = "test-bot";

    [SlackBotFact]
    [Trait("Category", "ExternalIntegration")]
    public async Task SendMessageAsync_PostsFromConfiguredBotToConfiguredChannel()
    {
        if (!SlackBotSettingsLocator.TryFindSettingsPaths(typeof(SlackBotClientIntegrationTests).Assembly.Location, out var configurationPath, out var secretsPath))
            throw new InvalidOperationException("Slack Bot settings disappeared after test discovery.");

        var configuration = SlackBotConfiguration.Load(configurationPath, secretsPath, BotId);
        var runId = Guid.NewGuid().ToString("N");
        var message = $"[TEST] {configuration.Bot.Name} Slack Web API integration test\nBot ID: {configuration.Bot.BotUserId}\nRun ID: {runId}";
        using var client = new SlackBotClient(configuration);

        var result = await client.SendMessageAsync(message);

        Assert.Equal(configuration.Bot.ChannelId, result.ChannelId);
        Assert.False(string.IsNullOrWhiteSpace(result.Timestamp));
    }
}

public sealed class SlackBotFactAttribute : FactAttribute
{
    public SlackBotFactAttribute()
    {
        if (!SlackBotSettingsLocator.TryFindSettingsPaths(typeof(SlackBotClientIntegrationTests).Assembly.Location, out var configurationPath, out var secretsPath))
        {
            Skip = "Slack Bot settings are not present under the repository config directory; live Slack delivery is skipped.";
            return;
        }

        try
        {
            _ = SlackBotConfiguration.Load(configurationPath, secretsPath, "test-bot");
        }
        catch (InvalidOperationException)
        {
            Skip = "Slack Bot configuration, channel settings, or credentials are missing or invalid.";
        }
    }
}

internal static class SlackBotSettingsLocator
{
    private const string SolutionFileName = "Ateliers.Ai.Orchestrator.sln";
    private const string IntegrationTestProjectPath = "tests/Ateliers.Slack.IntegrationTests/Ateliers.Slack.IntegrationTests.csproj";

    internal static bool TryFindSettingsPaths(string testAssemblyPath, out string configurationPath, out string secretsPath)
    {
        configurationPath = string.Empty;
        secretsPath = string.Empty;

        if (string.IsNullOrWhiteSpace(testAssemblyPath))
            return false;

        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(testAssemblyPath))!);
        while (directory is not null)
        {
            var isRepositoryRoot = File.Exists(Path.Combine(directory.FullName, SolutionFileName)) &&
                File.Exists(Path.Combine(directory.FullName, IntegrationTestProjectPath.Replace('/', Path.DirectorySeparatorChar)));

            if (isRepositoryRoot)
            {
                var configDirectory = Path.Combine(directory.FullName, "config");
                configurationPath = Path.Combine(configDirectory, "slack.json");
                secretsPath = Path.Combine(configDirectory, "slack.secrets.json");
                return File.Exists(configurationPath) && File.Exists(secretsPath);
            }

            directory = directory.Parent;
        }

        return false;
    }
}
