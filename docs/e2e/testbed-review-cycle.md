# Testbedレビューサイクル

このシナリオは Ateliers-Dev/ateliers-ai-orchestrator-testbed の単語数課題を使い、現行schemaVersion 1の契約とPR必須Codex経路を検証します。実行基盤の共通手順は [E2E実行基盤](README.md) を参照してください。

## 目的と公開操作

Testbed `main` に意図的に残してある `TextMetrics.CountWords` の空白区切り不具合を対象とします。独立Reviewerが修正を求めた場合だけ、契約上限内で修正・再レビューします。

TestbedはPublicリポジトリです。実Codexを開始するとCodex利用枠または費用が発生する場合があります。成功時には一意なE2E作業ブランチをpushし、公開PRとReviewer結果コメントを作成します。PRは自動マージされません。修正PRは証跡確認後にクローズし、教材の未修正状態を保つため `main` へマージしてはいけません。

## シナリオ契約と前提確認

- `scripts/e2e/scenarios/testbed-review-cycle.json` は現行CLIと同じ契約フィールドを使い、`reporting.pullRequestExpected` を `true` にします。対象範囲、5つのチェック、独立レビュー、必要な場合のみのbounded修正、停止条件を定義します。
- 実行対象はGitHubから取得するTestbed `main` のcleanな一時cloneです。実行直前にGitHubの既定ブランチSHAとcloneのHEADを照合します。本体リポジトリや利用者の既存checkoutを対象にしません。
- PR必須経路はrun IDを使う一意な作業ブランチを作成し、push、PR作成、Reviewer結果コメント投稿・照合へ進みます。
- Slack通知は常に `disabled` です。

[E2E実行基盤の準備確認](README.md#準備確認) を実行します。共通の準備確認は本体Release build、Fake-only Unit tests、GitHub認証とTestbed `main` のSHA、cleanな一時clone、生成した契約の現行Release CLI検証を行います。準備確認だけの実行では一時cloneを削除します。

Testbedの課題が想定どおりの初期状態か確認する場合は、別の一時cloneで `tests/WordCounter.Tests/WordCounter.Tests.csproj` をRelease buildし、チェックを実行します。`tabs and newlines` と `whitespace only` の2件だけが失敗し、`ordinary spaces`、`repeated spaces`、`empty input` が成功することを確認してください。対象のcloneはTestbed `main` から作り、確認後に削除します。

Fake-only Unit testsには、FakeScenario.RevisionThenSuccessを使い、指摘、修正、再レビュー後にPRとReviewer結果コメント証跡を保存する既存テストが含まれています。実CodexにReviewerの修正指摘を強制する偽指摘や追加変更はありません。

## 実Codex E2E

準備確認を成功させた後、 [E2E実行基盤の実行手順](README.md#実codex実行) に従って実行します。確認文は次のとおりです。

    RUN CODEX E2E FOR ATELIERS-DEV/ATELIERS-AI-ORCHESTRATOR-TESTBED WITH PUSH AND PR

実行ではCodex CLIとモデルを使い、cleanな一時cloneに対して契約を実行します。契約のPR必須経路は、専用ブランチへのcommit/push、固定差分の独立Reviewer確認、必要な場合のみの上限内修正と再レビュー、PR作成、head/base SHA検証、Reviewer結果コメントの投稿と再読込照合、NeedsHumanInputでの停止を行います。pushやPR作成に失敗した場合、成功扱いにはしません。

## 証跡と失敗時

一時clone、実行記録、ライフサイクルログは `%TEMP%\AteliersAiOrchestrator\e2e\checkout-<一意ID>\` に保存されます。実行記録の `github-pull-request.json` と `github-review-comment.json` が共に検証済みで、head/base SHAが一致することを基盤が確認します。`events.jsonl` と `report.md` も同じrun記録に残ります。ログと応答にはタスク内容や診断情報が含まれる可能性があるため、アクセスと保持を管理してください。

Codex開始後の失敗・中断・証跡不一致では一時cloneと実行記録を自動削除しません。push済みブランチや作成済みPRが残った可能性がある場合は、報告された状態とGitHub上のPR・ブランチを人間が確認します。証跡確認前にPRやブランチを削除・クローズせず、PRをマージしないでください。正常終了後も一時cloneと記録は自動削除しません。PRと証跡を確認し、修正PRは `main` にマージせずクローズしてください。

## Fake経路と実Codexの境界

Fakeテストで確認できるのはOrchestratorの制御フローと証跡形式です。実Codexの判断、認証、push、PR作成、GitHubコメント投稿を証明しません。この変更では実Codexを起動しません。
