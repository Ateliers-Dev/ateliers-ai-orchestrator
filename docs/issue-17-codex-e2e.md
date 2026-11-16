# Issue #17 Codex E2E 検証

この手順は、Issue #17「Reviewer停止時に停止原因を実行証跡へ記録する」のCodex one-shot検証を行います。Releaseビルドと契約検証は既存checkoutで実行し、実Codex E2Eを明示的に開始する場合は `%TEMP%` に作る一時Gitクローンを使います。既存checkoutのブランチとファイルは切り替えたり書き換えたりしません。

## 前提

- Windows PowerShell 5.1
- .NET SDK 10、Git for Windows、GitHub CLI (`gh`)、Codex CLI (`codex.exe`)
- 検証資材を含む変更が既定ブランチへ取り込まれていること
- 実Codex E2Eを実行する場合、ローカルcheckoutがcleanでGitHubの最新既定ブランチと同じSHAであること
- GitHub CLIが `github.com` に認証済みで、対象リポジトリを読み取れること

実行は `-RunCodex` と確認文字列を両方必要とします。契約の `pullRequestExpected` は `false` です。CLIはCodexとReviewerをone-shotで各1回実行し、公開やブランチ作成を行わず `NeedsHumanInput` で停止します。

## 準備と契約検証

次の `<repo-root>` を対象リポジトリの絶対パスに置き換えて実行します。リポジトリルート以外のカレントディレクトリからでも実行できます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<repo-root>\scripts\issue-17\run-codex-e2e.ps1'
```

スクリプトはCLIプロジェクトをReleaseビルドし、指定された次のバイナリで契約テンプレートから一時生成したJSONを検証します。

```text
src\Ateliers.Ai.Orchestrator.Cli\bin\Release\net10.0\Ateliers.Ai.Orchestrator.Cli.exe
```

ここまでで終了します。実Codexは起動せず、一時checkoutも作成しません。通常準備実行はclean作業ツリーを要求せず、ブランチも切り替えません。契約の `workingDirectory` は準備実行時には現在のリポジトリルート、Codex実行時には一時クローンの絶対パスに設定します。テンプレート自体はCLIへ直接渡す契約ではありません。

## 実Codex one-shot検証

実Codexを起動する場合だけ `-RunCodex` を指定します。Codex実行ファイルは `codex.exe` をPATHから検出するか、必要に応じて絶対パスを渡します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<repo-root>\scripts\issue-17\run-codex-e2e.ps1' -RunCodex
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<repo-root>\scripts\issue-17\run-codex-e2e.ps1' -RunCodex -CodexExecutable 'C:\Tools\Codex\codex.exe'
```

開始前に `RUN ISSUE-17 CODEX E2E` の入力を求めます。続行すると `%TEMP%` にローカルGitクローンを作り、そのクローン内でCodexが変更とテストを行い、Reviewerが固定差分を確認します。commit、push、PR作成、レビュー結果コメント投稿は行いません。

CLIのone-shot経路では `NeedsHumanInput` が未完了状態です。スクリプトはCLIの終了コードだけで成功と判断せず、出力された終端状態を確認します。`NeedsHumanInput` または状態を確認できない場合は非0終了し、一時クローンと実行記録を保持してパスを表示します。自動削除はしません。実行証跡を確認し、必要な人間判断を終えた後に一時クローンを手動で削除してください。

実行記録とライフサイクルJSONLは `%TEMP%\AteliersAiOrchestrator\issue-17\` 以下へ保存します。実行ログや応答にはタスク内容や診断情報が含まれ得るため、アクセスと保持期間を管理してください。Slack通知は無効にします。

## 失敗時

ビルド、契約検証、Git状態・既定ブランチ確認、依存CLI確認、クローン準備、Codex実行、終端状態確認の失敗時は、短い日本語の診断を表示して停止します。元のcheckoutの変更はstash・reset・削除しません。実Codex実行後に一時クローンが残った場合は、実行証跡を確認してから対応してください。

## v6 実行結果

2026-11-17 の実行 `e835d3c41e84464588dccc0390f639a2` では、ImplementerとReviewerが各1回完了し、実行イベントと報告が保存されました。CLIは設計上の人間確認地点 `NeedsHumanInput` で停止しました。実行時にtimeoutやkillは発生していません。これはCodexの実行・レビュー受け渡し・証跡保存までの確認であり、Issue #17の修正受入やPR提出までを検証したものではありません。PR公開はこの実行では未実施です。

Reviewerは `CodexCliAgentRunner.cs` の停止理由の優先順位についてP2を報告しました。しかし、保存された提出差分では `stopReason` と `timedOut` が非0終了コード診断より先に判定されており、指摘内容は実際の差分と一致していませんでした。追加回帰テストはReviewer timeoutでkill後に非0終了コードとなる条件を作り、実行証跡にtimeout理由が残ることを確認します。ReviewerのP2は誤検知と評価し、その内容を検証記録に残します。
