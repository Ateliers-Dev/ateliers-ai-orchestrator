# Testbedレビューサイクル

このシナリオは Ateliers-Dev/ateliers-ai-orchestrator-testbed の単語数課題を使い、現行schemaVersion 1の契約とPR必須Codex経路を検証します。実行基盤の共通手順は [E2E実行基盤](README.md) を参照してください。

## 目的と公開操作

Testbed `main` に意図的に残してある `TextMetrics.CountWords` の空白区切り不具合を対象とします。独立Reviewerが修正を求めた場合だけ、契約上限内で修正・再レビューします。

TestbedはPublicリポジトリです。実Codexを開始するとCodex利用枠または費用が発生する場合があります。成功時には一意なE2E作業ブランチをpushし、公開PRとReviewer結果コメントを作成します。PRは自動マージされず、明示承認を得るまでOPENのまま保持します。教材の未修正状態を保つため `main` へマージしてはいけません。

## シナリオ契約と前提確認

- `scripts/e2e/scenarios/testbed-review-cycle.json` は現行CLIと同じ契約フィールドを使い、`reporting.pullRequestExpected` を `true` にします。対象範囲、5つのチェック、独立レビュー、必要な場合のみのbounded修正、停止条件を定義します。
- 実行対象はGitHubから取得するTestbed `main` のcleanな一時cloneです。実行直前にGitHubの既定ブランチSHAとcloneのHEADを照合します。本体リポジトリや利用者の既存checkoutを対象にしません。
- PR必須経路はrun IDを使う一意な作業ブランチを作成し、push、PR作成、Reviewer結果コメント投稿・照合へ進みます。
- Slack通知は実行スクリプトの `-SlackNotifications` で `disabled`、`test`、`auto` を選びます。既定値は `disabled` です。`test` は `[Ateliers AI TEST]` と明示した通知を設定済みテストチャンネルへ、`auto` は通常の通知先へ送信します。通知を指定したときだけ、実Codex起動前に設定とBot認証を確認し、確認文へモードとチャンネルIDを表示します。

[E2E実行基盤の準備確認](README.md#準備確認) を実行します。共通の準備確認は本体Release build、GitHub認証とTestbed `main` のSHA、cleanな一時clone、生成した契約の現行Release CLI検証を行います。単体テストは実行せず、E2Eの前提ゲートにも使いません。準備確認だけの実行では一時cloneを削除します。

実行準備ではTestbed `main` のclean cloneをRelease buildし、チェックを実行します。`tabs and newlines` と `whitespace only` の2件だけが失敗し、`ordinary spaces`、`repeated spaces`、`empty input` が通ることを確認できない場合はCodexを起動せずNGとします。基準状態のログは一時ディレクトリに記録されます。

Fake経路はこの実試験の合格判定に使いません。実CodexによるImplementer、独立Reviewer、必要時のみの修正・再レビュー、GitHub公開を実行します。Slack実通知は `test` または `auto` を明示した場合だけ追加されます。Reviewerの修正指摘を強制する偽指摘や追加変更はありません。

## 実Codex E2E

準備確認を成功させた後、 [E2E実行基盤の実行手順](README.md#実codex実行) に従って実行します。確認文は次のとおりです。

Slack送信なしの場合の確認文:

    RUN CODEX E2E FOR ATELIERS-DEV/ATELIERS-AI-ORCHESTRATOR-TESTBED WITH PUSH AND PR WITHOUT SLACK

テストチャンネル送信は `-SlackNotifications test`、通常通知先への送信は `-SlackNotifications auto` を追加します。各モードの確認文には実際の送信先が含まれます。

実行ではCodex CLIとモデルを使い、cleanな一時cloneに対して契約を実行します。契約のPR必須経路は、専用ブランチへのcommit/push、固定差分の独立Reviewer確認、必要な場合のみの上限内修正と再レビュー、PR作成、head/base SHA検証、Reviewer結果コメントの投稿と再読込照合、NeedsHumanInputでの停止を行います。スクリプトはpush後にもTestbedでRelease buildと全5チェックを独立実行し、期待値ファイルがmainから変更されていないことを確認します。またPRがOPENかつbaseがmainであることを再取得して確認します。push、PR作成、独立実行確認のいずれかに失敗した場合、成功扱いにはしません。

`test` または `auto` ではチャンネル設定かBot Tokenが不足すると実Codexを起動せず停止します。Slack送信失敗や成功件数なしもE2E不合格です。実行後、選択したSlack送信先でrun IDの到達を目視確認し、表示された確認文を入力してください。確認できない場合は不合格です。`disabled` では設定を読み込まずSlack送信のゲートを適用しません。

## 証跡と失敗時

一時clone、実行記録、ライフサイクルログ、基準・修正後build/check出力、CLI出力は `%TEMP%\AteliersAiOrchestrator\e2e\checkout-<一意ID>\` に保存されます。実行記録の `github-pull-request.json` と `github-review-comment.json` が共に検証済みで、head/base SHAが一致することを基盤が確認します。`events.jsonl` と `report.md` も同じrun記録に残ります。ログと応答にはタスク内容や診断情報が含まれる可能性があるため、アクセスと保持を管理してください。

Codex開始後の失敗・中断・証跡不一致では一時cloneと実行記録を自動削除しません。push済みブランチや作成済みPRが残った可能性がある場合は、報告された状態とGitHub上のPR・ブランチを人間が確認します。証跡確認前にPRやブランチを削除・クローズせず、PRをマージしないでください。正常終了後も一時cloneと記録は自動削除しません。PRは人間の最終承認があるまで開いたままです。

## リリース判定

このE2Eが途中停止、証跡欠落・不一致、または明示的に選択したSlack通知の送信失敗・未到達のいずれかならリリース判定はNGです。Fake実行や単体テストの成功で代替できません。合格してもPRは開いたままにし、人間の最終承認前にTestbed `main` へマージしません。
