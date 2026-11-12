using System.Net;
using System.Net.Http.Json;

namespace Ateliers.Slack;

public sealed class SlackWebhookClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient httpClient;
    private readonly Uri webhookUri;

    public SlackWebhookClient(HttpClient httpClient, Uri webhookUri)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(webhookUri);

        if (!webhookUri.IsAbsoluteUri || webhookUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(webhookUri.UserInfo))
            throw new ArgumentException("Webhook URL must be an absolute HTTPS URL.", nameof(webhookUri));

        this.httpClient = httpClient;
        this.webhookUri = webhookUri;
    }

    public static SlackWebhookClient CreateForProduction(HttpClient httpClient)
    {
        var webhookUrl = Environment.GetEnvironmentVariable("ATELIERS_SLACK_WEBHOOK_URL");
        if (string.IsNullOrWhiteSpace(webhookUrl) ||
            !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ||
            webhookUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(webhookUri.UserInfo))
        {
            throw new InvalidOperationException("ATELIERS_SLACK_WEBHOOK_URL is missing or invalid.");
        }

        return new SlackWebhookClient(httpClient, webhookUri);
    }

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

    internal static SlackNotificationException TimedOut() =>
        new("Slack webhook request timed out.", null);

    internal static SlackNotificationException TransportFailure() =>
        new("Slack webhook request failed before receiving a response.", null);
}
