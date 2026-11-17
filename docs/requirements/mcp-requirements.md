# STDIO MCP要求仕様

> 状態: Phase 2の実装基準。実装開始指示で明示された責務境界、stdin JSON方式、機密情報とキャンセルの扱いを反映。

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

1. MCPは、既存契約スキーマを入力として受け取る単一の `run_orchestration` ツールを提供する。
2. MCPは契約JSONをstdinへ渡し、内容を意味的に再設計・書き換えない。オーケストレーションへ渡すための一時契約ファイルは作らない。
3. MCPは、CLIインストーラーが定めたインストール先と `install.json` の固定バージョン情報から起動するCLI実体を解決し、`--capabilities` でプロトコル互換性を確認する。
4. インストール済みCLIがない、実体が見つからない、または起動できない場合、CLI処理の成功と誤認させず、明確なエラー結果を返す。
5. CLIのmachine modeはstdoutに1個のJSON応答だけを書き、診断はstderrへ分離する。応答はprotocol version、成功/失敗、CLI終了コード、実行ID、状態、記録先、機械可読エラーを識別できる。
6. MCPの障害やCLIの異常終了を、正常なオーケストレーション完了として報告しない。
7. MCPは、契約や応答のために親側で毎回ソースをclone・buildする処理を要求しない。
8. CLIとMCPはそれぞれ固定バージョンで管理し、更新は各コンポーネントの明示的な操作で行う。既存契約インターフェースとの互換性が維持される場合は独立して更新でき、一方の更新を理由に他方の再ビルドまたは再インストールを要求しない。
9. MCPキャンセルはCancellationTokenをCLIプロセスへ伝播し、終了しない場合は子プロセスツリーを終了する。終了した実行の最終状態はCLIが応答を返さない限り不明として扱い、成功に変換しない。
10. CLIへ渡す環境変数はPATH、OS/ユーザープロファイル、Codex home、明示したCodex CLIパス、HTTP(S) proxyに限る。親プロセスの任意環境変数、APIキー、Tokenを無差別に継承しない。契約本文、stdout/stderrログをMCPログへ複製しない。
11. CLI stdoutの応答上限は64 KiBとし、超過または不正JSONはプロトコルエラーとする。stderrは排水して子プロセスを停止させないが、機密情報を含みうるためMCP応答へ転載しない。

## 5. 責務境界と初期版の対象外

- MCPは契約の生成者ではなく、親AIへ契約作成を委譲する。
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
- 呼び出し時にソースclone/buildを行わず、インストール済みCLIが利用される。
- MCPに独自契約形式やオーケストレーション実装が追加されていないことを確認する。

## 7. 初期版のプロトコル値

- `run_orchestration` は `contract` JSON objectを必須入力とする。`agent` は `codex` が既定で `fake` をテスト時に指定できる。Codex CLI pathとrun記録先は任意実行設定で、既存CLI引数へ写像する。
- CLI stdin実行は `run --stdin-json`。既存の `run <contract.json>` と他のコマンドを維持する。
- CLI能力応答は `--capabilities`、インストール記録と照合する`cliVersion`、`protocolVersion: 1`、`commands.runStdinJson: true`。
- CLI成功応答は `protocolVersion`, `ok`, `exitCode`, `runId`, `state`, `runDirectory`, `error` を持つ。失敗応答の `error` は `code` と安全な `message`、入力検証の場合のみ`details`を持つ。
- 終了コード `0` はCLI操作完了、`2` は入力または引数不備、`3` は実行/保存失敗。Workflow state `NeedsHumanInput` はCLI操作が正常に完了した場合はexit `0`で返り、タスク自体が完了したという意味にはしない。
- MCPからのキャンセル時、CLIプロセスを終了した後の永続化・状態記録は保証しない。実行記録が部分的な場合は手動調査が必要。
