using System.Text.Json;

namespace Ateliers.Slack;

public sealed record SlackBotDefinition(
    string Id,
    string Name,
    string BotUserId,
    string ChannelId,
    bool Enabled);

public sealed class SlackBotConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private SlackBotConfiguration(SlackBotDefinition bot, string token)
    {
        Bot = bot;
        Token = token;
    }

    public SlackBotDefinition Bot { get; }

    internal string Token { get; }

    public static SlackBotConfiguration Load(string configurationPath, string secretsPath, string botId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(botId);

        if (!Path.IsPathFullyQualified(configurationPath) || !Path.IsPathFullyQualified(secretsPath))
            throw new ArgumentException("Slack configuration and secrets paths must be absolute.");

        try
        {
            var configuration = JsonSerializer.Deserialize<ConfigurationFile>(File.ReadAllText(configurationPath), JsonOptions)
                ?? throw new InvalidOperationException();
            var bot = configuration.Bots.SingleOrDefault(candidate => string.Equals(candidate.Id, botId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException();
            var secrets = JsonSerializer.Deserialize<SecretsFile>(File.ReadAllText(secretsPath), JsonOptions)
                ?? throw new InvalidOperationException();

            if (string.IsNullOrWhiteSpace(bot.Name) || string.IsNullOrWhiteSpace(bot.BotUserId) ||
                string.IsNullOrWhiteSpace(bot.ChannelId) || !bot.Enabled ||
                !secrets.Tokens.TryGetValue(bot.Id, out var token) || string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException();
            }

            return new SlackBotConfiguration(bot, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("Slack bot configuration is missing or invalid.");
        }
    }

    private sealed class ConfigurationFile
    {
        public List<SlackBotDefinition> Bots { get; init; } = [];
    }

    private sealed class SecretsFile
    {
        public Dictionary<string, string> Tokens { get; init; } = new(StringComparer.Ordinal);
    }
}
