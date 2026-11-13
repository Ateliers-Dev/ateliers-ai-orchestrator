using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ateliers.Slack;

public sealed class SlackBotClient : IDisposable
{
    private static readonly Uri PostMessageUri = new("https://slack.com/api/chat.postMessage");
    private readonly TimeSpan requestTimeout;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient httpClient;
    private readonly SlackBotConfiguration configuration;

    public SlackBotClient(SlackBotConfiguration configuration)
        : this(configuration, SlackWebhookClient.CreateHandler(), TimeSpan.FromSeconds(10))
    {
    }

    internal SlackBotClient(SlackBotConfiguration configuration, HttpMessageHandler handler, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(handler);
        this.configuration = configuration;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (this.requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Token);
    }

    public async Task<SlackPostMessageResult> SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(requestTimeout);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                PostMessageUri,
                new SlackPostMessageRequest(configuration.ChannelId, message),
                JsonOptions,
                timeoutSource.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw SlackNotificationException.RateLimited(ReadRetryAfter(response));
            if (!response.IsSuccessStatusCode)
                throw SlackNotificationException.FromStatusCode(response.StatusCode);

            SlackPostMessageResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<SlackPostMessageResponse>(cancellationToken: timeoutSource.Token).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw SlackNotificationException.InvalidResponse(response.StatusCode);
            }

            if (body is null || !body.Ok)
                throw SlackNotificationException.ApiFailure(response.StatusCode);

            if (string.IsNullOrWhiteSpace(body.Channel) || string.IsNullOrWhiteSpace(body.Ts))
                throw SlackNotificationException.InvalidResponse(response.StatusCode);

            return new SlackPostMessageResult(body.Channel, body.Ts);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw SlackNotificationException.TimedOut();
        }
        catch (HttpRequestException)
        {
            throw SlackNotificationException.TransportFailure();
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static int? ReadRetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        if (value is not null)
            return Math.Max(0, (int)Math.Ceiling(value.Value));

        return response.Headers.TryGetValues("Retry-After", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var seconds) ? Math.Max(0, seconds) : null;
    }

    private sealed record SlackPostMessageRequest(string Channel, string Text);
    private sealed record SlackPostMessageResponse(bool Ok, string? Channel, string? Ts);
}

public sealed record SlackPostMessageResult(string ChannelId, string Timestamp);
