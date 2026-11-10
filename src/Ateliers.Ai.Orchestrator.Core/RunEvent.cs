using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ateliers.Ai.Orchestrator.Core;

/// <summary>JSONL実行記録に保存する一件のイベントを表します。</summary>
public sealed record RunEvent
{
    /// <summary>このイベントが属する実行の識別子です。</summary>
    public required string RunId { get; init; }
    /// <summary>実行内で単調に増加するイベント番号です。</summary>
    public required long Sequence { get; init; }
    /// <summary>イベントを記録したUTC時刻です。</summary>
    public required DateTimeOffset TimestampUtc { get; init; }
    /// <summary>イベントを発生させた役割です。</summary>
    public required string Role { get; init; }
    /// <summary>発生した操作または状態変化の種別です。</summary>
    public required string EventType { get; init; }
    /// <summary>イベントの結果を表す短い状態値です。</summary>
    public required string Result { get; init; }
    /// <summary>遷移前の状態です。状態遷移以外ではnullです。</summary>
    public RunState? FromState { get; init; }
    /// <summary>遷移後の状態です。状態遷移以外ではnullです。</summary>
    public RunState? ToState { get; init; }
    /// <summary>入力資料または要求を特定する参照です。</summary>
    public string? InputReference { get; init; }
    /// <summary>出力資料または応答を特定する参照です。</summary>
    public string? OutputReference { get; init; }
    /// <summary>対象となるGitコミットを取得できた場合のSHAです。</summary>
    public string? TargetCommitSha { get; init; }
    /// <summary>レビュー対象差分のSHA-256です。</summary>
    public string? DiffSha256 { get; init; }
    /// <summary>処理開始からの経過時間（ミリ秒）です。</summary>
    public long? ElapsedMilliseconds { get; init; }
    /// <summary>イベントの補足情報です。</summary>
    public string? Detail { get; init; }
    /// <summary>失敗時のエラー情報です。</summary>
    public string? Error { get; init; }
    /// <summary>この実行では取得できていない情報です。</summary>
    public IReadOnlyList<string> UnknownInformation { get; init; } = [];
}

/// <summary>実行イベントを永続化するための差し替え可能な境界です。</summary>
public interface IRunEventWriter
{
    /// <summary>一件の実行イベントを永続化します。</summary>
    /// <param name="eventType">イベント種別です。</param>
    /// <param name="result">イベント結果です。</param>
    /// <param name="from">遷移前の状態です。</param>
    /// <param name="to">遷移後の状態です。</param>
    /// <param name="detail">補足情報です。</param>
    /// <param name="error">失敗理由です。</param>
    /// <param name="inputReference">入力の保存先または識別子です。</param>
    /// <param name="outputReference">出力の保存先または識別子です。</param>
    /// <param name="targetCommitSha">取得済みの場合の対象コミットSHAです。</param>
    /// <param name="diffSha256">対象差分のSHA-256です。</param>
    /// <param name="elapsedMilliseconds">経過時間です。</param>
    /// <param name="unknownInformation">未取得情報です。</param>
    /// <param name="role">記録上のイベント発生役割です。</param>
    /// <returns>永続化したイベントです。</returns>
    RunEvent Append(string eventType, string result, RunState? from = null, RunState? to = null,
        string? detail = null, string? error = null, string? inputReference = null,
        string? outputReference = null, string? targetCommitSha = null, string? diffSha256 = null,
        long? elapsedMilliseconds = null, IReadOnlyList<string>? unknownInformation = null,
        string role = "Orchestrator");
}

/// <summary>新規JSONLイベント記録を作成し、順序付きイベントをディスクへ追記します。</summary>
public sealed class JsonlRunEventStore : IRunEventWriter, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly FileStream stream;
    private readonly string runId;
    private long sequence;

    private JsonlRunEventStore(string path, string runId)
    {
        this.runId = runId;
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    /// <summary>既存ファイルを上書きせず、新しい実行記録を作成します。</summary>
    /// <param name="path">作成するJSONLファイルのパスです。</param>
    /// <param name="runId">イベントに付与する実行識別子です。</param>
    /// <returns>イベントを追記するストアです。</returns>
    /// <exception cref="IOException">同じパスにファイルが既に存在する場合、または作成できない場合に発生します。</exception>
    public static JsonlRunEventStore CreateNew(string path, string runId) => new(path, runId);

    /// <summary>一件のイベントを連番・UTC時刻付きで追記し、ディスクへフラッシュします。</summary>
    /// <param name="eventType">記録する操作または状態変化の種別です。</param>
    /// <param name="result">処理結果です。</param>
    /// <param name="from">遷移前の状態です。</param>
    /// <param name="to">遷移後の状態です。</param>
    /// <param name="detail">イベントの補足情報です。</param>
    /// <param name="error">失敗時の説明です。</param>
    /// <param name="inputReference">入力を特定する参照です。</param>
    /// <param name="outputReference">出力を特定する参照です。</param>
    /// <param name="targetCommitSha">取得した場合の対象コミットSHAです。</param>
    /// <param name="diffSha256">対象差分のSHA-256です。</param>
    /// <param name="elapsedMilliseconds">処理にかかった時間です。</param>
    /// <param name="unknownInformation">取得できなかった情報です。</param>
    /// <param name="role">イベントを発生させた役割です。</param>
    /// <returns>追記したイベントです。</returns>
    /// <exception cref="IOException">記録の追記またはディスクへのフラッシュに失敗した場合に発生します。</exception>
    public RunEvent Append(
        string eventType,
        string result,
        RunState? from = null,
        RunState? to = null,
        string? detail = null,
        string? error = null,
        string? inputReference = null,
        string? outputReference = null,
        string? targetCommitSha = null,
        string? diffSha256 = null,
        long? elapsedMilliseconds = null,
        IReadOnlyList<string>? unknownInformation = null,
        string role = "Orchestrator")
    {
        var nextSequence = sequence + 1;
        var item = new RunEvent
        {
            RunId = runId,
            Sequence = nextSequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            Role = role,
            EventType = eventType,
            Result = result,
            FromState = from,
            ToState = to,
            InputReference = inputReference,
            OutputReference = outputReference,
            TargetCommitSha = targetCommitSha,
            DiffSha256 = diffSha256,
            ElapsedMilliseconds = elapsedMilliseconds,
            Detail = detail,
            Error = error,
            UnknownInformation = unknownInformation ?? []
        };
        var line = JsonSerializer.Serialize(item, JsonOptions) + "\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        sequence = nextSequence;
        return item;
    }

    /// <summary>イベントファイルを閉じます。</summary>
    public void Dispose() => stream.Dispose();
}

/// <summary>確認待ち状態へ進むための、役割別に検証可能な完了証跡です。</summary>
/// <param name="ArtifactSha256">レビューした固定成果物のSHA-256です。</param>
/// <param name="ImplementerId">実装担当の識別子です。</param>
/// <param name="ReviewerId">実装担当と異なるレビュー担当の識別子です。</param>
/// <param name="ReviewEvidenceReference">独立レビュー記録の参照です。</param>
/// <param name="AcceptanceCriteriaSha256">契約の受け入れ条件一覧のSHA-256です。</param>
/// <param name="AcceptanceCriteriaCount">検証した受け入れ条件の件数です。</param>
/// <param name="PassedAcceptanceCriteriaCount">合格した受け入れ条件の件数です。</param>
/// <param name="AcceptanceEvidenceReference">受け入れ検証記録の参照です。</param>
/// <param name="PullRequestUrl">GitHub上の提出済みPR URLです。</param>
/// <param name="PullRequestOperationId">PR作成操作の外部識別子です。</param>
/// <param name="PullRequestOperationRecordReference">PR作成操作を裏づける記録の参照です。</param>
public sealed record HumanReviewEvidence(
    string ArtifactSha256,
    string ImplementerId,
    string ReviewerId,
    string ReviewEvidenceReference,
    string AcceptanceCriteriaSha256,
    int AcceptanceCriteriaCount,
    int PassedAcceptanceCriteriaCount,
    string AcceptanceEvidenceReference,
    string PullRequestUrl,
    string PullRequestOperationId,
    string PullRequestOperationRecordReference);

/// <summary>現在状態を保持し、記録に成功した場合だけ状態遷移を適用します。</summary>
public sealed class RunExecution(IRunEventWriter eventStore, TaskContract? contract = null)
{
    /// <summary>現在の実行状態です。</summary>
    public RunState State { get; private set; } = RunState.Prepared;

    /// <summary>永続化に失敗した場合に、追加処理を止めるため状態だけを人間判断待ちへ固定します。</summary>
    public void StopInMemoryAfterPersistenceFailure() => State = RunState.NeedsHumanInput;

    /// <summary>許可遷移を記録してから実行状態を更新します。</summary>
    /// <param name="target">遷移先の状態です。</param>
    /// <exception cref="InvalidRunTransitionException">遷移が許可されていない場合に発生します。</exception>
    /// <exception cref="IOException">遷移イベントを永続化できなかった場合、状態は変更されずに発生します。</exception>
    public void Transition(RunState target)
    {
        if (target == RunState.ReadyForHumanReview)
            throw new InvalidRunTransitionException("ReadyForHumanReview には完了証跡を指定する必要があります。");
        RunStateMachine.EnsureAllowed(State, target);
        var previous = State;
        eventStore.Append("state_transition", "accepted", previous, target);
        State = target;
    }

    /// <summary>独立レビュー・受け入れ検証・提出済みPRの証跡が揃った場合だけ人間確認待ちへ進みます。</summary>
    /// <param name="evidence">検証する完了証跡です。</param>
    /// <exception cref="InvalidRunTransitionException">現在状態がReviewingでない、または必須証跡に不足・矛盾がある場合に発生します。</exception>
    /// <exception cref="IOException">状態遷移イベントを永続化できなかった場合に発生します。状態は変更されません。</exception>
    public void CompleteForHumanReview(HumanReviewEvidence evidence)
    {
        if (State != RunState.Reviewing)
            throw new InvalidRunTransitionException("人間確認待ちへの遷移はレビュー中に限り許可されます。");
        var expectedCriteriaHash = contract is null ? null : Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", contract.AcceptanceCriteria))));
        if (!IsSha256(evidence.ArtifactSha256) || string.IsNullOrWhiteSpace(evidence.ImplementerId) ||
            string.IsNullOrWhiteSpace(evidence.ReviewerId) ||
            string.Equals(evidence.ReviewerId, evidence.ImplementerId, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256(evidence.AcceptanceCriteriaSha256) ||
            contract is null || evidence.AcceptanceCriteriaCount != contract.AcceptanceCriteria.Count ||
            evidence.PassedAcceptanceCriteriaCount != contract.AcceptanceCriteria.Count ||
            !evidence.AcceptanceCriteriaSha256.Equals(expectedCriteriaHash, StringComparison.OrdinalIgnoreCase) ||
            evidence.AcceptanceCriteriaCount <= 0 ||
            string.IsNullOrWhiteSpace(evidence.PullRequestOperationId) ||
            !Uri.TryCreate(evidence.PullRequestUrl, UriKind.Absolute, out var pullRequestUri) ||
            pullRequestUri.Scheme != Uri.UriSchemeHttps || pullRequestUri.Host != "github.com" ||
            !System.Text.RegularExpressions.Regex.IsMatch(pullRequestUri.AbsolutePath, @"^/[^/]+/[^/]+/pull/\d+/?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidRunTransitionException("独立レビュー、全受け入れ条件、GitHub PR作成操作の各証跡が必要です。");

        var criteriaCount = evidence.AcceptanceCriteriaCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var passedCount = evidence.PassedAcceptanceCriteriaCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reviewRecordHash = ReadVerifiedRecordHash(evidence.ReviewEvidenceReference, "independent-review",
            ("artifactSha256", evidence.ArtifactSha256), ("reviewerId", evidence.ReviewerId),
            ("implementerId", evidence.ImplementerId), ("reviewInputArtifactSha256", evidence.ArtifactSha256),
            ("selfAssessmentExcluded", "true"), ("verdict", "approve"));
        var acceptanceRecordHash = ReadVerifiedRecordHash(evidence.AcceptanceEvidenceReference, "acceptance-verification",
            ("criteriaSha256", evidence.AcceptanceCriteriaSha256), ("criteriaCount", criteriaCount), ("passedCount", passedCount));
        var pullRequestRecordHash = ReadVerifiedRecordHash(evidence.PullRequestOperationRecordReference, "github-pull-request-created",
            ("url", evidence.PullRequestUrl), ("operationId", evidence.PullRequestOperationId));
        if (reviewRecordHash is null || acceptanceRecordHash is null || pullRequestRecordHash is null)
            throw new InvalidRunTransitionException("独立レビュー、全受け入れ条件、GitHub PR作成操作の記録を照合できません。");

        eventStore.Append("state_transition", "accepted", State, RunState.ReadyForHumanReview,
            detail: $"独立レビュー記録 {evidence.ReviewEvidenceReference} sha256:{reviewRecordHash}、受け入れ検証記録 {evidence.AcceptanceEvidenceReference} sha256:{acceptanceRecordHash}、PR作成操作記録 {evidence.PullRequestOperationRecordReference} sha256:{pullRequestRecordHash}（operationId={evidence.PullRequestOperationId}）を照合しました。PR: {evidence.PullRequestUrl}。人間のマージは未実施です。",
            inputReference: evidence.ReviewEvidenceReference,
            outputReference: evidence.PullRequestOperationRecordReference,
            diffSha256: evidence.ArtifactSha256,
            role: "Orchestrator");
        State = RunState.ReadyForHumanReview;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string? ReadVerifiedRecordHash(string? reference, string kind, params (string Name, string Value)[] fields)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            var bytes = File.ReadAllBytes(reference);
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("evidenceKind", out var kindElement) || kindElement.GetString() != kind) return null;
            var matches = fields.All(field => root.TryGetProperty(field.Name, out var value) &&
                string.Equals(value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : value.ToString(),
                    field.Value, StringComparison.OrdinalIgnoreCase));
            return matches ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) : null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
