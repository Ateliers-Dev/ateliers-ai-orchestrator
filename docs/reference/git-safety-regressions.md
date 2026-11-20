# Git安全性の回帰対策

> 作成日: 2026-11-20
> 更新日: 2026-11-20
> 作成者: Codex

この文書は、繰り返し発生したGit作業領域の障害と、現行コードが維持する保証・回帰テスト・失敗診断を対応付けます。Git関連変更やテスト分類の整理では、この表を更新し、記載した保証を通常のUnit/Fake必須CI経路に維持してください。

## 原因と現在の保証

| 過去の事例 | 確認された原因・抜け | 現在の保証 | 必須回帰確認 |
|---|---|---|---|
| PR #24 / Issue #25、PR #28 | `git status -z`出力の解析不足と、Git fixture cleanup時のReadOnly・junction処理 | statusのNUL区切りを扱い、危険なfixture cleanupを検査する | `PublisherChecksRealLinkedWorktreeConfigAndCleanliness` およびfixture cleanupのFakeTestsケース |
| PR #32、PR #34、PR #106 | lifecycleログの既定配置がcheckout配下。PR #34の外部保存先指定・永続化確認が、PR #106のIntegrationTest整理で外れた | 実行記録とlifecycleログの既定先をユーザーデータ領域に置き、Git checkoutと分離。明示パス指定は維持 | `DefaultRunDataDoesNotDirtyGitWorkspace` が実Git clean状態、既定ログ、Fake Codex起動境界を確認 |
| PR #43 / #46 / #48 / #56 / #99 / #101 | Git状態・公開条件・設定・改行・診断の個別境界に不足があった | Git公開前のclean確認、設定隔離、Windows改行正規化、固定段階と安全な状態集計 | GitHubWorkflowPublisherの安全なGit FakeTests、およびWindows/Ubuntu必須CI |
| PR #50 | 子プロセス環境とcheckout内の設定・生成物の境界が不足 | Codex子プロセス用profile・一時領域を分離し、checkout内を指すjunctionを拒否 | `CodexEnvironmentJunctionIntoCheckoutIsRejected` と子プロセス環境FakeTests |
| PR #58 | 実Git状態の回帰を通常の値テストだけで確かめられない | 隔離したローカルGit repoとlinked worktreeを使い、clean/dirty・snapshotを確認 | `PublisherChecksRealLinkedWorktreeConfigAndCleanliness`、`CodexChildChangesAreSnapshottedFromLinkedWorktree` |
| Issue #80 / #98 | Publisher停止のGit原因が確定していない | clean-checkとPublisher失敗はstage/reason/exit/output統計を記録。未確認の原因は推定と分ける | 既存のPublisher失敗分類FakeTests。Issue #80/#98の未確定原因を解決済みとは扱わない |

この表は確認できた代表例であり、全PRをGit障害と分類した一覧ではありません。詳しい発生経緯は各PR、Issue、および[Milestone 0/1計画](../plans/ateliers-ai-orchestrator-milestone-0-1-plan.md)を参照してください。

## clean-check診断の契約

Implementerを起動する前のGit clean-checkで停止した場合、実行記録のImplementer失敗内容に次を残します。

- `stage=pre-agent-clean-check`
- 安全なGitコマンド識別子
- Git終了コード
- dirty entry件数
- 最大8件の状態コードと相対パス

制御文字、絶対パス、`.env`、秘密鍵、credential、secret、password、passphrase、token、API keyらしい名前はパスを表示せず、短いSHA-256参照で識別します。Gitのstatus本文や未追跡ファイルの内容は診断に複製しません。これは名前に基づく限定的な秘匿処理であり、機密情報を含む全パスを判定するものではありません。

## テスト分類と変更ルール

- 実Git CLIを使っても、隔離repo内で完結し、GitHub・実Codex・Slack等の外部接続をしない決定論テストは `FakeTests` に置きます。これは実外部接続を責務とする `IntegrationTests` とは区別します。
- `OrchestratorTestCatalog` のGit安全性ケースと `docs/reference/testing-classification.md` を同時に見直します。分類変更時はテストコードだけを移動し、回帰保証を落とさないでください。
- 通常PRのUnitTests・FakeTestsは両方とも必須です。`.github/workflows/unit-tests.yml` がWindowsとUbuntuで両方を実行します。
- Git安全性テストの削除・分割・名称変更は、同じ受入条件を満たす代替ケースへの対応付けを本書へ記録します。コードレビューで対応表、CI分類、実行結果を確認してください。
- 新たなGit障害が原因未確定なら、確認事実・仮説・未確認事項を分けて記録し、推測を修正理由として確定させないでください。
