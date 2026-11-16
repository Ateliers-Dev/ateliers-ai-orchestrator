# Issue #17 Codex E2E 検証

この手順は、Issue #17「Reviewer停止時に停止原因を実行証跡へ記録する」の実Codex検証を、既存のリポジトリから実行します。別のgit checkoutやworktreeは作成しません。

## 前提

- Windows PowerShell 5.1
- .NET SDK 10、Git for Windows、GitHub CLI (`gh`)、Codex CLI (`codex.exe`)
- この検証資材を含む変更が既定ブランチへ取り込まれ、ローカル checkout がGitHubの最新既定ブランチと一致していること
- 実行開始時の作業ツリーがcleanであること
- GitHub CLIが `github.com` に認証済みで、対象リポジトリへのブランチpush、PR作成、PRコメント投稿権限があること

CLIの公開処理は実行時にもclean checkout、既定ブランチの最新SHA、認証・リモート等を検査します。前提を満たさない場合は停止し、作業ツリーの差分を破棄しません。スクリプト自体も実行前に変更・未追跡ファイルを検知して停止します。

## 準備と契約検証

リポジトリルートまたは任意の場所から実行できます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\issue-17\run-codex-e2e.ps1
```

スクリプトはまずclean checkoutを確認し、CLIプロジェクトをReleaseビルドします。ビルド成功後、指定された次のバイナリで、同じフォルダーにある契約テンプレートから一時生成したJSONを検証します。

```text
src\Ateliers.Ai.Orchestrator.Cli\bin\Release\net10.0\Ateliers.Ai.Orchestrator.Cli.exe
```

ここまでで終了します。契約の `workingDirectory` には実行時にリポジトリルートの絶対パスを設定します。テンプレートはCLIへ直接渡す契約ではありません。

## 実Codex E2E

実Codexを起動する場合だけ `-RunCodex` を指定します。Codex実行ファイルは `codex.exe` をPATHから検出するか、必要に応じて絶対パスを渡します。

```powershell
.\scripts\issue-17\run-codex-e2e.ps1 -RunCodex
.\scripts\issue-17\run-codex-e2e.ps1 -RunCodex -CodexExecutable 'C:\Tools\Codex\codex.exe'
```

開始直前に `RUN ISSUE-17 CODEX E2E` の入力を求めます。Codex実行は対象リポジトリの既定ブランチから専用ブランチを作成し、変更をcommit・pushし、PRとReviewer結果コメントを作成します。自動mergeは行いません。実行前にReleaseビルドと契約検証を毎回行います。

実行記録とライフサイクルJSONLは `%TEMP%\AteliersAiOrchestrator\issue-17\` 以下へ保存します。実行ログや応答に認証情報を記録することを意図した処理はありませんが、実行記録にはタスク内容や診断情報が含まれ得るため、アクセスと保持期間を管理してください。Slack通知は無効にします。

## 失敗時

ビルド、契約検証、Git状態確認、依存CLI確認、またはCodex実行の失敗時は、終了コードと短い日本語の診断を表示して停止します。自動でcheckoutを作成したり、既存差分をstash・reset・削除したりしません。Codex実行後に残った変更・ブランチ・PRは、実行証跡とGitHub上の状態を確認してから対応してください。
