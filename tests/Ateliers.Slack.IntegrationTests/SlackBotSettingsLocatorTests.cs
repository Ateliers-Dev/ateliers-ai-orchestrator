using Xunit;

namespace Ateliers.Slack.IntegrationTests;

public sealed class SlackBotSettingsLocatorTests
{
    [Fact]
    public void TryFindSettingsPaths_UsesTestAssemblyLocationAndRequiresBothLocalFiles()
    {
        using var files = new TemporaryRepository();

        var foundBeforeSecretsExist = SlackBotSettingsLocator.TryFindSettingsPaths(files.TestAssemblyPath, out _, out _);

        Assert.False(foundBeforeSecretsExist);

        File.WriteAllText(files.SecretsPath, "{}");
        var foundAfterBothFilesExist = SlackBotSettingsLocator.TryFindSettingsPaths(files.TestAssemblyPath, out var configurationPath, out var secretsPath);

        Assert.True(foundAfterBothFilesExist);
        Assert.Equal(files.ConfigurationPath, configurationPath);
        Assert.Equal(files.SecretsPath, secretsPath);
    }

    [Fact]
    public void TryFindSettingsPaths_ReturnsFalseWhenAssemblyIsOutsideRepository()
    {
        using var files = new TemporaryRepository();
        var unrelatedAssemblyPath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}", "test.dll");

        Assert.False(SlackBotSettingsLocator.TryFindSettingsPaths(unrelatedAssemblyPath, out _, out _));
    }

    [Fact]
    public void SlackBotClientIntegrationTests_UsesDedicatedTestChannelConfiguration()
    {
        using var files = new TemporaryRepository();
        File.WriteAllText(files.ConfigurationPath, """{"testChannelId":"C_TEST","bots":[{"id":"test-bot","name":"Test","botUserId":"U_TEST","channelId":"C_PRODUCTION","enabled":true}]}""");
        File.WriteAllText(files.SecretsPath, """{"tokens":{"test-bot":"xoxb-test"}}""");

        Assert.True(SlackBotSettingsLocator.TryFindSettingsPaths(files.TestAssemblyPath, out var configurationPath, out var secretsPath));
        var settings = Ateliers.Slack.SlackBotConfiguration.Load(configurationPath, secretsPath, "test-bot", useTestChannel: true);

        Assert.Equal("C_TEST", settings.ChannelId);
        Assert.NotEqual(settings.Bot.ChannelId, settings.ChannelId);
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"ateliers-slack-settings-{Guid.NewGuid():N}");
        public string ConfigurationPath => Path.Combine(DirectoryPath, "config", "slack.json");
        public string SecretsPath => Path.Combine(DirectoryPath, "config", "slack.secrets.json");
        public string TestAssemblyPath => Path.Combine(DirectoryPath, "tests", "Ateliers.Slack.IntegrationTests", "bin", "Debug", "net10.0", "tests.dll");

        public TemporaryRepository()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(TestAssemblyPath)!);
            File.WriteAllText(Path.Combine(DirectoryPath, "Ateliers.Ai.Orchestrator.sln"), string.Empty);
            File.WriteAllText(Path.Combine(DirectoryPath, "tests", "Ateliers.Slack.IntegrationTests", "Ateliers.Slack.IntegrationTests.csproj"), string.Empty);
            File.WriteAllText(ConfigurationPath, "{}");
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
