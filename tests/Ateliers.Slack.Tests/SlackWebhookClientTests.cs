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
        using var httpClient = new HttpClient(handler);
        var client = new SlackWebhookClient(httpClient, TestWebhookUri);

        await client.SendMessageAsync("unit test notification");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(TestWebhookUri, handler.Request.RequestUri);
        using var payload = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("unit test notification", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendMessageAsync_NonSuccessResponseReportsOnlyStatus()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(TestWebhookUri.ToString())
        });
        using var httpClient = new HttpClient(handler);
        var client = new SlackWebhookClient(httpClient, TestWebhookUri);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(TestWebhookUri.ToString(), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_TransportErrorDoesNotExposeWebhookUrl()
    {
        using var handler = new RecordingHandler(_ => throw new HttpRequestException($"Failed at {TestWebhookUri}"));
        using var httpClient = new HttpClient(handler);
        var client = new SlackWebhookClient(httpClient, TestWebhookUri);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Contains("failed before receiving", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestWebhookUri.ToString(), exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task SendMessageAsync_UnexpectedSuccessBodyIsRejected()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not ok") });
        using var httpClient = new HttpClient(handler);
        var client = new SlackWebhookClient(httpClient, TestWebhookUri);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.DoesNotContain("not ok", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
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
        using var httpClient = new HttpClient();
        var client = new SlackWebhookClient(httpClient, webhookUri!);

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
