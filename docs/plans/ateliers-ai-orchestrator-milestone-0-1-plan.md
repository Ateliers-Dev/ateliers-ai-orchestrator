# Ateliers AI Orchestrator 開発計画

> 作成日: 2026-11-18
> 更新日: 2026-11-20
> 作成者: yuu503

> 状態: Milestone 0完了記録・Milestone 1残作業案（#109が最終受入ゲート、#105は独立追跡、自律マージは将来候補）

> 対象: Ateliers-Dev/ateliers-ai-orchestrator

## 1. この文書の目的

今回のMCP→CLI連携と実エージェント検証を **Milestone 0** として記録し、その確認結果から次の **Milestone 1: 自己改善基盤** の計画案を整理する。

以前の「Phase 2」は技術検証の区分名として扱う。Issue管理と開発計画ではMilestone番号を使い、**Phase 2とMilestone 0を同じ種類の番号として混同しない**。Milestone 0はPhase 2相当の取り組みを含む完了記録であり、Phase 2自体の名称を置き換えるものではない。

本書では情報を次のように区別する。

- **確認済み**: 実行報告またはGitHub上のIssue/PRで根拠を確認した内容
- **実装済み**: PRのマージは確認済みだが、実環境の動作確認とは区別する内容
- **提案（未確定）**: Milestone 1の必須候補、順序、受け入れ条件。GitHub Milestone登録前の案
- **合意済み運用方針**: Milestone 1で委譲されたIssueの実装・レビュー・修正・品質ゲート・PR作成までの手順と停止条件。PRの最終承認・マージは人間が行う
- **検討事項**: 方針が未決定で、今回新規Issueを作成しない内容
- **未確認**: 原因・状態・設計や実機動作がまだ確定していない内容

## 2. Milestone 0 — MCP→CLI連携と実エージェント検証

### 2.1 目的

MCPからCLI/Coreの処理をつなぎ、GuideとSchemaに沿った契約でFake実行と実Implementer/Reviewerを動かせるかを確認する。実際のPR必須経路まで完了できるかも試し、次の改善に必要な不足を把握する。

### 2.2 達成した内容と確認根拠

| 項目 | 確認内容 |
| --- | --- |
| MCP→CLI→Core | MCPからCLIを起動し、Coreへ処理を渡す経路を確認した。MCP実行環境のmanifest/path問題があり、CLIの絶対パスを明示する構成で疎通させた。環境差の一般的な解決は別課題として残る（[#77](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/77)）。 |
| Guide / Schema | TaskContractのGuideとSchemaを用いて実行契約を扱えることを確認した。今回の検証範囲を超えて、すべての契約仕様や互換性が確定したとは扱わない。 |
| Fake run | Fake実行を完了した。Run ID: `e0477f0e75e04bccaa4806582769c21c`。 |
| 実Implementer / Reviewer（先行スモーク） | 実エージェントを起動し、Testbed上の `TextMetrics.CountWords` を修正した。Run ID: `a64d43e4e04e4934a4b7b17fbb8ea3dc`。Reviewerは当初、必要な実行ログ・成果物を受け取れず `NeedsHumanInput` とした。証跡受け渡しは[#79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)に記録されている。 |
| Testbed検証 | 先行スモーク後の手動再確認でRelease buildと5ケースすべての成功が報告された。後述の追加Runでは、Reviewer終了後に一時的なプロセス環境から同じくRelease buildと5チェックの成功を確認したが、Reviewerの再確認は未実施。 |
| 実Codex E2E（2026-11-18） | Run ID `061cc78e2951423b9488cf58ffa36c98`。MCP `run_orchestration` → CLI v0.0.3 → 実Codexの経路で、Implementer（PID 4904）と独立Reviewer（PID 60000）がそれぞれexit 0で起動。Implementerは `TextMetrics.CountWords` を空白文字で分割する修正を隔離ブランチに作成し、テストコードは変更していない。Reviewerは差分に異議なしとした一方、証跡不足で修正依頼と判定し、Runは `NeedsHumanInput` となった。 |
| 追加ビルド・チェック | Implementer中のビルドはOrchestrator管理の `DOTNET_CLI_HOME` への書き込み拒否で未完了。Reviewer終了後、プロセス限定の一時環境でRelease build（警告・エラー0）と5ケースのチェック（終了コード0）を確認した。Reviewerによる後追い証跡の再確認は未実施。原因解消は[#84](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84)、証跡受け渡しは[#79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)で扱う。 |
| PR必須経路 | 別のPR必須RunはImplementerの修正まで進んだが、`push.secret-scan-and-whitespace-check` がexit code 2で停止し、Reviewer・push・PR作成には至らなかった。Run ID: `7d7e0e60ff494ff4994e2b8f2d4e75f2`。直接原因は未確定（[#80](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80)）。 |
| マージ済み対応 | OrchestratorのSlack設定対応は[PR #82](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/82)、Testbed E2E対応は[PR #2](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator-testbed/pull/2)でマージ済み。PowerShell 5.1向け文字コード修正PRもマージ済み（[#73](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/73)）。ユーザー側でSlack JSON設定ファイルをローカル環境に配置済み。これらは実装・設定配置の確認であり、下記RunからのSlack送信成功を意味しない。 |
| Slack実送信 | ユーザー側ではSlack通知の受信が別途確認されている。一方、Run `061cc78e2951423b9488cf58ffa36c98` はMCP経由で通知無効、送信0件。設定JSON 2つは存在とJSON読取を確認したが、`testChannelId` は通常通知先と重複し、`verify-slack` は投稿を行わずexit 3で設定または認証を確認できなかった。テストモードの誤送信を避けるため実送信は見送っており、今回のMCP RunでSlack通知が成功したとは記録しない。 |
| 外部変更 | Run `061cc78e2951423b9488cf58ffa36c98` ではGitHubへのpush・PR・コメント・マージなし。Testbed `main` も変更なし。 |

Runの詳細は参照会話に添付された `report(20261118-090801).md`、`post-review-verification.md` と追加実行報告に基づく。この文書作成時にRunを再実行した独立検証ではない。

### 2.3 完了判定

Milestone 0の目的は、MCP→CLI→Coreの実行経路、契約に基づくFake実行、実エージェント起動とTestbed修正を確認し、次の開発に進める根拠を得ることとする。この範囲ではPhase 2相当の疎通検証を達成した。

**PR作成までの開発サイクルが成功した、またはPR必須経路が完成したとは判定しない。** PR必須Runはpush前チェックで停止し、Reviewer・push・実PR作成は未達である。したがって自己改善基盤の完成はMilestone 1の目標として扱う。

### 2.4 Milestone 0終了後の作業状況

以下は実装・マージ、ローカル設定、実機検証の状態を分けて記録する。

- OrchestratorのSlackユーザー設定配置・E2E通知モード対応はPR #82でマージ済み。TestbedのE2E通知モード・シナリオ文書対応はPR #2でマージ済み。
- PowerShell 5.1向けインストーラーの文字コード修正もPR #73でマージ済み。
- ユーザーがSlack JSON設定ファイルをローカルに配置済み。設定ファイルの存在・読取確認はできているが、Run `061cc78e2951423b9488cf58ffa36c98` のSlack通知はdisabledで送信0件。実際のテスト通知経路は未検証。
- Phase 2のMCP→CLI→Core疎通、Fake、実Codex Implementer/Reviewer起動、TestbedのCountWords修正と5チェック成功は確認済みであり、Milestone 0のPassを維持する。ただし実Runは `NeedsHumanInput` であり、Reviewerの再確認、PR作成・マージは未検証。

以上から、Slack通知設定やRun後の検証証跡をMilestone 0の完了条件へ遡及追加しない。未解決課題はMilestone 1候補として扱う。

## 3. Milestone 1 — 自己改善基盤

### 3.1 中心目標と委譲運用方針（合意済み）

Milestone 1の目標は、オーケストレーションの仕組みをオーケストレーション自身によって改善できることを実証することである。Issueと上位の指示で許可された範囲内で、実装の委譲、独立レビュー、指摘修正と再レビュー、品質ゲート、PR作成、成果物と証跡の記録までを行う。PRの最終的な承認とマージ判断・操作は人間が担当する。自律マージ機能はMilestone 1の必須条件ではなく、実装・受入テストを行わない。

#### PRレビューと人間による最終マージ

1. 実装後、独立ReviewerがPRの差分を技術的に評価し、指摘事項または指摘なしの結果をPRに記録する。コード行へのコメントが難しい場合は、PR全体へのコメントでよい。
2. 指摘がある場合、実装・修正者は対応後にPRへ報告し、独立Reviewerが修正後の差分を再確認する。修正・再レビューは最大3回まで行う。上限後も指摘が未解決なら停止して報告する。
3. 必須のレビュー結果・指摘対応証跡・品質ゲート結果を記録し、PRを人間の最終確認へ引き継ぐ。Reviewerの技術評価は人間の最終承認を代替しない。
4. 最終的なPR承認とマージ可否の判断・操作は人間が行う。AIは明示的に許可された作業範囲でPRを作成し、最終確認に必要な根拠と証跡を揃える。
5. 品質ゲートが失敗した場合は原因を確認して修正または再試行する。再試行は最大1回とする。コード等を修正した場合は修正・再レビューの最大3回に含め、独立Reviewerによる再確認と更新された証跡を得る。修正後または再試行後も未達なら停止して報告し、失敗したゲートを成功扱いしない。

#### 停止条件

次の場合はPRをマージせず、判断できない点、未解決事項、実施結果と根拠となる証跡を報告する。

- Issueの仕様または方針を根拠に基づいて判断できない。
- Issueの範囲外に対応が必要な問題が見つかり、別Issueで扱う必要がある。
- レビュー指摘が未解決である。
- テスト・CI等の品質ゲートが、修正または再試行後も未達である。

#### 将来候補: 自律マージの責務・承認設計（未確定）

Milestone 1では自律マージを実装せず、FakeTestsを含む自律マージ受入条件も必須にしない。PR作成・レビュー・修正・品質確認・証跡記録までの目標を達成し、最終マージは人間が担当する。この方針変更は、マージ操作の実行権限と承認判断の責任が異なり、Reviewerの技術評価と要件に対する最終承認を分ける必要があるためである。複数Reviewerの結果・残存リスクの統合、承認証跡、権限管理、人間への委譲も含むオーケストレーション全体の設計が必要なため、Milestone 1の期間内に単純なマージ処理として実装しない。

将来の検討では、少なくとも次の責務分離案を評価する。この構成は初期案であり、正式採用したアーキテクチャではない。

| 役割 | 初期案の責務 |
| --- | --- |
| Implementer | 要件に基づく実装、修正、検証 |
| Reviewer（複数を想定） | コード品質・セキュリティ・アーキテクチャ等の担当領域を技術評価し、問題点を報告する。最終承認は担当しない |
| Approver（新規AIエージェント案） | 複数のレビュー結果、要件・受入条件、残存リスクを総合し、最終承認可否を判断する。GitHub上のマージ操作権限は持たない |
| Orchestrator | 工程を管理し、証跡・必須品質ゲート・対象SHA・権限・保護ルール等を確認して、承認に基づくマージ操作を制御する |

設計時には、マージ操作権限と承認責任の分離、Reviewerの技術評価とApproverの要件適合判断の分離、複数レビュー結果・残存リスクの統合、承認証跡と権限モデル、判断不能時の人間への委譲を検討する。Approver導入、承認基準、レビュー統合方法、証跡形式、権限モデルはいずれも未決定である。将来実装する場合も、プログラム上の分岐は固定入力のUnit/FakeTestsで決定論的に検証し、実Codexの判断品質や実GitHubマージを分岐テストの条件にしない。
#### #109 最終E2Eの別責務

#109は、CLIから実Codexが起動すること、Implementer/Reviewerの接続とRun証跡、Slack `testChannelId` へのAPI通知、および必要な場合のGitHub公開経路を一度確認する接続E2Eとする。push/PRは元Issueの公開処理とPR証跡の受入条件を実証する範囲で維持し、成功したTestbed PRはOPENのままにする。E2Eの合否に、自律マージ、実マージ、AIモデルの判断品質を含めない。

### 3.2 現在の残作業と提案する順序（2026-11-20棚卸し）

現在の状態・証跡・当初計画とのギャップは[Milestone 1状況・受入条件棚卸し](../reports/milestone-1/current-status.md)を参照する。確認対象IssueにGitHub Milestone紐付け・ラベルはなく、[P1]等はIssueタイトル上の優先度表記である。

| 順序 | Issue / 項目 | 状態・位置づけ | 次に必要な検証 |
| --- | --- | --- | --- |
| 1（最終段階） | [#109 最終Testbed E2E](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/109) | Open・タイトルP1。M1最終ゲート。[PR #114](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/114)はOPEN、head `b3f23ed` のWindows/Ubuntu必須CI成功。必要なPR上レビュー証跡は未完了。最終E2E未実施。 | PR #114の非対話準備はmainへ反映済み。受入範囲を整理する本件文書PRを別途レビューする。以降の#109最終E2Eでは実Codex/Reviewer接続・Run証跡、Release build・5 checks、testChannel API成功、必要なpush/PR経路、SHA整合、main不変を確認する。成功PRはOPENで終了し、最終マージは人間が担当する。 |
| 独立追跡 | [#105 testhost接続問題](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/105) | Open・タイトルP2。M1完了条件外。 | Work環境の制約をCI/別Windows環境の結果と分けて調査。解消を他開発の前提にしない。 |
| 後続候補 | [#36 Slackライフサイクル通知](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/36) | Open・Milestone/priorityなし。現計画では機能拡張。 | #109はtestChannelへの最小API成功だけを確認。スレッド/broadcast/複数Bot設計は#36の個別範囲。 |
| 後続候補 | [#77 MCP実行環境パス](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/77) | Open・Milestone/priorityなし。今回の対象外。 | 既存の固定CLIパスで不足が再発・拡大した場合に再優先付けする。 |

完了済み前提は[#84](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84)、[#79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)、[#80](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80)、[#107](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/107)、[#108](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/108)。checkout/worktreeと実Git回帰はClosedの#51/#54等を再利用し、重複実装しない。

### 3.3 Milestone 1の最小完了条件（提案）

1. #84/#79/#80/#107/#108はそれぞれの受入条件・証跡により完了済み。#80の元Run原因未確定は残存リスクとして保ち、原因特定を遡及的な必須条件にしない。
2. 通常PRでは対象Windows・Ubuntu必須CI（UnitTests/FakeTests）と独立レビューを確認する。変更範囲が要求しない実外部IntegrationTestsやAI判断品質を重複ゲート化しない。
3. #109を一度実施し、CLI経由の実Codex起動、Implementer/Reviewer接続、Run ID/対象SHA、Release build・Testbed 5 checks、専用testChannelへのSlack API成功、必要な専用一時ブランチのpush/PRと証跡のhead/base SHA一致を記録する。GitHub公開経路はIssue本文の元の要求を満たすのに必要な範囲を保ち、実施前にその必要十分性を明記する。
4. Testbed mainは不変、既存ブランチ/PRは無変更、force pushなし、成功PRはOPENで終了とする。E2Eで自律マージや実マージを試験しない。失敗時は外部状態を自動削除/クローズせず、Codex未開始と開始後失敗を区別する。
5. Run ID、対象SHA、PR、CI/Reviewerコメント、Slack API応答、最終状態を相互に追跡可能にする。Reviewerの自然言語判断品質、Slack画面到達確認、#36全体はこのゲートに含めない。

自律マージはMilestone 1の完了条件から除外する。Issue #109本文・受入条件は変更しない。testChannel、権限、対象SHAを安全に確認できない場合は実E2Eを開始せず準備阻害として扱う。#105解決は他の開発やM1完了の前提にしない。

## 4. Milestone 1では必須にしない後続候補

これらは自己改善の最小サイクルを成立させるための必須条件から外す提案である。価値がないという意味ではなく、基本経路の実証後に優先度を判断する。

| 後続候補 | 扱い・既存課題 |
| --- | --- |
| 自律マージと承認モデル | Milestone 1では実装しない。Reviewer/Approver/Orchestratorの責務分離、複数レビュー結果の統合、残存リスク、権限・証跡、人間委譲を将来設計として検討する。初期案は本書3.1に記載し、正式決定ではない。 |
| 高度な自動リトライ・復旧 | 最初は失敗理由と人間の次アクションを明確にする。安全な再試行条件や上限を別途設計する。 |
| 複数Issue処理、並列エージェント、Worktree自動割当 | 単一Issueの実行・証跡・PR経路が安定してから検討する。 |
| 自動分析・診断エージェント | まずは記録された証跡から人間が振り返れることを優先する。 |
| 実行結果を横断集約するDB/トラッカー | 実行数・検索ニーズを確認してから永続化の要否を決める。初期はRun IDと保存済み成果物で足りるかを評価する。 |
| 完全自律のIssue提案 | 人間が改善Issueを選ぶ運用を維持し、提案の有用性・誤提案時の扱いは後続で検討する。 |
| 通知の高度な分析・ダッシュボード | #36の実行単位通知と人間に必要な情報を先に満たす。 |
| Issue #77 MCP環境パス調査 | [#77](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/77)はOpen。固定絶対パスによる現在の疎通を妨げないためMilestone 1の必須候補から外し、後続で扱う。実行環境差が再発・拡大した場合に優先度を見直す。 |

既存課題の対応範囲を確認し、必要なら既存Issueを参照・更新する。**同じ目的のIssueを重複作成しない。** 特に#45/#47/#51/#54と#79/#80の境界は着手前に確認する。

## 5. 境界・リスク・未確認事項

### Milestone 0とMilestone 1の境界

- Milestone 0は接続・契約・Fake run・実エージェントのスモーク検証までを記録する。
- Milestone 0では実PR作成に至っていないため、自己改善をPRまで完了させる基盤はMilestone 1で扱う。
- Slack設定・Testbed E2E対応はPR #82・Testbed PR #2、PowerShell 5.1文字コード修正はPR #73でマージ済み。設定JSONのローカル配置も確認済み。ただしRun `061cc78e2951423b9488cf58ffa36c98` は通知disabledで送信0件であり、設定実装済みと実送信検証済みを区別する。

### 主なリスク

- #80の元Run原因は未確定。CRLF/末尾空白の限定検証成功と、元Runの原因未特定を分けて保持する。
- #105はWork環境のtesthost接続問題で、GitHub Actions Windows/Ubuntu必須CIとは別の事象。#109の実行場所と証跡は明記する。
- #109はTestbed実Codex、testChannel、専用ブランチpush、PR作成を含む。事前条件・権限・対象SHAを副作用前に検査し、送信先や権限が安全に確認できなければ実行しない。
- #36のスレッド化/broadcastは独立した将来要求であり、#109の最小通知API確認に混ぜない。
- 2026-11-20時点で#79/#80/#84/#107/#108はClosed、#109/#105/#36/#77はOpen。Milestone紐付け・priority labelsは未設定。最新のIssue/PRを再確認する。

## 6. 現在の判断状況

2026-11-20の方針変更を本書および状況報告に記録する。自律マージ機能の新規Issueは作成せず、Issue #109の本文・状態・受入条件、他の既存IssueおよびGitHub Milestoneは変更しない。Milestone 1では人間がPRの最終承認とマージを行う。

## 7. 参照資料

- [Issue #36 — Slackライフサイクル通知](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/36)
- [Issue #77 — MCP実行環境ごとのローカルパス検証](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/77)
- [Issue #79 — Implementerの検証証跡受け渡し](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)
- [Issue #80 — PR必須Runのpush前停止調査](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80)
- [Issue #84 — 実Codex子プロセスのDOTNET_CLI_HOME書き込み拒否](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84)
- [PR #82 — Slackユーザー設定の配置とE2E通知モード](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/82)
- [Testbed PR #2 — E2E通知モードとシナリオ文書](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator-testbed/pull/2)
- [PR #73 — PowerShell 5.1文字コード修正](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/73)
- 関連確認対象: [#45](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/45)、[#47](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/47)、[#51](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/51)、[#54](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/54)
- [Ateliers ドキュメント作成ガイドライン](https://github.com/Ateliers-Dev/ateliers-website/blob/main/docs/development-guidelines/documentation-guidelines.md) — Feature/Referenceの目的と粒度、実装済みと計画中の区別、Docusaurus文書の構成方針を参照。本書はFeature/Reference仕様書ではなく、次の開発判断へ渡す計画・引継ぎ文書として作成した。










