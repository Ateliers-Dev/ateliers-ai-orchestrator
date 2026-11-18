# オーケストレーション契約ガイド

> 作成日: 2026-11-18
> 更新日: 2026-11-18
> 作成者: yuu503

このガイドは `get_orchestration_guide` とCLI `--orchestration-guide` から取得できます。契約を作る前に `get_contract_schema` またはCLI `--contract-schema` でTaskContract v1のJSON Schemaも取得してください。

## 契約を作成する

1. 作業目的、対象範囲、根拠資料と版、要件、受け入れ条件、許可する操作、停止条件、時間・修正・再試行上限を確定します。
2. JSON Schemaの全必須プロパティをcamelCaseで記述します。未知のプロパティは追加できません。
3. `workingDirectory` を実在する作業ディレクトリの完全修飾パスに設定します。JSON Schemaだけでは存在確認できません。
4. `scope.included` は1件以上とし、同一項目を `scope.excluded` にも書かないでください。重複判定はCore validatorが行います。
5. スキーマ取得後に作った契約をCLI validatorで確認してから実行します。Core validatorが最終判定です。

## 例

次の例は既存の `examples/task-contract.json` と同じTaskContract v1形式です。`workingDirectory` は実行環境に存在する完全修飾パスへ置き換えてください。値を置き換えた後にCLIで検証すると、その環境で有効な契約になります。

```json
{
  "schemaVersion": 1,
  "taskId": "sample-task-001",
  "purpose": "変更案を作成し、根拠を報告する",
  "workingDirectory": "C:/work/target-repository",
  "scope": { "included": ["src/"], "excluded": ["production deployment"] },
  "inputs": [{ "description": "作業要件", "reference": "https://example.invalid/task", "revision": "issue revision or commit SHA" }],
  "requirements": ["変更内容を記録する"],
  "acceptanceCriteria": ["変更をテストで確認する"],
  "delegatedOperations": ["対象作業領域のファイルを編集する"],
  "limits": { "maxDurationMinutes": 120, "maxRevisionAttempts": 2, "maxRetries": 2 },
  "stopConditions": ["委譲範囲外の判断が必要になった場合"],
  "reporting": { "pullRequestExpected": true, "finalReportFormat": "markdown" }
}
```

## CLIで検証・実行する

```powershell
Ateliers.Ai.Orchestrator.Cli.exe --contract-schema
Ateliers.Ai.Orchestrator.Cli.exe --orchestration-guide
Ateliers.Ai.Orchestrator.Cli.exe validate .\task-contract.json
Ateliers.Ai.Orchestrator.Cli.exe run .\task-contract.json --agent fake
```

実行時は `--agent codex` を指定し、必要に応じて `--codex-exe` にCodex CLIの完全修飾パスを渡します。MCPでは最初にガイドを取得し、契約の生成前にSchemaを取得してから `run_orchestration` を呼びます。ツールの説明はこの順序を勧めますが、ホストAIがツールを参照すること自体は保証できません。

## エラー対処

- `invalid_contract`: 必須項目、値の型、空文字、未知項目、配列要素を確認します。CLI validatorの詳細は標準エラー出力を確認します。
- `schemaVersion` / `finalReportFormat`: それぞれ `1` / `markdown` を指定します。
- `limits`: `maxDurationMinutes` は1以上、`maxRevisionAttempts` と `maxRetries` は0以上の整数です。
- `workingDirectory`: 現在存在する作業ディレクトリの絶対パスを指定します。
- `scope`: `included` と `excluded` の重複を解消します。
- MCPの `cli_not_installed`、`cli_start_failed`、`invalid_cli_response`、`cli_capabilities_timeout`、`unsupported_cli_protocol`: 対応するCLI Releaseがインストールされ、起動可能かを確認してください。MCPは組込みSchemaやガイドへ切り替えません。
