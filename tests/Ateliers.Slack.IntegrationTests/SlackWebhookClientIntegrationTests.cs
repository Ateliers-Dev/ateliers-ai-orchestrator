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
