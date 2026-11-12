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
        var configurationPath = Environment.GetEnvironmentVariable(SlackBotFactAttribute.ConfigurationPathVariable)!;
        var secretsPath = Environment.GetEnvironmentVariable(SlackBotFactAttribute.SecretsPathVariable)!;
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
    public const string OptInVariable = "ATELIERS_SLACK_BOT_INTEGRATION_TESTS";
    public const string ConfigurationPathVariable = "ATELIERS_SLACK_CONFIG_PATH";
    public const string SecretsPathVariable = "ATELIERS_SLACK_SECRETS_PATH";

    public SlackBotFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            Skip = $"Set {OptInVariable}=1 to enable live Slack Bot delivery.";
            return;
        }

        var configurationPath = Environment.GetEnvironmentVariable(ConfigurationPathVariable);
        var secretsPath = Environment.GetEnvironmentVariable(SecretsPathVariable);
        if (string.IsNullOrWhiteSpace(configurationPath) || string.IsNullOrWhiteSpace(secretsPath) ||
            !Path.IsPathFullyQualified(configurationPath) || !Path.IsPathFullyQualified(secretsPath))
        {
            Skip = $"Set absolute {ConfigurationPathVariable} and {SecretsPathVariable} paths to enable live Slack Bot delivery.";
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
