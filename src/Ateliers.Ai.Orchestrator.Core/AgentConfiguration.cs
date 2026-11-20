using System.Security.Cryptography;
using System.Text;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>Run開始時に解決したAgent設定です。Run中のファイル変更は反映しません。</summary>
public sealed record AgentProfileSettings(
    string Id,
    string Name,
    string Role,
    string? Specialty,
    string SlackBotId,
    string? DefaultModel,
    string? DefaultModelReasoningEffort,
    string Instructions);

/// <summary>Roleごとに一つのAgentを選択した、Run単位の設定スナップショットです。</summary>
public sealed class AgentConfigurationSnapshot
{
    private readonly IReadOnlyDictionary<string, AgentProfileSettings> profiles;

    public AgentConfigurationSnapshot(IEnumerable<AgentProfileSettings> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var resolved = profiles.ToDictionary(profile => profile.Role, StringComparer.Ordinal);
        if (!resolved.ContainsKey("Implementer") || !resolved.ContainsKey("Reviewer"))
            throw new ArgumentException("ImplementerとReviewerの設定が必要です。", nameof(profiles));
        this.profiles = resolved;
    }

    public AgentProfileSettings ForRole(string role) => profiles.TryGetValue(role, out var profile)
        ? profile
        : throw new ArgumentException("未対応のAgent Roleです。", nameof(role));

    public IReadOnlyDictionary<string, string> SlackBotRoutes => profiles.ToDictionary(
        entry => entry.Key, entry => entry.Value.SlackBotId, StringComparer.Ordinal);

    public IReadOnlyList<AgentProfileAuditRecord> ToAuditRecords() => profiles.Values.OrderBy(profile => profile.Role, StringComparer.Ordinal)
        .Select(profile => new AgentProfileAuditRecord(profile.Id, profile.Name, profile.Role, profile.Specialty,
            profile.SlackBotId, profile.DefaultModel, profile.DefaultModelReasoningEffort,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Instructions)))))
        .ToArray();

    public AgentConfigurationAuditSnapshot ToAuditSnapshot() => new(1, ToAuditRecords());

    /// <summary>設定ファイル未配置時の後方互換設定です。</summary>
    public static AgentConfigurationSnapshot LegacyDefaults() => new(
    [
        new AgentProfileSettings("default-implementer", "Implementer", "Implementer", null, "implementer", null, null, string.Empty),
        new AgentProfileSettings("default-reviewer", "Reviewer", "Reviewer", "code", "reviewer", null, null, string.Empty)
    ]);
}

/// <summary>実行に用いたAgent設定を識別する秘密情報を含まない監査記録です。</summary>
public sealed record AgentProfileAuditRecord(string AgentId, string AgentName, string Role, string? Specialty, string SlackBotId,
    string? DefaultModel, string? DefaultModelReasoningEffort, string InstructionsSha256);

/// <summary>Run単位のAgent設定監査ファイルです。</summary>
public sealed record AgentConfigurationAuditSnapshot(int SchemaVersion, IReadOnlyList<AgentProfileAuditRecord> Profiles);
