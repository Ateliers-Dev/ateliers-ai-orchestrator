using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>実行前に固定するタスクの目的、範囲、入力、上限、報告要件を表します。</summary>
public sealed record TaskContract
{
    /// <summary>契約形式のバージョンです。現在は <c>1</c> のみ受け付けます。</summary>
    public required int SchemaVersion { get; init; }
    /// <summary>実行記録と対応づけるタスク識別子です。</summary>
    public required string TaskId { get; init; }
    /// <summary>タスクで達成する目的です。</summary>
    public required string Purpose { get; init; }
    /// <summary>操作対象となる既存の絶対パスです。</summary>
    public required string WorkingDirectory { get; init; }
    /// <summary>対象に含める範囲と除外する範囲です。</summary>
    public required TaskScope Scope { get; init; }
    /// <summary>契約の根拠として参照する入力資料です。</summary>
    public required IReadOnlyList<TaskInput> Inputs { get; init; }
    /// <summary>満たすべき要件です。</summary>
    public required IReadOnlyList<string> Requirements { get; init; }
    /// <summary>完了を判定するための受け入れ条件です。</summary>
    public required IReadOnlyList<string> AcceptanceCriteria { get; init; }
    /// <summary>委譲された操作の範囲です。</summary>
    public required IReadOnlyList<string> DelegatedOperations { get; init; }
    /// <summary>実行時間、修正、再試行の上限です。</summary>
    public required ExecutionLimits Limits { get; init; }
    /// <summary>人間への確認が必要となる条件です。</summary>
    public required IReadOnlyList<string> StopConditions { get; init; }
    /// <summary>最終報告の形式とPRに関する要件です。</summary>
    public required ReportingRequirements Reporting { get; init; }
}

/// <summary>タスクで許可される対象範囲を表します。</summary>
public sealed record TaskScope
{
    /// <summary>変更・参照対象に含めるパスまたは範囲です。</summary>
    public required IReadOnlyList<string> Included { get; init; }
    /// <summary>含める範囲から除外するパスまたは範囲です。</summary>
    public required IReadOnlyList<string> Excluded { get; init; }
}

/// <summary>タスク契約の入力資料と、その版を特定する情報です。</summary>
public sealed record TaskInput
{
    /// <summary>入力資料の役割を説明します。</summary>
    public required string Description { get; init; }
    /// <summary>入力資料を取得または確認できる参照先です。</summary>
    public required string Reference { get; init; }
    /// <summary>入力資料の版または確認時点です。</summary>
    public required string Revision { get; init; }
}

/// <summary>実行時間と再試行・修正の上限を表します。</summary>
public sealed record ExecutionLimits
{
    /// <summary>実行を許可する最大時間（分）です。</summary>
    public required int MaxDurationMinutes { get; init; }
    /// <summary>レビュー指摘に対して許可する最大修正回数です。</summary>
    public required int MaxRevisionAttempts { get; init; }
    /// <summary>通常失敗に対して許可する最大再試行回数です。</summary>
    public required int MaxRetries { get; init; }
}

/// <summary>最終報告とプルリクエスト提出に関する要件です。</summary>
public sealed record ReportingRequirements
{
    /// <summary>タスク完了時にプルリクエストを期待するかを示します。</summary>
    public required bool PullRequestExpected { get; init; }
    /// <summary>要求する最終報告形式です。現在はMarkdownを受け付けます。</summary>
    public required string FinalReportFormat { get; init; }
}

/// <summary>契約読み込みの結果と、検証で見つかった問題をまとめます。</summary>
/// <param name="Contract">すべての検証に成功した場合の契約です。</param>
/// <param name="Errors">読み込みまたは検証で検出した問題です。</param>
/// <param name="ContentSha256">読み込んだJSONバイト列のSHA-256です。取得できない場合はnullです。</param>
public sealed record ContractReadResult(TaskContract? Contract, IReadOnlyList<string> Errors, string? ContentSha256)
{
    /// <summary>契約が存在し、検証エラーがない場合にtrueを返します。</summary>
    public bool IsValid => Contract is not null && Errors.Count == 0;
}

/// <summary>JSONタスク契約を読み込み、バージョンと必須条件を検証します。</summary>
public static class TaskContractReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>指定ファイルを一度読み込み、その内容のハッシュと検証結果を返します。</summary>
    /// <param name="path">読み込むJSON契約ファイルのパスです。</param>
    /// <returns>契約、検証エラー、読み込んだ内容のハッシュを含む結果です。</returns>
    public static ContractReadResult Read(string path)
    {
        if (!File.Exists(path))
        {
            return new(null, [$"契約ファイルが見つかりません: {path}"], null);
        }

        try
        {
            var content = File.ReadAllBytes(path);
            var contract = JsonSerializer.Deserialize<TaskContract>(content, JsonOptions);
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            if (contract is null)
            {
                return new(null, ["契約JSONが空です。"], hash);
            }

            var errors = Validate(contract);
            return errors.Count == 0 ? new(contract, errors, hash) : new(null, errors, hash);
        }
        catch (JsonException exception)
        {
            return new(null, [$"契約JSONを読み取れません: {exception.Message}"], null);
        }
        catch (IOException exception)
        {
            return new(null, [$"契約ファイルを読み取れません: {exception.Message}"], null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, ["契約ファイルを読み取る権限がありません。"], null);
        }
    }

    /// <summary>契約の必須値、サポート対象バージョン、範囲と上限の整合性を検証します。</summary>
    /// <param name="contract">検証する契約です。</param>
    /// <returns>検出した問題の一覧です。問題がなければ空です。</returns>
    public static IReadOnlyList<string> Validate(TaskContract contract)
    {
        var errors = new List<string>();
        if (contract.SchemaVersion != 1) errors.Add("schemaVersion は 1 である必要があります。");
        RequireText(contract.TaskId, "taskId", errors);
        if (!string.IsNullOrWhiteSpace(contract.TaskId) && !Regex.IsMatch(contract.TaskId, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant))
            errors.Add("taskId は英数字で始まり、英数字・ピリオド・アンダースコア・コロン・ハイフンのみで指定してください。");
        RequireText(contract.Purpose, "purpose", errors);
        if (string.IsNullOrWhiteSpace(contract.WorkingDirectory) || !Path.IsPathFullyQualified(contract.WorkingDirectory))
            errors.Add("workingDirectory は絶対パスである必要があります。");
        else if (!Directory.Exists(contract.WorkingDirectory))
            errors.Add("workingDirectory に指定した作業ディレクトリが存在しません。");

        if (contract.Scope is null)
            errors.Add("scope が必要です。");
        else
        {
            RequireItems(contract.Scope.Included, "scope.included", errors, atLeastOne: true);
            RequireItems(contract.Scope.Excluded, "scope.excluded", errors);
            if (contract.Scope.Included is not null && contract.Scope.Excluded is not null)
            {
                var excluded = new HashSet<string>(contract.Scope.Excluded, StringComparer.OrdinalIgnoreCase);
                foreach (var item in contract.Scope.Included.Where(excluded.Contains))
                    errors.Add($"scope の同じ項目を含める範囲と除外範囲の両方に指定しています: {item}");
            }
        }

        if (contract.Inputs is null) errors.Add("inputs が必要です。");
        else
        {
            for (var index = 0; index < contract.Inputs.Count; index++)
            {
                var input = contract.Inputs[index];
                if (input is null)
                {
                    errors.Add($"inputs[{index}] が null です。");
                    continue;
                }
                RequireText(input.Description, $"inputs[{index}].description", errors);
                RequireText(input.Reference, $"inputs[{index}].reference", errors);
                RequireText(input.Revision, $"inputs[{index}].revision", errors);
            }
        }

        RequireItems(contract.Requirements, "requirements", errors, atLeastOne: true);
        RequireItems(contract.AcceptanceCriteria, "acceptanceCriteria", errors, atLeastOne: true);
        RequireItems(contract.DelegatedOperations, "delegatedOperations", errors);
        RequireItems(contract.StopConditions, "stopConditions", errors, atLeastOne: true);

        if (contract.Limits is null)
            errors.Add("limits が必要です。");
        else
        {
            if (contract.Limits.MaxDurationMinutes <= 0) errors.Add("limits.maxDurationMinutes は正の整数である必要があります。");
            if (contract.Limits.MaxRevisionAttempts < 0) errors.Add("limits.maxRevisionAttempts は 0 以上である必要があります。");
            if (contract.Limits.MaxRetries < 0) errors.Add("limits.maxRetries は 0 以上である必要があります。");
        }

        if (contract.Reporting is null)
            errors.Add("reporting が必要です。");
        else
        {
            RequireText(contract.Reporting.FinalReportFormat, "reporting.finalReportFormat", errors);
            if (!string.Equals(contract.Reporting.FinalReportFormat, "markdown", StringComparison.OrdinalIgnoreCase))
                errors.Add("reporting.finalReportFormat は markdown を指定してください。");
        }

        return errors;
    }

    private static void RequireItems(IReadOnlyList<string>? values, string field, ICollection<string> errors, bool atLeastOne = false)
    {
        if (values is null)
        {
            errors.Add($"{field} が必要です。");
            return;
        }
        if (atLeastOne && values.Count == 0) errors.Add($"{field} に少なくとも1件必要です。");
        for (var index = 0; index < values.Count; index++)
            if (string.IsNullOrWhiteSpace(values[index])) errors.Add($"{field}[{index}] を空にできません。");
    }

    private static void RequireText(string? value, string field, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{field} を空にできません。");
    }
}
