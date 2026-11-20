# Milestone 1 状況・受入条件棚卸し

> 作成日: 2026-11-20
> 更新日: 2026-11-20
> 作成者: Codex

## 調査基準

GitHub default branch mainのコミット b71157c3c0e957e28e6456b4708f39beaae9783c（PR #113 merge後）と、2026-11-20に確認したIssue/PR、既存計画・検証記録を照合した。本更新はこのSHAを基点とした分離checkoutで作成した。

Issue上の[P1]/[P2]はタイトルの記載で、GitHub labels欄は空、Milestone欄も今回確認したIssueでは未設定である。Issue close stateやユーザー報告を、今回の独立再実行結果とは扱わない。

## Issue一覧

| Issue | 状態 / priority | Milestone | 実態と証跡 | 残る作業 / 依存 |
| --- | --- | --- | --- | --- |
| [#109 最終Testbed E2E](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/109) | Open / タイトルP1 | 未設定 | PR #114で非対話実行、Codex CLI解決、Slack事前確認、起動段階/run ID記録、決定論テストを準備。head `b3f23ed6693d37e181f997e7abf649246b3e528a` のWindows/Ubuntu必須CIは成功。独立レビューの追加指摘なしとの報告があるが、必要なPR上レビュー証跡は未完了。実Codex・Slack送信・Testbed push/PRを含む最終E2Eは未実施。 | PR #114はレビューコメント投稿が自動承認審査で拒否され、迂回せずOPEN・未マージ。合意した受入範囲を別の文書PRに記録し、その後に#109のIssue条件との整合を判断する。 |
| [#105 Work testhost問題](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/105) | Open / タイトルP2 | 未設定 | WorkのWindowsでxUnit testhostがlocalhost接続待ちとなった記録。最小テストでも観測。PR #96 CIではWindows/UbuntuのFake-only Unitが成功。原因は未特定。 | 別追跡。Milestone受入条件ではない。#109の実行場所選定に関係する場合だけ依存を明記し、他開発を止めない。 |
| [#36 Slackライフサイクル通知](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/36) | Open / priorityなし | 未設定 | スレッド集約、各Bot名義、終端broadcast等の将来機能要求。現計画では対象外。 | #109の最小testChannel API送信とは別範囲。後続候補として再優先付け。 |
| [#77 MCPパス差](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/77) | Open / priorityなし | 未設定 | 固定CLIパスでの疎通を妨げない別課題。 | 今回のM1完了条件外。環境差の再発・拡大時に再検討。 |
| [#79 Reviewerへの検証証跡](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79) | Closed / タイトルpriorityなし | 未設定 | [PR #96](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/96)の保存・参照とFakeTestsで、証跡の固定成果物紐付け・安全なReviewer受け渡し・欠落/不正検出を決定論的に実装。[PR #106](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/106)の実Codex接続テストは起動・通信・応答到達に限定。[PR #110](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/110)で条件とリスクを記録しClosed。 | Issue受入条件完了。実Reviewerの自由記述や判断品質はIssue合否条件にしない。M1 E2Eでは工程証跡を観察する。 |
| [#80 Publisher push前チェック](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80) | Closed / タイトルpriorityなし | 未設定 | [PR #110](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/110)で限定Fake検証を記録。CRLF snapshotが通過し、意図した末尾空白が拒否される。元Run 7d7e0e60... のexit 2直接原因は未確定。 | Issue条件完了。実Reviewer/push/PRは#109で一度確認。原因未確定はリスクとして保持し、受入条件に追加しない。 |
| [#84 DOTNET_CLI_HOME](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84) | Closed / priorityなし | 未設定 | [PR #88](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/88)をmergeし、実Codex子プロセスの.NET/NuGet環境を修正。履歴上のRun/証跡不足は別論点。 | Issue条件完了。最終Testbed E2Eでの再確認は#109の範囲。 |
| [#107 AppData保存先](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/107) | Closed / タイトルP2 | 未設定 | PR #111、merge cde292b1cd0c9a1bb7f5601136b5e4a8f4cd5be0。既定パス・旧データ非移動/非削除・明示指定をWindows/Ubuntu CIと独立レビューで確認。 | 完了。実ユーザーデータの移行確認は要求外で、移行もしていない。 |
| [#108 Beta installer](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/108) | Closed / タイトルP2 | 未設定 | PR #112、merge a38293dde1ab032041d857b2895d38ebe275cdbf。v0.0.4-beta.7 prerelease、ZIP/checksum、SHA/manifest確認。Windows/Ubuntu必須CI・独立レビュー成功。ユーザー報告では実インストールと-Verify成功、Slack設定維持。 | Issue完了。実インストールはユーザー報告で、本調査では再実行していない。 |

### 関連する完了済み支援Issue

これらは過去計画の依存関係・重複回避の根拠として参照した。すべてClosed、Milestone未設定、GitHub labelsなし。

| Issue | タイトル上のpriority | 今回の関係 |
| --- | --- | --- |
| [#45 CLIヘルプ/GitHub事前確認](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/45) | P2 | 完了済み。現行の#109で追加実装する対象ではない。 |
| [#47 隔離Git設定のcredential helper](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/47) | P2 | 完了済みのGit安全性対応。再起票しない。 |
| [#51 GitHub公開処理の診断情報](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/51) | V6前 | 診断と証跡の既存成果を再利用する。 |
| [#54 一時Git repository/worktreeテスト](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/54) | V6前 | FakeTestsに分類された実Git回帰の既存成果を再利用する。 |

## PRと検証証跡

- [PR #114](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/114) はOPEN・未マージ、最新head `b3f23ed6693d37e181f997e7abf649246b3e528a`。必須の[Windows/Ubuntu CI](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/actions/runs/44714606676)は成功。PR説明では独立レビューの最新確認で追加指摘なしと報告されるが、レビューコメント投稿は自動承認審査で拒否され、GitHub上の所定レビュー証跡は未完了。今回は拒否を迂回せず、PR #114を変更・マージしない。
- [PR #110](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/110) はmerge commit 112398ec87ec09f1740ff50b0b8d1a1a44b12aaa。Issue #79/#80の受入条件見直しと限定検証。最新headのWindows/Ubuntu必須CI成功、独立レビュー指摘を修正・再確認済み。
- [PR #111](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/111) はmerge commit cde292b1cd0c9a1bb7f5601136b5e4a8f4cd5be0。Windows/Ubuntu必須CIと独立レビュー成功。ユーザー環境のRelease build、Orchestrator/Slack Unit/Fake成功の報告。
- [PR #112](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/112) はmerge commit a38293dde1ab032041d857b2895d38ebe275cdbf。最新head Windows/Ubuntu必須CI、独立レビュー、PowerShell 5.1 release/installer test、Release build成功。ローカル.NET testhost制約はCI成功と区別。
- [v0.0.4-beta.7 Release](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/releases/tag/v0.0.4-beta.7) と[公開workflow](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/actions/runs/44703125192)は配布物確認根拠。#108完了記録も参照。

## 実装済み・検証済み・未確認・未着手

**実装済み:** PR #88 (#84)、#96/#106 (#79)、#101/#110 (#80)、#111 (#107)、#112 (#108) はmainへ反映済み。CI/レビュー等の証跡は各PRとIssueに記録済み。

**検証済み:** #79/#80の決定論Fake検証、Windows/Ubuntu必須CI、#107/#108のCI・独立レビュー。#108はbeta release生成と配布物のhash/manifest確認、さらにユーザー報告として実機インストールと-Verify成功。#80元Run原因と#105 testhost原因は未確認。

**実装済み・未完了:** #109の非対話準備はPR #114にあり、mainには未反映。最終の実Codex/Reviewer・Slack testChannel・必要なTestbed push/PR E2Eも未実施。

**独立追跡:** #105調査はOpenのまま。#109実行環境に影響するかは実行前に判定するが、調査完了自体をM1完了条件にしない。

## 既存FakeTestsの自律マージ分岐カバレッジ

対象は最新main `b71157c` の `tests/Ateliers.Ai.Orchestrator.FakeTests/Program.cs`、`src/Ateliers.Ai.Orchestrator.Core/WorkflowPublication.cs` と関連するワークフロー実装である。コードを変更せず、既存カバレッジを読み取り調査した。

| 既存Fakeカバレッジ | 確認内容 |
| --- | --- |
| `RequiredPullRequestWorkflowPushesReviewsAndCreatesPr` | push→Reviewer→PR作成→レビュー結果コメントの順序、PR/コメント証跡を確認し、成功後の状態は `NeedsHumanInput`。 |
| `RequiredPullRequestWorkflowRetriesMalformedReviewWithinLimit` | 不正レビュー形式を再試行し、上限到達時はPR作成へ進まない。 |
| `RequiredPullRequestWorkflowFailsClosedOnReviewCommentFailure` / `RequiredPullRequestWorkflowRequiresPersistedEvidence` | コメント失敗、不正な指摘、欠落/改変証跡を成功扱いせず停止する。 |
| `RequiredPullRequestWorkflowFailsClosedOnPushFailure` / `RequiredPullRequestWorkflowFailsClosedOnPrFailure` / cancellation cases | push、PR作成、停止要求で後続段階へ進まない・失敗証跡を残す。 |

**不足:** 現行の公開ワークフローFakeはPR作成・レビュー証跡後に人間待ちへ進む経路を検証する。一方、自律マージを選ぶ経路と、判断不能/条件未達で非マージのまま理由・証跡を返す対照分岐を検証するFakeは見当たらない。公開ワークフロー契約にもマージ操作がなく、現コードに自律マージ分岐はない。この不足は今後の実装・テスト課題であり、今回の文書PRでは実装しない。次工程では、固定したレビュー結果・ゲート結果・許可条件に対して両分岐と副作用不在を検証する。実Codexのモデル判断や実GitHubマージは不要。

## 当初計画とのギャップ

当初の計画案は#84/#79を必須、#80・clean checkout/worktree・Issue→TaskContract・#36通知を候補とし、実装・実Codex・PR・マージ・Slack報告までを自己改善E2Eで一括確認する構成だった。現在は次のとおり。

- #84/#79は実装・個別受入済み。#80も必要十分な限定条件でClosed。#107/#108は後から追加され、CI・レビュー・配布確認まで完了。
- #51/#54等の既存作業と#101/#110がcheckout/Git安全性を担い、#109や新Issueで重複させない。
- #36全面実装、Slack通知モード設計、TaskContract手順の独立Issue化は現在の残IssueにもMilestone登録にもなく、現段階で必須にしない。
- 主な未達はPR #114の所定レビュー証跡、同PRのマージ、および一度の最終E2E。#105は別追跡の阻害要因候補であってM1達成条件ではない。
- 本計画の合意済み方針は、原則AIが独立レビュー・修正・品質ゲート・PR作成・マージまで自律実行すること。マージ可否が判断不能、または必要条件未達なら、理由・証跡とともに人間へ返す。#109で生成するTestbed PRは公開経路確認のためOPENで終了する。この個別E2Eの終了条件を、M1の一般的な自律マージ方針と混同しない。

## 合意済み方針と#109条件の整理

自律マージ可否の分岐はUnit/Fakeで固定入力を用いて決定論的に検証する。実Codexに実際のモデル判断をさせて分岐を試験したり、GitHubで実マージしたりすることは要求しない。必要なFakeの不足は後述する。

| 責務 | 維持・提案する条件 | 理由 |
| --- | --- | --- |
| 一般の委譲Issue | 独立レビュー、指摘修正・再レビュー、必須CI、PR作成、マージを原則自律実行。判断不能または条件未達なら理由・証跡を添えて人間へ返す。 | 合意済み運用方針。人間承認を一律条件にせず、未解決リスクや未達を隠さない。 |
| マージ分岐 | Fakeで、条件充足時にマージ操作を選ぶこと、および判断不能/条件未達時にマージせず報告することを検証する。 | 分岐制御・副作用・証跡は決定論的なプログラム動作。AIや実GitHubの接続を使わず確認できる。 |
| #109接続E2E | CLI経由の実Codex起動、Implementer/Reviewer接続、Slack `testChannelId` 通知、Run証跡を確認する。Issue本文が要求する専用branch push、PR作成、レビュー証跡とhead/base SHA照合は維持する。成功PRはOPENで終了。 | Issue本文の元要件は最終Testbed実行とGitHub公開経路を対象にしている。push/PRはその接続を示す証跡として必要な範囲に限る。main不変・既存PR不変・force pushなしを守る。 |
| #109で扱わないこと | 実マージ、自律マージ可否の試験、モデル判断の正しさ/品質は合否条件にしない。 | E2Eは実Codex/Slack/GitHubとの接続とRun証跡を確認する責務。判断分岐はFakeで十分に決定できる。 |

### #109 Issue受入条件の現行記載と提案

Issue本文は変更していない。今後本文を更新する場合に備え、変更範囲と根拠をここに記録する。

| 観点 | 現行Issue本文 | 提案する整理 | 根拠 |
| --- | --- | --- | --- |
| 決定論テスト | Fake/Unitで対話待ち、Codex path、Slack早期停止、testChannel固定、push/PR失敗時の停止を確認。 | 維持し、Run証跡・起動段階の保持も含める。自律マージ可否/人間返却分岐を固定入力のFake条件として明記する。 | #114に準備テストがある。自律マージはプログラム分岐なのでFakeで必要十分に確認できる。 |
| 接続と公開経路 | 最終E2EでImplementer/独立Reviewer、Release build・5 checks、Slack API成功、専用branch push、PR、Reviewerコメントとhead/base SHA整合を確認。 | 接続確認とRun証跡を維持。push/PRとレビューコメント証跡も、元Issueの公開処理を実証するのに必要な範囲で維持し、成功PRはOPENで停止する。 | これらはIssueの「最終Testbed E2Eを無人で一度実施」し、GitHub公開結果とレビュー対象SHAを照合する要求に対応する。 |
| マージ・モデル品質 | 成功時PRはOPENで人間レビュー待ち。実マージ・モデル判断品質の条件は記載なし。 | OPEN終了を維持。自律マージ分岐/実マージをE2E条件に追加しない。モデル判断品質は引き続き対象外。 | E2Eの目的を接続・公開経路の証明に限定し、マージ分岐のFake検証と重複させない。 |

PR #114の非対話準備、Codex解決順位、Slack事前確認、起動段階/run ID証跡、決定論テストは再利用する。これらを作り直すコード変更はこの計画に含めない。

## #109 最小確認案

1. **Fake/Unit・必須CI:** 対話待ちがないこと、Codex実行形式の解決、Slack設定不足時の副作用前失敗、testChannel固定、push/PR失敗停止、秘密情報をログへ出さないこと、UTF-8記録、未開始/開始後失敗の区別、既存状態を変更しないことに加え、自律マージ選択と人間へ返す分岐を検証する。
2. **最終E2E（1回）:** 認証/権限、CLI、Testbed main SHA、testChannelの前提を確認後、実Implementer/独立Reviewerの接続、Release buildと5 checks、testChannel Slack API成功、専用一時ブランチのpushとPR生成を確認する。
3. **結果照合:** Run IDと対象SHA、Reviewer証跡、CI結果、PR head/base SHA、Slack API応答を関連づける。Testbed mainと既存PRは不変、成功PRはOPEN。失敗時に外部状態の自動削除・クローズ・マージをしない。
4. **E2Eの対象外:** 自律マージ分岐、実マージ、AI文章品質、Reviewerの主観的な判断品質、Slack UI到達確認。必要な接続結果はRun証跡/API結果で判断する。

Slack先や権限を安全に確認できなければ実E2Eを開始せず準備阻害として扱う。

## ゲート見直しと未確定事項

- **維持:** 対象PRのWindows/Ubuntu Unit/Fake必須CI、独立レビュー、#109の一度の最終E2E、Slack testChannel API成功、Issue本文が要求する範囲のpush/PRとSHA照合、Testbed main不変、専用PR OPEN。
- **分担:** 自律マージ可否の決定論的な分岐はFakeTestsで検証し、実Codexや実マージを要求しない。#109は実Codex/Reviewer/Slack/GitHub接続とRun証跡を確認する。
- **整理:** #79/#80個別Issueから実AI・push・PRを外し、Milestoneゲート#109に集約。通常PRへフルE2Eや実外部IntegrationTestsを一律要求しない。
- **提案として外す:** #36全文字列・スレッド/broadcast要件、#105の原因解明、Reviewer自然言語品質、Slack画面到達をMilestone必須条件にしない。
- **次の判断:** 文書PRレビュー後、#109本文を現行のpush/PR受入条件を保ったまま、マージ分岐をFakeへ分離する提案に更新するか。GitHub正式Milestone登録、#36の後続扱い、#109実E2E実施時期も別途判断する。Issue本文・状態・優先度はこの作業では変更していない。

## 参照

- [開発計画](../../plans/ateliers-ai-orchestrator-milestone-0-1-plan.md)
- [Issue #79/#80受入レビューと限定検証](issue-79-80-acceptance-review.md)
- [テスト種別と責務](../../reference/testing-classification.md)
- [開発用IntegrationTestの実行範囲](../../reference/development-integration-tests.md)
- [AGENTS.md: Issue受入条件の見直し / テスト](../../../AGENTS.md)
