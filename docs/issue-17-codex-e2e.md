# Issue #17 Codex E2E 検証

この手順は、Issue #17「Reviewer停止時に停止原因を実行証跡へ記録する」の実Codex検証を行います。Releaseビルドと契約検証は既存checkoutで実行し、実Codex E2Eを明示的に開始する場合は `%TEMP%` に作る一時Gitクローンを使用します。既存checkoutのブランチとファイルは切り替えたり書き換えたりしません。

## 前提

- Windows PowerShell 5.1
- .NET SDK 10、Git for Windows、GitHub CLI (`gh`)、Codex CLI (`codex.exe`)
- 検証資材を含む変更が既定ブランチへ取り込まれていること
- 実Codex E2Eを実行する場合、ローカルcheckoutがcleanでGitHubの最新既定ブランチと同じSHAであること
- GitHub CLIが `github.com` に認証済みで、対象リポジトリへのブランチpush、PR作成、PRコメント投稿権限があること

実Codex実行は明示的な `-RunCodex` と確認文字列を両方必要とします。一時Gitクローンの作成と専用ブランチの切り替えは、この確認後に限って実施します。通常の準備実行ではcheckoutを作成せず、ブランチも切り替えません。

## 準備と契約検証

次の `<repo-root>` を対象リポジトリの絶対パスに置き換えて実行します。リポジトリルート以外のカレントディレクトリからでも実行できます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<repo-root>\scripts\issue-17\run-codex-e2e.ps1'
```

スクリプトはCLIプロジェクトをReleaseビルドし、指定された次のバイナリで契約テンプレートから一時生成したJSONを検証します。

```text
src\Ateliers.Ai.Orchestrator.Cli\bin\Release\net10.0\Ateliers.Ai.Orchestrator.Cli.exe
```

ここまでで終了します。実Codexは起動せず、一時checkoutも作成しません。契約の `workingDirectory` は準備実行時には現在のリポジトリルート、Codex実行時には一時クローンの絶対パスに設定します。契約テンプレート自体はCLIへ直接渡す契約ではありません。

## 実Codex E2E

実Codexを起動する場合だけ `-RunCodex` を指定します。Codex実行ファイルは `codex.exe` をPATHから検出するか、必要に応じて絶対パスを渡します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<repo-root>\scripts\issue-17\run-codex-e2e.ps1' -RunCodex
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<repo-root>\scripts\issue-17\run-codex-e2e.ps1' -RunCodex -CodexExecutable 'C:\Tools\Codex\codex.exe'
```

開始直前に `RUN ISSUE-17 CODEX E2E` の入力を求めます。続行すると `%TEMP%` にローカルGitクローンを作り、そのクローン内で専用ブランチ作成、Codex編集、commit、push、PR、Reviewerコメント投稿を行います。元のcheckoutは参照元として使い、ブランチ・作業ファイルを変更しません。自動mergeは行いません。

GitHub公開と実行記録・ライフサイクルJSONLは `%TEMP%\AteliersAiOrchestrator\issue-17\` 以下を使います。正常終了時は一時クローンを削除します。失敗時は調査できるよう一時クローンを残し、そのパスを表示します。実行ログや応答にはタスク内容や診断情報が含まれ得るため、アクセスと保持期間を管理してください。Slack通知は無効にします。

## 失敗時

ビルド、契約検証、Git状態・既定ブランチ確認、依存CLI確認、クローン準備、またはCodex実行の失敗時は、終了コードと短い日本語の診断を表示して停止します。元のcheckoutの変更はstash・reset・削除しません。Codex実行後に一時クローンが残った場合は、実行証跡とGitHub上の公開状態を確認してから対応してください。