using System.Text.Json;
using System.Text.RegularExpressions;
using Ateliers.Ai.Orchestrator.Core;

namespace Ateliers.Ai.Orchestrator.Cli;

internal static partial class AgentConfigurationLoader
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Roles = ["Implementer", "Reviewer"];

    public static bool TryFindConfiguration(string? localAppData, string? applicationBaseDirectory, out string configurationPath)
    {
        configurationPath = string.Empty;
        localAppData ??= Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var userPath = Path.Combine(localAppData, "Ateliers-Dev", "Ai-Orchestrator", "config", "agents.json");
            if (File.Exists(userPath)) { configurationPath = Path.GetFullPath(userPath); return true; }
        }

        applicationBaseDirectory ??= AppContext.BaseDirectory;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var current = new DirectoryInfo(applicationBaseDirectory); current is not null; current = current.Parent)
        {
            if (!seen.Add(current.FullName)) continue;
            var candidate = Path.Combine(current.FullName, "config", "agents.json");
            if (File.Exists(candidate)) { configurationPath = Path.GetFullPath(candidate); return true; }
        }
        return false;
    }

    public static AgentConfigurationSnapshot Load(string configurationPath)
    {
        if (!Path.IsPathFullyQualified(configurationPath) || !File.Exists(configurationPath))
            throw new InvalidOperationException("Agent設定ファイルを確認できません。");
        EnsureNoLinks(Path.GetFullPath(configurationPath));
        var configRoot = Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        var agentsRoot = Path.Combine(configRoot, "agents");
        var document = JsonSerializer.Deserialize<AssignmentsFile>(ReadConfigurationText(configurationPath), JsonOptions)
            ?? throw new InvalidOperationException("Agent割当設定が空です。");
        var commonInstructions = ReadOptionalInstruction(Path.Combine(agentsRoot, "common-instructions.md"));
        var slackInstructions = ReadOptionalInstruction(Path.Combine(agentsRoot, "instructions", "slack-report.md"));
        var profiles = new List<AgentProfileSettings>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in Roles)
        {
            if (document.Assignments is null || !document.Assignments.TryGetValue(role, out var assignments) || assignments is null || assignments.Count != 1)
                throw new InvalidOperationException($"{role}にはMilestone 1で実行するAgentを1件だけ指定してください。");
            var assignment = assignments[0] ?? throw new InvalidOperationException($"{role}のAgent割当が不正です。");
            if (!IsAgentId(assignment.AgentId)) throw new InvalidOperationException($"{role}のAgent IDが不正です。");
            var specialty = CleanOptional(assignment.Specialty);
            if (specialty is not null && (role != "Reviewer" || specialty.Length > 100 || specialty.Any(char.IsControl)))
                throw new InvalidOperationException($"{role}の専門分野設定が不正です。");
            var profilePath = SafeReference(agentsRoot, $"{role.ToLowerInvariant()}-{assignment.AgentId}.json");
            var profile = JsonSerializer.Deserialize<AgentProfileFile>(ReadConfigurationText(profilePath), JsonOptions)
                ?? throw new InvalidOperationException($"{role}のAgent設定が空です。");
            if (!IsAgentId(profile.Id) || !string.Equals(profile.Id, assignment.AgentId, StringComparison.Ordinal) ||
                !string.Equals(profile.Role, role, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(profile.Name) ||
                profile.Name.Length > 100 || profile.Name.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(profile.SlackBotId) || !IsIdentifier(profile.SlackBotId))
                throw new InvalidOperationException($"{role}のAgent ID、Role、表示名、Slack Bot IDが設定と一致しません。");
            if (!ids.Add(profile.Id!)) throw new InvalidOperationException("Agent IDはRole間で重複できません。");
            if (!string.IsNullOrWhiteSpace(profile.DefaultModelReasoningEffort) &&
                !new[] { "minimal", "low", "medium", "high", "xhigh" }.Contains(profile.DefaultModelReasoningEffort, StringComparer.Ordinal))
                throw new InvalidOperationException($"{role}の推論強度が未対応です。");
            var custom = new List<string>();
            foreach (var reference in profile.Customs ?? [])
            {
                if (string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Agent個別指示の参照が空です。");
                var customPath = SafeReference(agentsRoot, reference);
                if (!File.Exists(customPath)) throw new InvalidOperationException("Agent個別指示ファイルを確認できません。");
                custom.Add(ReadOptionalInstruction(customPath));
            }
            var model = CleanOptional(profile.DefaultModel);
            if (model is not null && !ModelIdentifierPattern().IsMatch(model))
                throw new InvalidOperationException($"{role}のモデルIDが不正です。");
            profiles.Add(new AgentProfileSettings(profile.Id!, profile.Name.Trim(), role, specialty, profile.SlackBotId,
                model, CleanOptional(profile.DefaultModelReasoningEffort),
                JoinInstructions(("Common", commonInstructions), ("Slack report", slackInstructions),
                    ("Agent-specific", string.Join("\n\n", custom)))));
        }

        return new AgentConfigurationSnapshot(profiles);
    }

    private static string ReadOptionalInstruction(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        EnsureNoLinks(path);
        var info = new FileInfo(path);
        if (info.Length > 256_000) throw new InvalidOperationException("Agent指示ファイルが256,000バイトを超えています。");
        var text = StrictUtf8.GetString(File.ReadAllBytes(path));
        if (text.Length > 64_000) throw new InvalidOperationException("Agent指示ファイルが64,000文字を超えています。");
        return text.Trim();
    }

    private static string ReadConfigurationText(string path)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new InvalidOperationException("Agent設定JSONが1,000,000バイトを超えています。");
        return StrictUtf8.GetString(File.ReadAllBytes(path));
    }

    private static string SafeReference(string root, string reference)
    {
        if (Path.IsPathRooted(reference) || reference.Contains(':') || reference.Contains('\\'))
            throw new InvalidOperationException("Agent指示の参照はagentsフォルダー内の相対パスにしてください。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Agent指示の参照がagentsフォルダー外を指しています。");
        EnsureNoLinks(fullPath);
        return fullPath;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = new FileInfo(path); current is not null; current = current.Directory?.Parent is { } parent ? new FileInfo(parent.FullName) : null)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Agent設定または指示にシンボリックリンクを使用できません。");
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Agent設定または指示にシンボリックリンクを使用できません。");
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static string JoinInstructions(params (string Label, string Content)[] instructions) =>
        string.Join("\n\n", instructions.Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .Select(item => $"## {item.Label} instructions\n{item.Content}"));
    private static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsAgentId(string? value) => value is not null && AgentIdPattern().IsMatch(value);
    private static bool IsIdentifier(string? value) => value is not null && IdentifierPattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdPattern();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdentifierPattern();

    private sealed class AssignmentsFile { public Dictionary<string, List<AssignmentFile?>?>? Assignments { get; init; } }
    private sealed class AssignmentFile { public string? AgentId { get; init; } public string? Specialty { get; init; } }
    private sealed class AgentProfileFile
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Role { get; init; }
        public string? SlackBotId { get; init; }
        public string? DefaultModel { get; init; }
        public string? DefaultModelReasoningEffort { get; init; }
        public List<string>? Customs { get; init; }
    }
}
