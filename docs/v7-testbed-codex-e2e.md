# v7 Testbed 実Codex E2E準備

この手順は Ateliers-Dev/ateliers-ai-orchestrator-testbed の単語数課題を使い、本体の現行 schemaVersion: 1 契約とPR必須Codex経路を準備します。v6 Issue #17 one-shot経路とは独立し、v6用契約、スクリプト、実行記録を使いません。

## 目的と公開操作

検証対象は、Testbed main に意図的に残してある TextMetrics.CountWords の空白区切り不具合です。v7は修正ループそのものを必須にせず、独立Reviewerが修正を求めた場合だけ契約上限内で再実行します。

TestbedはPublicリポジトリです。実Codexを開始すると、Codex利用枠または費用が発生する場合があります。成功時には一意なE2E作業ブランチをpushし、公開PRとReviewer結果コメントを作成します。PRは自動マージされません。修正PRは証跡確認後にクローズし、教材の未修正状態を保つため main へマージしてはいけません。

## 使用ファイル

- scripts/v7/task-contract.template.json は現行CLIと同じ契約フィールドのみを使い、reporting.pullRequestExpected を true にします。対象範囲、5つのチェック、独立レビュー、必要な場合のみのbounded修正、停止条件を定義しています。
- scripts/v7/run-codex-e2e.ps1 はv7専用の起動経路です。v6の scripts/issue-17/ は変更せず流用もしません。
- 実行時の作業ディレクトリはGitHubから取得するTestbed main のcleanな一時cloneです。実行直前にGitHubの既定ブランチSHAとcloneのHEADを照合します。本体または利用者の既存checkoutを実行対象にしません。
- PR必須経路の作業ブランチはOrchestratorがrun IDを使って専用作成します。PRのhead/base SHAとReviewer結果コメントが照合されるまで成功扱いにしません。
- Slack通知は常に disabled です。

## 準備確認

Windows PowerShell 5.1、.NET SDK 10、Git for Windows、GitHub CLI (gh) が必要です。PR必須経路では、GitHub CLIが github.com に認証され、TestbedへのpushとPR作成が可能でなければなりません。Codex CLI (codex.exe) は準備確認だけなら不要です。

本体リポジトリルート以外からも実行できます。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<orchestrator-root>\scripts\v7\run-codex-e2e.ps1'
~~~

引数なしでは実Codexを起動しません。Release CLI build、Fake-only Unit tests、GitHub認証とTestbed main のSHA、Testbed一時cloneのclean状態、初期Release build、教材チェックで既知の tabs and newlines と whitespace only の2件だけが失敗すること、および一時生成したv7契約を現行Release CLIで検証します。準備だけの実行では一時cloneを削除します。本体の既存変更は検査対象にせず、stash/resetもしません。

Fake-only Unit testsには、FakeScenario.RevisionThenSuccess を使い、指摘、修正、再レビュー後にPRとReviewer結果コメント証跡を保存する既存テストが含まれています。実CodexにReviewerの修正指摘を強制するための偽指摘や追加変更はありません。

## 実Codex E2E

まず上記の準備確認を成功させます。その後、開始時に再度GitHub状態とTestbed初期状態を確認し、以下を実行します。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<orchestrator-root>\scripts\v7\run-codex-e2e.ps1' -RunCodex
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<orchestrator-root>\scripts\v7\run-codex-e2e.ps1' -RunCodex -CodexExecutable 'C:\Tools\Codex\codex.exe'
~~~

-RunCodex だけでは開始せず、次の確認文を完全一致で入力する必要があります。

    RUN V7 TESTBED E2E WITH PUSH AND PR

この確認は実Codexを開始する指示です。開始後はCodex CLIとモデルを使い、cleanな一時cloneに対して契約を実行します。契約のPR必須経路は、専用ブランチへのcommit/push、固定差分の独立Reviewer確認、指摘があれば上限内の修正・再push・再レビュー、PR作成、head/base SHA検証、Reviewer結果コメントの投稿と再読込照合、NeedsHumanInput での停止を行います。pushやPR作成の権限確認に失敗した場合、成功扱いにはしません。

## 証跡と失敗時

一時clone、実行記録、ライフサイクルログは %TEMP%\AteliersAiOrchestrator\v7\checkout-<一意ID>\ に保存されます。実行記録の github-pull-request.json と github-review-comment.json が共に検証済みで、head/base SHAが一致することをスクリプトが確認します。events.jsonl と report.md も同じrun記録に残します。ログと応答にはタスク内容や診断情報が含まれる可能性があるため、アクセスと保持を管理してください。

Codex開始後の失敗・中断・証跡不一致では一時cloneと実行記録を自動削除しません。push済みブランチや作成済みPRが残った可能性がある場合は、報告された状態とGitHub上のPR・ブランチを人間が確認します。証跡確認前にPRやブランチを削除・クローズせず、PRをマージしないでください。正常終了後も一時cloneと記録は自動削除しません。PRと証跡を確認し、修正PRは main にマージせずクローズしてください。

## Fake経路と実Codexの境界

Fakeテストで確認できるのはOrchestratorの制御フローと証跡形式です。実Codexの判断、認証、push、PR作成、GitHubコメント投稿を証明しません。この準備作業では実Codexを起動していません。実Codex E2Eは上記の明示確認を得た後に別途実行します。
