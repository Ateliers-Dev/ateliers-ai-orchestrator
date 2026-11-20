namespace Ateliers.Slack.IntegrationTests;

internal static class SlackBotSettingsLocator
{
    private const string SolutionFileName = "Ateliers.Ai.Orchestrator.sln";
    private const string IntegrationTestProjectPath = "tests/Ateliers.Slack.IntegrationTests/Ateliers.Slack.IntegrationTests.csproj";

    internal static bool TryFindSettingsPaths(string testAssemblyPath, out string configurationPath, out string secretsPath)
    {
        configurationPath = string.Empty;
        secretsPath = string.Empty;

        if (string.IsNullOrWhiteSpace(testAssemblyPath))
            return false;

        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(testAssemblyPath))!);
        while (directory is not null)
        {
            var isRepositoryRoot = File.Exists(Path.Combine(directory.FullName, SolutionFileName)) &&
                File.Exists(Path.Combine(directory.FullName, IntegrationTestProjectPath.Replace('/', Path.DirectorySeparatorChar)));

            if (isRepositoryRoot)
            {
                var configDirectory = Path.Combine(directory.FullName, "config");
                configurationPath = Path.Combine(configDirectory, "slack.json");
                secretsPath = Path.Combine(configDirectory, "slack.secrets.json");
                return File.Exists(configurationPath) && File.Exists(secretsPath);
            }

            directory = directory.Parent;
        }

        return false;
    }
}
