using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
internal sealed class OrchestratorTools(CliExecutionService cli)
{
    [McpServerTool(Name = "run_orchestration")]
    [Description("Ateliers AI Orchestratorの契約を実行します。初回は get_orchestration_guide の参照を推奨し、契約を生成する前に get_contract_schema を使うとTaskContract v1に適合しやすくなります。contractはTaskContract v1のJSONオブジェクトです。失敗時も構造化JSONを返します。")]
    public Task<string> RunOrchestrationAsync(
        [Description("既存TaskContractスキーマのJSONオブジェクト。MCPは内容を再設計せずCLIへ渡します。")]
        JsonElement contract,
        [Description("実行Agent。通常はcodex。CLIのFake実行を検証するときだけfakeを指定します.")]
        string agent = "codex",
        [Description("Codex CLIの絶対パス。省略時はATELIERS_CODEX_CLI_PATH環境変数を使います。")]
        string? codexExecutablePath = null,
        [Description("任意の絶対パス。省略時は契約のworkingDirectory下の既定runsディレクトリをCLIが使います。")]
        string? runsDirectory = null,
        CancellationToken cancellationToken = default) =>
        cli.RunAsync(contract, agent, codexExecutablePath, runsDirectory, cancellationToken);

    [McpServerTool(Name = "get_contract_schema")]
    [Description("契約を作成する前に呼び出し、TaskContract v1のJSON Schemaを取得してください。SchemaはCoreのCLIから取得します。CLIが未対応または利用不能なら構造化エラーを返し、組込みSchemaへフォールバックしません。")]
    public Task<string> GetContractSchemaAsync(CancellationToken cancellationToken = default) =>
        cli.GetCliResourceAsync("--contract-schema", "contractSchema", cancellationToken);

    [McpServerTool(Name = "get_orchestration_guide")]
    [Description("初回利用時に呼び出し、TaskContractの作成、CLI validator、実行方法、エラー対処のガイドを取得してください。ガイドはCoreのCLIから取得し、MCPにコピーを持ちません。CLI未対応時は構造化エラーを返します。")]
    public Task<string> GetOrchestrationGuideAsync(CancellationToken cancellationToken = default) =>
        cli.GetCliResourceAsync("--orchestration-guide", "orchestrationGuide", cancellationToken);
}

