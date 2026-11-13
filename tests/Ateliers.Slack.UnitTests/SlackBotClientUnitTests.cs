using System.Net;
using System.Text.Json;
using Ateliers.Slack;
using Xunit;

namespace Ateliers.Slack.UnitTests;

public sealed class SlackBotClientUnitTests
{
    private const string Token = "xoxb-unit-test-secret";
    private static readonly Uri ApiUri = new("https://slack.com/api/chat.postMessage");

    [Fact]
    public async Task SendMessageAsync_PostsAsConfiguredBotAndReturnsMessageReference()
    {
        using var files = new TestConfigurationFiles();
        var configuration = files.Load();
        using var handler = new RecordingHandler(_ => JsonResponse("{\"ok\":true,\"channel\":\"C123TEST\",\"ts\":\"1700000000.000001\"}"));
        using var client = new SlackBotClient(configuration, handler);

        var result = await client.SendMessageAsync("hello from test bot");

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(ApiUri, handler.Requests[0].RequestUri);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal(Token, handler.Requests[0].Headers.Authorization!.Parameter);
        using var payload = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal("C123TEST", payload.RootElement.GetProperty("channel").GetString());
        Assert.Equal("hello from test bot", payload.RootElement.GetProperty("text").GetString());
        Assert.Equal("C123TEST", result.ChannelId);
        Assert.Equal("1700000000.000001", result.Timestamp);
    }

    [Fact]
    public async Task SendMessageAsync_ApiFailureDoesNotExposeResponseBodyOrToken()
    {
        using var files = new TestConfigurationFiles();
        var configuration = files.Load();
        using var handler = new RecordingHandler(_ => JsonResponse($"{{\"ok\":false,\"error\":\"missing_scope\",\"detail\":\"{Token}\"}}"));
        using var client = new SlackBotClient(configuration, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal("Slack Web API rejected the request.", exception.Message);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiUri.ToString(), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_ApiErrorThatContainsTokenIsNotExposed()
    {
        using var files = new TestConfigurationFiles();
        var configuration = files.Load();
        using var handler = new RecordingHandler(_ => JsonResponse($"{{\"ok\":false,\"error\":\"{Token}\"}}"));
        using var client = new SlackBotClient(configuration, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal("Slack Web API rejected the request.", exception.Message);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(string.Concat(Token.Where(char.IsAsciiLetterOrDigit)), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_HttpFailureDoesNotExposeResponseBodyOrToken()
    {
        using var files = new TestConfigurationFiles();
        var configuration = files.Load();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(Token)
        });
        using var client = new SlackBotClient(configuration, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiUri.ToString(), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_RateLimitReportsRetryDelayWithoutExposingResponseBody()
    {
        using var files = new TestConfigurationFiles();
        var configuration = files.Load();
        using var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(Token) };
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return response;
        });
        using var client = new SlackBotClient(configuration, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(30, exception.RetryAfterSeconds);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_OkFalseIsRejectedEvenWithHttpSuccess()
    {
        using var files = new TestConfigurationFiles();
        var configuration = files.Load();
        using var handler = new RecordingHandler(_ => JsonResponse("{\"ok\":false,\"error\":\"channel_not_found\"}"));
        using var client = new SlackBotClient(configuration, handler);

        var exception = await Assert.ThrowsAsync<SlackNotificationException>(() => client.SendMessageAsync("test"));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("Slack Web API rejected the request.", exception.Message);
    }

    [Fact]
    public void ConfigurationRequiresAbsolutePaths()
    {
        var exception = Assert.Throws<ArgumentException>(() => SlackBotConfiguration.Load("slack.json", "slack.secrets.json", "test-bot"));

        Assert.DoesNotContain("slack.secrets.json", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsNullBots()
    {
        using var files = new TestConfigurationFiles("{\"bots\":null}", "{\"tokens\":{\"test-bot\":\"xoxb-test\"}}");

        var exception = Assert.Throws<InvalidOperationException>(() => files.Load());

        Assert.Equal("Slack bot configuration is missing or invalid.", exception.Message);
    }

    [Fact]
    public void ConfigurationRejectsNullTokens()
    {
        using var files = new TestConfigurationFiles(
            "{\"bots\":[{\"id\":\"test-bot\",\"name\":\"Ateliers Test Bot\",\"botUserId\":\"U123TEST\",\"channelId\":\"C123TEST\",\"enabled\":true}]}",
            "{\"tokens\":null}");

        var exception = Assert.Throws<InvalidOperationException>(() => files.Load());

        Assert.Equal("Slack bot configuration is missing or invalid.", exception.Message);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class TestConfigurationFiles : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"ateliers-slack-tests-{Guid.NewGuid():N}");

        public TestConfigurationFiles(string? configurationJson = null, string? secretsJson = null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "slack.json"), configurationJson ?? """{"bots":[{"id":"test-bot","name":"Ateliers Test Bot","botUserId":"U123TEST","channelId":"C123TEST","enabled":true}]}""");
            File.WriteAllText(Path.Combine(directory, "slack.secrets.json"), secretsJson ?? JsonSerializer.Serialize(new { tokens = new Dictionary<string, string> { ["test-bot"] = Token } }));
        }

        public SlackBotConfiguration Load() => SlackBotConfiguration.Load(
            Path.Combine(directory, "slack.json"),
            Path.Combine(directory, "slack.secrets.json"),
            "test-bot");

        public void Dispose() => Directory.Delete(directory, recursive: true);
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
