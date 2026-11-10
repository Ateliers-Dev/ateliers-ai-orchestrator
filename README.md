# Ateliers AI オーケストレーター

.NET 10 のローカルCLIです。バージョン付きJSON契約の検証、状態遷移モデル、JSONL実行記録に加え、Fake AgentまたはCodex CLI子プロセスによるワークフローを実行できます。GitHubへの書込とmergeは行いません。


## 今回のPoC完了地点

今回のPoCの完了地点は、**Codex Implementer → Codex Reviewer → 結果集約 → 停止**です。実行結果とレビュー結果を記録した後、人間の判断待ちで止まります。

レビュー指摘に応じた自動修正・再レビューの反復や、本格的な運用管理は今回の完了条件に含めず、後続フェーズで扱います。現行コードにはFakeとCLIの反復処理がありますが、この反復経路は今回の受入対象ではありません。実Codex CLIによる受入も未実施であり、実証済み・運用可能とはみなしません。

## 必要環境

- .NET SDK 10

追加NuGetパッケージはありません。

## 使い方

契約を検証します。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- validate examples/task-contract.json
```

契約を検証して実行準備記録を作成します。`workingDirectory` は既存の絶対パスを指定してください。Agentは起動しません。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- start examples/task-contract.json --runs ./.ateliers-orchestrator/runs
```

出力先に実行IDのディレクトリを新規作成し、`events.jsonl` と `report.md` を保存します。既存ディレクトリや既存イベントを上書きしません。契約本文は実行記録にコピーせず、SHA-256だけをレポートに記録します。

Fake Agentの縦断フローを実行します。シナリオは `Success`、`RevisionThenSuccess`、`Stop`、`ImplementerFailure`、`ReviewerFailure`、`EmptyResponse`、`ReviewerEmptyResponse`、`Timeout`、`ReviewerTimeout` から選べます。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- run examples/task-contract.json --scenario RevisionThenSuccess --runs ./.ateliers-orchestrator/runs
```

**安全ゲート完了と追加の明示承認までは、実Codex CLIを起動しないでください。--help と --version も起動に含みます。** 下記コマンドは仕様例であり、現時点での実行手順ではありません。

Codex CLI経路は、ImplementerとReviewerに**別々の使い捨てCODEX_HOME**を指定する設計です。既定のユーザープロファイル、`--ephemeral`、既存認証情報のコピーは使いません。各プロファイルは事前に作成し、専用ディレクトリ内へ次の確認ファイルを置く必要があります。

```json
{"disposable":true}
```

確認ファイルの存在は、プロファイルの使い捨て性をプログラムが保証するものではありません。作業者が専用パスであることを確認し、そのプロファイルを使う準備を済ませてください。Codex CLIが書く状態やログはそのディレクトリ内に残ります。認証を行う場合も、通常利用中のプロファイルから認証情報をコピーせず、使い捨てプロファイルごとに用意してください。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- run examples/task-contract.json `
  --agent codex `
  --codex-exe "C:/path/to/codex.exe" `
  --implementer-codex-home "C:/scratch/codex-implementer" `
  --reviewer-codex-home "C:/scratch/codex-reviewer" `
  --runs ./.ateliers-orchestrator/runs
```

Implementerは契約の作業領域で `workspace-write`、ReviewerはGit差分を含む固定成果物だけを別の一時作業領域へ受け取り `read-only` で実行します。子プロセスにはAPI/GitHub秘密値を親環境から渡さず、ImplementerとReviewerで別々の新規 `codex exec` を起動します。`--ask-for-approval never` を指定するため、非対話run中に承認待ち画面は出ません。通常の変更はworkspace sandboxで制限し、拒否された操作はAgent失敗として記録します。子Agent/再委譲を無効化できることは実証していないため、完全に記録できたとは扱いません。timeoutとCtrl+C停止要求を区別し、kill結果、終了確認、stream切断を分けて保存します。killはCodex turn中断の保証ではありません。Unattended `codex exec` の例は[OpenAI Cookbook](https://developers.openai.com/cookbook/examples/codex/build_iterative_repair_loops_with_codex)を参照しています。

実行ごとにCLI JSONL、stderr、最終応答、process/thread識別子、terminal event、終了コード、取得できたtoken usageを保存します。stderr警告単独では成功・失敗を判定しません。token数は実費・請求額・硬い予算上限ではありません。生ログと応答は秘密情報を含む可能性があるため、実行ディレクトリのアクセスと保持を管理してください。GitHub push・PR作成・mergeはこのコマンドから行わず、人間が最終PR判断を行います。

Fake Agentは実行ごとに同じ入力へ同じ応答を返します。成果物は版ごとに別ファイルへ保存し、保存後の同じバイト列のSHA-256をReviewer要求とイベントに記録します。役割別の要求・応答、試行数、状態遷移、最終報告は実行ディレクトリへ保存されます。初回Reviewer入力にはImplementerの自己評価を含めません。

## 契約

`examples/task-contract.json` が最小例です。実行前に `workingDirectory` を実在する作業ディレクトリへ書き換えてください。`schemaVersion` は `1` に固定し、未知項目、必須値の欠落、含める範囲と除外範囲の重複、不正または存在しない作業ディレクトリを拒否します。操作範囲はタスク単位で定義でき、許可操作を細かく分割するルールは設けません。

## 状態と実行記録

状態は `Prepared → Implementing → Reviewing → Revising → Implementing ...` の流れと、`ReadyForHumanReview`、`NeedsHumanInput`、`Failed` の終端状態を定義しています。禁止遷移は拒否されます。`ReadyForHumanReview` への遷移には、固定成果物ハッシュと一致する独立レビュー記録、タスクの全受け入れ条件の検証記録、GitHub PR URLと作成操作記録が必要です。3種類の記録ファイルを読み、種別と内容を照合します。Fakeモードは実PRや受け入れ結果を作らないため、証跡が別途与えられない限り `NeedsHumanInput` で停止します。Agentの自己申告だけで遷移する経路はありません。人間によるマージ状態はモデルに含まず、CLIからマージする機能もありません。

`--review-evidence` を使う場合は、`HumanReviewEvidence` のJSONと、それが参照する3記録を指定します。レビュー記録は `evidenceKind=independent-review`、対象成果物・レビュー入力のハッシュ、異なる実装・レビュー担当ID、`selfAssessmentExcluded=true`、`verdict=approve` を含めます。受け入れ記録は `evidenceKind=acceptance-verification`、契約の受け入れ条件一覧ハッシュ、全条件数と合格数を含めます。PR操作記録は `evidenceKind=github-pull-request-created`、実PR URL、操作IDを含めます。CLIは記録内容と相互参照を検査して、参照先と記録ファイルのSHA-256を実行イベントへ残します。タイムアウトシナリオはFake応答による再現です。記録の発行元を暗号学的に認証する仕組みはなく、実GitHub操作を行う機能でもありません。

JSONLには実行ID、連番、UTC時刻、役割、イベント種別、状態、入出力参照、対象コミット／成果物ハッシュ、経過時間、結果、エラー、未取得情報の欄を持たせます。Fake呼び出しの要求・応答は実行ごとの参照ファイルへ保存します。取得していない値は推測せず未取得として明示します。ローカルJSONLは耐改ざん・完全監査の台帳ではありません。

## テスト

依存パッケージを使わない自動テストハーネスを実行します。

```powershell
dotnet run --project tests/Ateliers.Ai.Orchestrator.Tests
```

ソリューション全体のビルドは次の通りです。

```powershell
dotnet build Ateliers.Ai.Orchestrator.sln
```
