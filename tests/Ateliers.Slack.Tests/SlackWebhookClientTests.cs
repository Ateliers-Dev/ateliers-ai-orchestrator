using System.Net;
using System.Text.Json;
using Ateliers.Slack;
using Xunit;

namespace Ateliers.Slack.Tests;

public sealed class SlackWebhookClientTests
{
    private static readonly Uri TestWebhookUri = new("https://hooks.slack.com/services/test/fake/token");

    [Fact]
    public async Task SendMessageAsync_PostsSlackPayloadAndAcceptsOkResponse()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var client = new SlackWebhookClient(TestWebhookUri, handler);

        await client.SendMessageAsync("unit test notification");

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(TestWebhookUri, handler.Requests[0].RequestUri);
        using var payload = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal("unit test notification", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendMessageAsync_NonSuccessResponseReportsOnlyStatus()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(TestWebhookUri.ToString())
        });
        using var client = new SlackWebhookClient(TestWebhookUri, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(TestWebhookUri.ToString(), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_TransportErrorDoesNotExposeWebhookUrl()
    {
        using var handler = new RecordingHandler(_ => throw new HttpRequestException($"Failed at {TestWebhookUri}"));
        using var client = new SlackWebhookClient(TestWebhookUri, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Contains("failed before receiving", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestWebhookUri.ToString(), exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task SendMessageAsync_UnexpectedSuccessBodyIsRejected()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not ok") });
        using var client = new SlackWebhookClient(TestWebhookUri, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.DoesNotContain("not ok", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_RedirectIsReturnedAsFailureWithoutPostingToLocation()
    {
        var redirectUri = new Uri("https://redirect-target.invalid/collect");
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = redirectUri }
        });
        using var client = new SlackWebhookClient(TestWebhookUri, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("redirect test"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, exception.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(TestWebhookUri, request.RequestUri);
        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies)!);
        Assert.Equal("redirect test", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void ProductionHandler_DisablesAutomaticRedirects()
    {
        using var handler = SlackWebhookClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(request);
        }
    }
}

public sealed class SlackWebhookIntegrationTests
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
