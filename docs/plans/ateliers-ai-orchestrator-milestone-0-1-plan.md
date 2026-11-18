# Ateliers AI Orchestrator 開発計画

> 状態: Milestone 0完了記録・Milestone 1計画案（必須範囲・受け入れ条件は次チャットで最終決定）  
> 作成日: 2026-11-18  
> 対象: Ateliers-Dev/ateliers-ai-orchestrator

## 1. この文書の目的

今回のMCP→CLI連携と実エージェント検証を **Milestone 0** として記録し、その確認結果から次の **Milestone 1: 自己改善基盤** の計画案を整理する。

以前の「Phase 2」は技術検証の区分名として扱う。Issue管理と開発計画ではMilestone番号を使い、**Phase 2とMilestone 0を同じ種類の番号として混同しない**。Milestone 0はPhase 2相当の取り組みを含む完了記録であり、Phase 2自体の名称を置き換えるものではない。

本書では情報を次のように区別する。

- **確認済み**: 実行報告またはGitHub上のIssue/PRで根拠を確認した内容
- **実装済み**: PRのマージは確認済みだが、実環境の動作確認とは区別する内容
- **提案（未確定）**: Milestone 1の必須候補、順序、受け入れ条件。GitHub Milestone登録前の案
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

### 3.1 中心目標（提案）

オーケストレーター自身の改善Issueを実エージェントへ委譲し、Implementerの変更を独立Reviewerが確認し、検証後にPR作成まで完了する。人間はSlack通知とPRから実行目的・成果・失敗理由・次のアクションを把握し、最終レビューとマージ判断を行う。

**自動マージは対象外**とする。PR作成後も、人間が差分と確認結果を見てマージを判断する。

### 3.2 必須候補と提案する順序

[#84](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84)と[#79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)はMilestone 1の必須対応とする。その他は**必須候補（未確定）**であり、GitHub Milestoneへの登録や対象Issueの確定はまだ行っていない。順序は依存関係を示す案で、計画を確定するチャットで調整する。

| 順序 | 必須候補 | 既存課題との関係 | 提案する受け入れ条件 |
| --- | --- | --- | --- |
| 1 | **実Codex子プロセスの .NET ビルド環境を直す** | [#84](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84)。[#79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)の証跡確認の前提となる。 | `DOTNET_CLI_HOME` の設定元・実パス・生成時期・書込権限・子プロセスへの継承を診断し、直接原因と再現条件を記録する。MCP→CLI→実CodexのRun中にImplementer自身がRelease buildと対象チェックを成功させる。人間や別プロセスの後追い環境変更に依存しない。 |
| 2 | **PR必須経路の停止原因を特定し、必要な修正を行う** | [#80](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80)。関連する[#45](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/45)、[#47](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/47)はGitHub上でClosed。 | `push.secret-scan-and-whitespace-check` の直接原因と再現条件を記録する。dotnet復元先、`.git/index.lock`、隔離環境、ログ配置は別々に切り分け、未確認の因果を断定しない。安全な検証用リポジトリで同等のPR必須Runを行い、チェック通過後のReviewer・push・PR作成までを確認する。GitHubへの副作用を明示する。 |
| 3 | **clean checkout/worktreeと生成ログ・成果物の配置を整理する** | #80のclean判定・clone内ログの観測に対応。[#51](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/51)と[#54](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/54)はClosed。必要なら完了済みIssueを仕様・経緯の参照として使い、重複作業を避ける。 | 作業用checkout/worktreeの作成・clean判定・後片付けの責務を定義する。生成ログ・成果物が対象checkoutを意図せずdirtyにしない配置とし、失敗時も診断と証跡を残す。実Git回帰確認でclean/dirtyと生成物の期待動作を示す。 |
| 4 | **ImplementerからReviewerへの検証証跡の引き継ぎ** | [#79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)。#84でImplementerの検証が実行できる状態になった後に、Run中の証跡受け渡しを確かめる。 | Reviewerが実行コマンド、終了コード、ログまたは安全な参照先、対象コミット/差分、成果物を確認できる。欠落時は不足を明示し、Implementerの自己申告だけで成功と扱わない。Testbed実エージェント回帰Runで、証跡をReviewerが確認して判定できることを確かめる。 |
| 5 | **IssueからTaskContractを作り、実行を依頼する最小手順を定型化する** | 今回はWorkが契約作成を補助した。既存Issue本文とTaskContract Guide/Schemaを使い、必要なら既存Issueへの追記範囲を判断する。現時点でこの項目の新規Issueは作らない。 | 人間が対象Issueと作業範囲を指定し、Guide/Schemaに適合するTaskContractを作成・検証し、合意された操作範囲でRunを開始できる手順を記録する。契約に必要な情報、未確定事項、停止条件が明確で、過剰な自律的Issue解釈を避ける。 |
| 6 | **Issue #36に沿ったSlack通知の内容・スレッド化** | [#36](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/36)はOpen。通常通知の内容とスレッド要件を扱う。MCP通知モード指定の責務は後述の検討事項として切り分ける。 | 実行単位のスレッドに通知がまとまり、Bot名義・通常/テスト通知先の要件を保つ。開始・終了通知から目的、Issue/対象、成果、検証状態、失敗理由、次の人間の操作が分かる。テストチャンネルE2Eで通常チャンネルへの誤送信がない。Slack無効・設定不足・API障害でもコア処理が続く。 |
| 7 | **自己改善E2Eと一連の証跡を確保する** | #84、#80、#79、#36とTaskContract手順に依存。既存Issueを使うか、小規模な対象Issueを別途選ぶ。 | オーケストレーター自身の安全な改善Issueを指定し、Contract作成、実Implementer、独立Reviewer、証跡確認、検証、PR作成、Slack報告までを1 Runとして追跡する。Run ID、対象SHA/差分、検証結果、PRリンク、失敗や人間介入を確認可能にする。マージは人間に残す。 |

**進め方の依存関係:** まず#84の実Codexビルド環境と#80のPR必須経路を安定させ、clean checkout/worktreeの扱いを確立する。その上で#79の証跡引き継ぎとIssue→TaskContract手順を整え、#36の通知内容・スレッド要件を実装・確認する。最後に一連の自己改善E2Eで各要素を確認する。#36の情報設計やTaskContract手順は並行して検討できる。Milestone 1の開始順と受け入れ条件は未確定である。

### Milestone 1の要検討事項（必須か未確定）

以下2点は現時点で判断を保留する。**この計画更新で新規Issueは作成しない。**

| 検討事項 | 確認すること |
| --- | --- |
| Slackの `testChannelId` と通常通知先の重複 | 同じチャンネルを許容するか、テスト時は通常通知先との分離を要求するかを決める。現状は設定上重複が確認され、誤送信回避のためテスト通知を実行していない。 |
| MCP経由でSlack通知モードを指定する方法 | `TaskContract` に持たせるか、MCPツール引数として指定するかを決め、#36のライフサイクル通知要件との責務分担を整理する。今回の `run_orchestration` は通知モードを指定できずdisabledだった。 |

### 3.3 Milestone 1の完了条件（提案）

下表で、Milestone 1の必須対応として扱うIssueと、必須候補・横断条件を受け入れ条件に対応づける。#84・#79は必須対応として合意済み。その他のIssueと横断条件は候補で、完了条件を含むMilestone 1の全体範囲は次チャットで確定する。

| Issue / 条件 | 区分 | 対応する受け入れ条件 |
| --- | --- | --- |
| [#84](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84) 実Codexの `DOTNET_CLI_HOME` 書き込み拒否 | 必須対応 | Implementer自身がRun中にRelease buildと対象チェックを成功させる。原因・再現条件を記録し、後追いの人手変更に依存しない。 |
| [#79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79) Reviewerへの検証証跡引き継ぎ | 必須対応 | Reviewerが検証証跡を確認し、不足・未実施を成功と誤認しない。 |
| [#80](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80) PR必須経路の停止調査 | 必須候補 | 停止原因・再現条件を説明し、安全な検証範囲でReviewer・push・PR作成までを確認する。 |
| [#36](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/36) Slackライフサイクル通知 | 必須候補 | 実行単位の通知、役割別Bot、終端通知と人間の次アクションを確認する。テスト先の重複可否とMCP通知モード指定は別途決定する。 |
| clean checkout/worktree・Issue→TaskContract・自己改善E2E | 横断候補 | 作業領域と証跡を保全し、Issueから実行契約を作成して、Run・検証・PRまでを追跡できる。 |

以下は対応表の各項目を含むMilestone 1の計画案としての完了条件であり、次チャットで必須範囲を確定する。

- #84の課題を解消し、実Codex ImplementerがRun中にRelease buildと対象チェックを実行できる。
- #80のPR必須経路で停止原因を説明でき、合意した安全な検証範囲でReviewer・push・PR作成までの証跡がある。
- clean checkout/worktreeとログ・成果物の配置について、意図しないdirty化を防ぎ、失敗時も必要な証跡を追える。
- Reviewerが#79の検証証跡を確認でき、不足や未実施を成功と誤認しない。
- Issue→TaskContractの最小手順を再現可能な形で記録する。
- #36に沿った通知で、実行単位の経緯・成果・失敗理由・人間の次の操作を確認できる。
- Slackテスト送信とMCP経由の通知モード指定について、採用する方式を決定してテスト可能な状態にする。通知モード方式と通知先の重複可否は計画確定時の検討事項であり、決定前に実装方針を固定しない。
- オーケストレーター自身のIssueを使ったE2Eで、Run ID、対象SHA、証跡、検証結果、PRを引き継げる。人間が最終レビュー・マージを行い、自動マージはしない。


## 4. Milestone 1では必須にしない後続候補

これらは自己改善の最小サイクルを成立させるための必須条件から外す提案である。価値がないという意味ではなく、基本経路の実証後に優先度を判断する。

| 後続候補 | 扱い・既存課題 |
| --- | --- |
| 高度な自動リトライ・復旧 | 最初は失敗理由と人間の次アクションを明確にする。安全な再試行条件や上限を別途設計する。 |
| 複数Issue処理、並列エージェント、Worktree自動割当 | 単一Issueの実行・証跡・PR経路が安定してから検討する。 |
| 自動分析・診断エージェント | まずは記録された証跡から人間が振り返れることを優先する。 |
| 実行結果を横断集約するDB/トラッカー | 実行数・検索ニーズを確認してから永続化の要否を決める。初期はRun IDと保存済み成果物で足りるかを評価する。 |
| 完全自律のIssue提案 | 人間が改善Issueを選ぶ運用を維持し、提案の有用性・誤提案時の扱いは後続で検討する。 |
| 自動マージ | 明示的にMilestone 1の対象外。最終レビュー・マージ判断は人間が行う。 |
| 通知の高度な分析・ダッシュボード | #36の実行単位通知と人間に必要な情報を先に満たす。 |
| Issue #77 MCP環境パス調査 | [#77](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/77)はOpen。固定絶対パスによる現在の疎通を妨げないためMilestone 1の必須候補から外し、後続で扱う。実行環境差が再発・拡大した場合に優先度を見直す。 |

既存課題の対応範囲を確認し、必要なら既存Issueを参照・更新する。**同じ目的のIssueを重複作成しない。** 特に#45/#47/#51/#54と#79/#80の境界は着手前に確認する。

## 5. 境界・リスク・未確認事項

### Milestone 0とMilestone 1の境界

- Milestone 0は接続・契約・Fake run・実エージェントのスモーク検証までを記録する。
- Milestone 0では実PR作成に至っていないため、自己改善をPRまで完了させる基盤はMilestone 1で扱う。
- Slack設定・Testbed E2E対応はPR #82・Testbed PR #2、PowerShell 5.1文字コード修正はPR #73でマージ済み。設定JSONのローカル配置も確認済み。ただしRun `061cc78e2951423b9488cf58ffa36c98` は通知disabledで送信0件であり、設定実装済みと実送信検証済みを区別する。

### 主なリスク

- #80のpush前チェック停止の直接原因は未確定。記録されたdotnet/Gitアクセス問題との因果関係を決めつけず、再現と切り分けが必要。#84の `DOTNET_CLI_HOME` 書き込み拒否も直接原因・再現条件が未確定である。
- 作業checkout内にログや生成物を置くとclean判定を妨げる可能性がある。診断ログの保全とclean状態の両立を設計する。
- #79の証跡共有は、Reviewerの独立性や権限境界を弱めずに実現する必要がある。
- Slack通知は複数Bot、実行順序、重複投稿、親投稿失敗、再起動時の状態などを含む。#36に記載されたSlack API/状態管理の技術検討を省略しない。
- GitHubのIssue状態は2026-11-18に照合した。#36、#77、#79、#80、#84はOpen、#45、#47、#51、#54はClosed。#82とTestbed PR #2はマージ済み。Milestone 1着手時は本文・コメント・変更履歴を再確認し、完了済みIssueへの重複対応を避ける。

## 6. 次チャットで最終承認する項目

1. Milestone 1の必須候補と後続見送りの分類、および正式な優先順位。
2. #84、#80、#79、#36およびclean checkout/worktreeの必須範囲と依存順序。Closedの#45/#47/#51/#54とは重複起票しない。
3. Issue #36と完了済み#45/#47/#51/#54の成果をどう利用するか。
4. 自己改善E2Eに使うIssue、対象リポジトリ、外部書き込み許可範囲、および証跡の保存先。
5. SlackのtestChannelId重複を許容するか、MCP経由の通知モードをTaskContractとMCPツールのどちらで指定するか、および#36との責務分担。
6. Milestone 1完了判定と、PR作成後に人間が確認する情報。

この文書だけではGitHub Milestone作成やIssue変更を行わない。これらは上記の計画を次チャットで確定した後に判断する。

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










