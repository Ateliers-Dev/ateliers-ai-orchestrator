using System.Net;
using System.Net.Http.Json;

namespace Ateliers.Slack;

public sealed class SlackWebhookClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient httpClient;
    private readonly Uri webhookUri;

    public SlackWebhookClient(Uri webhookUri)
        : this(webhookUri, CreateHandler())
    {
    }

    internal SlackWebhookClient(Uri webhookUri, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(webhookUri);
        ArgumentNullException.ThrowIfNull(handler);

        if (!webhookUri.IsAbsoluteUri || webhookUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(webhookUri.UserInfo))
            throw new ArgumentException("Webhook URL must be an absolute HTTPS URL.", nameof(webhookUri));

        httpClient = new HttpClient(handler);
        this.webhookUri = webhookUri;
    }

    public static SlackWebhookClient CreateForProduction()
    {
        var webhookUrl = Environment.GetEnvironmentVariable("ATELIERS_SLACK_WEBHOOK_URL");
        if (string.IsNullOrWhiteSpace(webhookUrl) ||
            !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ||
            webhookUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(webhookUri.UserInfo))
        {
            throw new InvalidOperationException("ATELIERS_SLACK_WEBHOOK_URL is missing or invalid.");
        }

        return new SlackWebhookClient(webhookUri);
    }

    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                webhookUri,
                new SlackMessage(message),
                timeoutSource.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw SlackNotificationException.FromStatusCode(response.StatusCode);

            var responseBody = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            if (!string.Equals(responseBody, "ok", StringComparison.Ordinal))
                throw SlackNotificationException.InvalidResponse(response.StatusCode);
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

    private sealed record SlackMessage(string Text);
}

public sealed class SlackNotificationException : Exception
{
    private SlackNotificationException(string message, HttpStatusCode? statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    internal static SlackNotificationException FromStatusCode(HttpStatusCode statusCode) =>
        new($"Slack webhook returned HTTP {(int)statusCode}.", statusCode);

    internal static SlackNotificationException InvalidResponse(HttpStatusCode statusCode) =>
        new("Slack webhook returned an unexpected success response.", statusCode);

    internal static SlackNotificationException ApiFailure(HttpStatusCode statusCode, string? error) =>
        new($"Slack Web API rejected the request ({SafeErrorCode(error)}).", statusCode);

    internal static SlackNotificationException RateLimited(int? retryAfterSeconds) =>
        new("Slack Web API rate limit reached (HTTP 429).", HttpStatusCode.TooManyRequests, retryAfterSeconds);

    internal static SlackNotificationException TimedOut() =>
        new("Slack webhook request timed out.", null);

    internal static SlackNotificationException TransportFailure() =>
        new("Slack webhook request failed before receiving a response.", null);

    public int? RetryAfterSeconds { get; }

    private SlackNotificationException(string message, HttpStatusCode? statusCode, int? retryAfterSeconds) : this(message, statusCode)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    private static string SafeErrorCode(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "unknown_error";

        var safe = new string(error.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').Take(64).ToArray());
        return safe.Length == 0 ? "unknown_error" : safe;
    }
}
