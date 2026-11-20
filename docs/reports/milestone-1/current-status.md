# Milestone 1 状況・受入条件棚卸し

> 作成日: 2026-11-20
> 更新日: 2026-11-20
> 作成者: Codex

## 調査基準

GitHub default branch mainのコミット a38293dde1ab032041d857b2895d38ebe275cdbf と、2026-11-20に確認したIssue/PR、既存計画・検証記録を照合した。既定checkoutにある別作業の未コミット変更は対象外とし、本報告は同SHAを基点とした分離checkoutで作成した。

Issue上の[P1]/[P2]はタイトルの記載で、GitHub labels欄は空、Milestone欄も今回確認したIssueでは未設定である。Issue close stateやユーザー報告を、今回の独立再実行結果とは扱わない。

## Issue一覧

| Issue | 状態 / priority | Milestone | 実態と証跡 | 残る作業 / 依存 |
| --- | --- | --- | --- | --- |
| [#109 最終Testbed E2E](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/109) | Open / タイトルP1 | 未設定 | [PR #114](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/114)で非対話実行、Codex CLI解決、testChannel/API結果、起動段階・run ID記録を実装。head `988674d` のWindows/Ubuntu CIは成功。独立レビューは最新差分に追加指摘なし。最終E2Eは未実施。直近の過去準備実行はCodex起動前にCLI解決で停止し、実Codex・Slack・push・PRは発生していない。 | M1最終ゲート。PR #114はOPENで、マージ前の必要なレビュー記録が未完了。ユーザー本人の一度のE2Eも未実施。 |
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

- [PR #110](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/110) はmerge commit 112398ec87ec09f1740ff50b0b8d1a1a44b12aaa。Issue #79/#80の受入条件見直しと限定検証。最新headのWindows/Ubuntu必須CI成功、独立レビュー指摘を修正・再確認済み。
- [PR #111](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/111) はmerge commit cde292b1cd0c9a1bb7f5601136b5e4a8f4cd5be0。Windows/Ubuntu必須CIと独立レビュー成功。ユーザー環境のRelease build、Orchestrator/Slack Unit/Fake成功の報告。
- [PR #112](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/112) はmerge commit a38293dde1ab032041d857b2895d38ebe275cdbf。最新head Windows/Ubuntu必須CI、独立レビュー、PowerShell 5.1 release/installer test、Release build成功。ローカル.NET testhost制約はCI成功と区別。
- [v0.0.4-beta.7 Release](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/releases/tag/v0.0.4-beta.7) と[公開workflow](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/actions/runs/44703125192)は配布物確認根拠。#108完了記録も参照。

## 実装済み・検証済み・未確認・未着手

**実装済み:** PR #88 (#84)、#96/#106 (#79)、#101/#110 (#80)、#111 (#107)、#112 (#108) はmainへ反映済み。CI/レビュー等の証跡は各PRとIssueに記録済み。

**検証済み:** #79/#80の決定論Fake検証、Windows/Ubuntu必須CI、#107/#108のCI・独立レビュー。#108はbeta release生成と配布物のhash/manifest確認、さらにユーザー報告として実機インストールと-Verify成功。#109のPowerShell preflight決定論テストはローカルとPR CIで成功し、head `988674d` のWindows/Ubuntu全Unit/Fake必須ゲートも成功。最新差分の独立レビューは追加指摘なし。#80元Run原因と#105 testhost原因は未確認。

**未着手:** Milestone最終の実Codex/Reviewer・Slack testChannel・Testbed push/PR E2E。実行用のコード変更はPR #114でレビュー中で、マージ・実E2E・Slack API送信は行っていない。

**独立追跡:** #105調査はOpenのまま。#109実行環境に影響するかは実行前に判定するが、調査完了自体をM1完了条件にしない。

## 当初計画とのギャップ

当初の計画案は#84/#79を必須、#80・clean checkout/worktree・Issue→TaskContract・#36通知を候補とし、実装・実Codex・PR・マージ・Slack報告までを自己改善E2Eで一括確認する構成だった。現在は次のとおり。

- #84/#79は実装・個別受入済み。#80も必要十分な限定条件でClosed。#107/#108は後から追加され、CI・レビュー・配布確認まで完了。
- #51/#54等の既存作業と#101/#110がcheckout/Git安全性を担い、#109や新Issueで重複させない。
- #36全面実装、Slack通知モード設計、TaskContract手順の独立Issue化は現在の残IssueにもMilestone登録にもなく、現段階で必須にしない。
- 主な未達は#109の実行準備PR #114の品質ゲートと一度の最終E2E。#105は別追跡の阻害要因候補であってM1達成条件ではない。
- 当初計画の自己改善E2EにはPRマージまでの記載がある一方、#109はTestbed PRをOPENで人間レビューへ渡す。#109ではAIの自律マージ能力を受け入れず、M1全体にそれを残すかは未確定として扱う。以前のMVP決定記録は人間の最終確認・マージを置いていたため、計画の文言と現在の委譲ルールを合わせて判断する必要がある。

## #109 最小確認案

1. **Fake/Unit・必須CI:** 対話待ちがないこと、Codex実行形式の解決、Slack設定不足時の副作用前失敗、testChannel固定、push/PR失敗停止、秘密情報をログへ出さないこと、UTF-8記録、未開始/開始後失敗の区別、既存状態を変更しないこと。
2. **最終E2E（1回）:** 認証/権限、CLI、Testbed main SHA、testChannelの前提を確認後、実Implementer/独立Reviewer、Release buildと5 checks、testChannel Slack API成功、専用一時ブランチのpushとPR生成を確認する。
3. **結果照合:** Run IDと対象SHA、Reviewer証跡、CI結果、PR head/base SHA、Slack API応答を関連づける。Testbed mainと既存PRは不変、成功PRはOPEN、自動merge/cleanupなし。
4. **合否から外す:** AI文章品質、Reviewerの主観的な判断品質、Slack UI到達確認。必要なら運用メモとして残し、機械的条件へ重複追加しない。

#109詳細条件のうちFakeで保証するものと最終接続試験で実証するものをIssue更新前に再整理する。Slack先や権限を安全に確認できなければ副作用を開始せず準備阻害として扱う。

## ゲート見直しと未確定事項

- **維持:** 対象PRのWindows/Ubuntu Unit/Fake必須CI、独立レビュー、#109の一度の最終E2E、Slack testChannel API成功、Testbed main不変、専用PR OPEN。
- **整理:** #79/#80個別Issueから実AI・push・PRを外し、Milestoneゲート#109に集約。通常PRへフルE2Eや実外部IntegrationTestsを一律要求しない。
- **提案として外す:** #36全文字列・スレッド/broadcast要件、#105の原因解明、Reviewer自然言語品質、Slack画面到達をMilestone必須条件にしない。
- **未確定:** GitHub正式Milestone登録、#109受入条件を最小案へ改訂するか、#36を後続機能としてどう扱うか、M1全体でAIによるPRマージまでを受け入れるか。Issueクローズ、優先度/ラベル変更、新規Issueは行わず提案に留めた。

## 参照

- [開発計画](../../plans/ateliers-ai-orchestrator-milestone-0-1-plan.md)
- [Issue #79/#80受入レビューと限定検証](issue-79-80-acceptance-review.md)
- [テスト種別と責務](../../reference/testing-classification.md)
- [開発用IntegrationTestの実行範囲](../../reference/development-integration-tests.md)
- [AGENTS.md: Issue受入条件の見直し / テスト](../../../AGENTS.md)
