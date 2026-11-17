# STDIO MCP要求仕様

> 状態: Phase 2の実装基準。追加承認により、契約Schema・ガイド公開とMCP Tool構成を更新。

## 承認済み要求変更と失敗記録

初期実装では親AIが `run_orchestration(JsonElement contract)` の契約構造を知らず、TaskContractを組み立てられなかった。この失敗は「要件定義不足、ツールを使うための必須実装の考慮漏れ」として記録する。MCP利用に必要なSchemaと作成ガイドの公開が要求から漏れていたため、単一Tool要求を3 Toolへ変更する。ツール一覧に取得手段が存在することと、親AIが実際にガイド・Schemaを参照することは別々に検証する。

## 1. 目的

親AIが作成した既存のAteliers AI Orchestrator契約スキーマを受け取り、インストール済みの固定バージョンRelease CLIに渡す薄いアダプターを提供する。親AIやMCPが実行ごとにリポジトリをclone・buildする必要をなくす。

## 2. 固定前提

- MCP通信方式はSTDIOとする。
- Ateliersの他ツールでWork/CodexからSTDIO MCPを起動できることは検証済みとし、本作業でSTDIO基盤の再検証を行わない。
- 親AIが既存契約スキーマの作成者であり、MCPは独自のタスク契約形式を追加しない。
- MCPが実行するのは、インストーラーで配置された固定バージョンのRelease CLI（`%LOCALAPPDATA%\Ateliers-Dev\Ai-Orchestrator\bin\Ateliers.Ai.Orchestrator.Cli.exe`）である。
- オーケストレーション処理と既存契約の意味解釈はCLI側の責務とする。
- CLIとMCP間は、契約JSONをstdinで受け取り、構造化JSON結果をstdoutへ返す。
- CLIは `--capabilities` で機械可読なプロトコル版を返し、MCPは実行前に対応を確認する。

## 3. 責務

| 主体 | 責務 |
|---|---|
| 親AI | 目的に合った既存契約スキーマを作成し、MCPツールへ渡す。結果を読み、次の判断を行う。 |
| STDIO MCP | 契約を受け取り、インストール済みCLIを解決・起動し、CLIの結果または失敗を呼び出し元へ返す。 |
| Release CLI | 契約を既存仕様に従って検証し、オーケストレーションを実行して結果を出す。 |

## 4. 機能要求

1. MCPは `get_contract_schema`、`get_orchestration_guide`、`run_orchestration` の3 Toolを提供する。実行Toolは維持する。
2. Coreの契約型・既存validator規則に沿ったJSON SchemaをCoreで管理し、CLI `--contract-schema` とMCP `get_contract_schema` で公開する。Schemaに表現できない実在ディレクトリ、included/excluded重複などはガイドで説明し、Core validatorを最終判定とする。SchemaとCore validatorの制約整合をテストで担保する。
3. 契約作成手順、スキーマ外制約、TaskContract v1例、CLI検証・実行方法、エラー対処を含む単一ガイドをCoreで管理し、CLI `--orchestration-guide` とMCP `get_orchestration_guide` から公開する。MCPに固定コピーを持たない。
4. Tool Descriptionは初回ガイド取得と契約生成前Schema取得を親AIへ勧める。参照を強制できるとの前提は置かない。
5. MCPは契約JSONをstdinへ渡し、内容を意味的に再設計・書き換えない。一時契約ファイルは作らない。
6. MCPは固定配置からCLIを解決し、`--capabilities` でプロトコル互換性を確認する。能力応答にSchema・ガイド取得対応を含める。
7. CLI不在、旧CLI、起動失敗、timeout、キャンセル、不正出力は構造化エラーとし、組込みSchema/ガイドへフォールバックしない。
8. CLI machine modeはstdoutに1 JSON応答、診断はstderrへ分離する。応答はprotocol、成否、終了コード、run ID、状態、記録先、機械可読エラーを識別できる。
9. MCPの障害やCLIの異常終了を正常完了として報告しない。呼出し時にclone/buildを要求しない。
10. CLIとMCPは明示的に更新し、互換性を保つ範囲では独立更新する。既存のプロトコル版、終了コード、環境変数allowlist、出力量上限、子プロセスツリー終了を含むキャンセル仕様を維持する。
11. MCPは契約本文、CLI stdout/stderr、引数値をログへ重複記録しない。stderrは排水するが応答へ転載しない。

## 5. 責務境界と初期版の対象外

- MCPは契約の生成者ではないが、Core管理Schemaとガイドを親AIへ提供する。
- MCPに契約スキーマ独自仕様、オーケストレーションロジック、Agent選択・反復ロジック、Git clone/build、PR作成、mergeを持たせない。
- STDIO通信方式自体の技術検証を本Phaseの完了条件に加えない。
- MCP経由の正常完了を、Phase 3の完了条件に加えない。

## 6. 受入条件

- 既存契約スキーマをMCPへ渡せる。
- MCPがインストール済み固定バージョンCLIを選択し、起動できる。
- CLIの結果と失敗状態が構造化JSONと終了コードで呼び出し元へ区別可能な形で返る。stderr診断はstdoutへ混ざらない。
- CLI不在・起動不能・異常終了が成功扱いされない。
- CLIとMCPのバージョンが個別に固定され、更新が明示的に行われる。
- protocol version handshakeで互換性を確認し、互換性がないCLIは起動しない。既存契約インターフェースとの互換性が維持される範囲で、一方の更新を理由に他方の再ビルド・再インストールを要求しない。
- CLIとMCP経由でSchema・ガイドを取得でき、MCPに固定コピーがない。
- Schemaで表現した必須・型・数値・taskId・report format制約がCore validatorと整合する。
- 古いCLI、非対応、不在、起動失敗、不正出力、timeout、キャンセルを構造化エラーとし、Schema/ガイドのフォールバックがない。
- 呼び出し時にソースclone/buildを行わず、インストール済みCLIが利用される。
- MCPに独自契約形式やオーケストレーション実装が追加されていないことを確認する。

## 7. 初期版のプロトコル値

- `run_orchestration` は `contract` JSON objectを必須入力とする。`agent` は `codex` が既定で `fake` をテスト時に指定できる。Codex CLI pathとrun記録先は任意実行設定で、既存CLI引数へ写像する。
- CLI stdin実行は `run --stdin-json`。既存の `run <contract.json>` と他のコマンドを維持する。
- CLI能力応答は `--capabilities`、インストール記録と照合する`cliVersion`、`protocolVersion: 1`、`commands.runStdinJson: true`。
- CLI能力応答には`commands.contractSchema`と`commands.orchestrationGuide`のboolean対応情報を追加する。CLI `--contract-schema` と `--orchestration-guide` はCore管理リソースをstdoutへ公開する。
- CLI成功応答は `protocolVersion`, `ok`, `exitCode`, `runId`, `state`, `runDirectory`, `error` を持つ。失敗応答の `error` は `code` と安全な `message`、入力検証の場合のみ`details`を持つ。
- 終了コード `0` はCLI操作完了、`2` は入力または引数不備、`3` は実行/保存失敗。Workflow state `NeedsHumanInput` はCLI操作が正常に完了した場合はexit `0`で返り、タスク自体が完了したという意味にはしない。
- MCPからのキャンセル時、CLIプロセスを終了した後の永続化・状態記録は保証しない。実行記録が部分的な場合は手動調査が必要。
