using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
internal sealed class OrchestratorTools(CliExecutionService cli)
{
    [McpServerTool(Name = "run_orchestration")]
    [Description("既存Ateliers AI Orchestrator契約を、インストール済みRelease CLIへ渡して実行します。contractは既存契約スキーマそのものです。契約の生成や意味解釈は行いません。失敗時もJSON結果を返します。")]
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
}
