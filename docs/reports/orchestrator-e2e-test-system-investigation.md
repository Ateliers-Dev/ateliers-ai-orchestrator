# Ateliers AI Orchestrator E2E・テスト体系の独立調査

> 作成日: 2026-11-19  
> 更新日: 2026-11-19  
> 作成者: Codex

## 1. 要約

**ユーザーが明示した設計意図**は、現在リポジトリにあるE2Eスクリプトを「ユーザー本人がCLIとMCPをインストールした後に最終受け入れ判断を行うためのもの」とし、開発段階やWork/Codexワーカーの作業には使わない、というものです。本報告ではこれを前提に、既存スクリプトの目的を変えず、開発用検証を別経路に置く案を示します。

**現行mainで確認したこと**:

- `scripts/e2e/run-codex-e2e.ps1` はTestbedを対象にした実Codex・GitHub公開操作込みの受入ランナーです。Release構成のCLIをソースからビルドして直接起動します。インストール済みCLIやMCPはこのスクリプト自体の前提ではありません。
- 実行時は認証済みGitHub CLI、Testbedへのpush/PR権限、実Codex CLIとその認証が必要です。Slackを明示的に有効化した場合は設定JSONとBot tokenを使います。`-SlackNotifications test` ではSlack API送信に加えて、run IDが届いたことを人間が確認するゲートがあります。
- 一方、`CodexIntegrationTests` にはソースビルド済みCLI assemblyと実Codexを使う、GitHub公開操作をしない一時GitリポジトリのIntegrationTestがあります。これは開発用の候補ですが、現状の契約は小さなファイル変更だけで、build/test証跡をReviewerへ引き渡す#79の回帰確認にはなっていません。
- CIはFake-only UnitとSlack Unitを実行し、実Codex、実Slack、GitHub書込みE2Eを実行しません。
- Issue #79はopen、PR #96はopen・未マージ、PR #103はmergedです。#96は証跡受け渡しの実装案を含みますが、そのPR本文自身が実エージェントTestbed回帰を未完了と記録しています。#103はReviewer応答形式の上限付き再試行であり、E2Eの対象者・役割分担を変える変更ではありません。

**推奨**: 既存のTestbed E2Eはユーザー専用の最終受入に留め、#79の実装・回帰確認は「CI内のFake/契約検証」と「必要時のソースビルドCLI＋実Codex、ローカル隔離fixture」の二段に分けます。後者はGitHub Testbed、GitHub認証、PR、MCP、インストール済み配布物、ブラウザ確認に依存させません。SlackはE2Eの都合で通知を無効化する変更を提案せず、通知設定・契約で指定されたモードをそのまま扱います。

## 2. 調査範囲と方法

Ateliers-Dev/ateliers-ai-orchestrator の現在の `main`、リポジトリの `AGENTS.md`、README、E2E共通手順・Testbedシナリオ、スクリプト、Codex IntegrationTests、CI定義を読みました。Issue #79/#90/#53/#55とPR #96/#103の現在の状態・本文も確認しました。作業ツリー内に対象リポジトリのcheckoutが見つからなかったため、GitHub接続で現行mainとPR・Issueを読み取りました。**E2Eスクリプト、テスト、インストール、push、PR、Slack通知は実行していません。**

### Skill / 作業指示

- リポジトリの `AGENTS.md` は、通常PRの検証でFake-only Unitを品質ゲートとし、フルE2Eを一律必須にしない方針です。E2Eは変更影響・Issue受け入れ条件・マイルストーン・リリースなど必要性に応じるとしています。また、Slack通知ONのTestbedレビューサイクルには `test` を使い、Slack設定不足を理由に通知を無効化して続行しないよう指示しています。([AGENTS.md: テストとE2E/Slackの指示](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/AGENTS.md#L43-L53), [AGENTS.md: 品質保証・テスト](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/AGENTS.md#L111-L144))
- Ateliers AI SkillsのカタログにはIssue登録、文書レビュー、PRレビューなどの共通Skillが載っていますが、Orchestrator専用のE2E実行Skillは見当たりません。Issue登録Skillは、重複確認後にタイトル・本文等を提示し、明示承認を得てから投稿する規約です。今回は調査報告のみとし、Issueの更新・登録は行っていません。([ateliers-ai-skills README](https://github.com/Ateliers-Dev/ateliers-ai-skills/blob/main/README.md#L1-L20), [ateliers-github-issue SKILL.md](https://github.com/Ateliers-Dev/ateliers-ai-skills/blob/main/skills/ateliers-github-issue/SKILL.md#L49-L77))
- 既存E2E資料には「確認文の完全一致」「明示承認済みの副作用は再承認不要」等がありますが、ユーザーの今回の明示は**ワーカーが既存E2Eを開発用に使う許可ではなく、使用対象者をユーザー本人の最終受入に限定する設計意図**として扱います。([AGENTS.md: 承認済みE2EとSlack通知](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/AGENTS.md#L43-L53))

## 3. 現行のテスト層

| 層 | 呼出し元・実行主体 | 外部依存・副作用 | 現在確認する内容 | 開発用適性 |
|---|---|---|---|---|
| Fake-only Unit | xUnit `Category=Unit`、PR CI | Fake中心。通常のCIでCodex・モデル・GitHub認証を使わない | Core状態・保存処理、CLI/Codex境界、証跡関連ロジック | 標準の開発/CIゲート |
| ローカルGit Integration | UnitTestsプロジェクトの`Integration` category | 実Gitの一時repo/worktree。GitHub・実Codexなし | Publisher等のローカルGit境界 | 軽量な実Git検証 |
| 実Codex IntegrationTest | `tests/Ateliers.Ai.Orchestrator.IntegrationTests`の明示opt-in | 認証済みCodex CLI、モデル利用枠/費用可能性、ローカル一時Git repo | Implementer→Reviewer、実行記録、終了証跡、固定成果物等 | #79用の土台はあるが、現契約は証跡受け渡しの回帰を証明しない |
| Slack IntegrationTests | `Ateliers.Slack.IntegrationTests`の明示opt-in | Slack API、Bot token、`testChannelId`。テスト用Webhookなら別opt-in/専用URL | Slack APIへの投稿と成功応答 | Slack連携そのものの実サービス確認 |
| Testbed最終受入E2E | ユーザーが`run-codex-e2e.ps1 -RunCodex`を実行 | Testbed public repo、`gh`認証/push/PR/comment、実Codex、Release build・チェック。Slack ONならAPI通知と目視確認 | 実Codex実装→独立Reviewer→必要な修正→公開PR/コメント→GitHub/テスト結果照合→人間判断待ち | **ユーザー専用。ワーカーの開発検証には使用しない** |
| MCP/配布物の最終受入 | インストール済みMCP/CLIを使うユーザーの別の受入作業 | 実際のインストール・MCP登録・環境設定 | 配布物とMCP経由の導入・接続・利用確認 | 既存Testbedスクリプトが現在保証している範囲とは別 |

CI定義はWindows/Ubuntu matrixでrestore、Release build、Fake-only Unit、Slack Unitを実行し、実Codex/GitHub書込みを含みません。`AGENTS.md`も`Integration` categoryとCodex IntegrationTestsを通常CIから除外すると明記しています。([unit-tests.yml](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/.github/workflows/unit-tests.yml#L8-L43), [AGENTS.md](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/AGENTS.md#L135-L144))

## 4. 既存Testbed E2Eの実際の前提・判定項目

共通手順はWindows PowerShell 5.1以降、.NET SDK 10、Git for Windows、GitHub CLIを要求し、GitHub CLIは対象repoへpush/PR可能であることを求めます。`-RunCodex`ではCodex CLIが追加で必要です。スクリプトは `src/Ateliers.Ai.Orchestrator.Cli/bin/Release/net10.0/Ateliers.Ai.Orchestrator.Cli.exe` を実行対象としており、冒頭でソリューションをrestore/buildします。よって、これは配布CLIやMCPのインストール確認ではありません。([E2E README: 準備確認・実行](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/docs/reference/e2e/README.md#L18-L53), [run-codex-e2e.ps1: CLI build](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/scripts/e2e/run-codex-e2e.ps1#L188-L220))

| 項目 | 現行Testbed E2Eで必要か | 根拠・意味 |
|---|---|---|
| インストール済み`v0.0.4-beta.3`等のCLI | 不要 | ローカルのRelease buildを使う。インストーラー/配布物の検証は別層。 |
| MCPのインストール/登録 | 不要 | スクリプトがRelease CLI実行ファイルを直接呼ぶ。MCP経由ではない。MCPの導入受入を代替しない。 |
| .NET SDK 10、Git、PowerShell | 必要 | ローカルRelease build、clone、スクリプト起動のため。 |
| GitHub CLI (`gh`) | 準備確認でも必要 | `gh auth status`、Testbed default branch/HEAD確認、最終PR状態確認に使う。準備確認では書込みしないが認証状態を要求する。 |
| GitHub認証 | 必要 | Testbed `main`情報取得。実行時はTestbedへのpush/PR/コメントに必要な権限が必要。 |
| Codex CLI + Codex認証 | `-RunCodex`時に必要 | 実Codex/モデル実行。認証ファイルはスクリプトがコピーせず、`CODEX_HOME`またはユーザー既定homeを利用。費用可能性あり。 |
| Slack config/secrets JSON | Slack通知を`test`/`auto`にした場合のみ | `slack.json`/`slack.secrets.json`を読み込み、Bot tokenと送信先を事前確認。既定モードは`disabled`。 |
| Slack API | Slack通知を有効にした場合 | Bot identity/authと投稿成功数を検証。`test`は`testChannelId`、`auto`は通常`channelId`。通常通知先との取り違えを拒否。 |
| Slack UI/ブラウザ確認 | Slackを有効にした場合は必要 | 送信成功数とは別に、選択チャンネルでrun IDを目視確認し、人間が確認文字列を入力する。現行スクリプトはブラウザを自動操作しない。 |
| 人間の開始承認 | 実Codex開始に必要 | 公開repo上でのCodex利用とpush/PR/comment、費用可能性を示し、確認文完全一致を要求。 |
| 人間による最終判断 | 成功後も必要 | PRはOPENのまま、Testbed `main`へmergeしない。人間がPRを確認する。 |
| 実行証跡・未完了時の保持 | 実Codex開始後に必要 | 失敗・中断・不一致ではfixture/ログ/可能性のあるGitHubブランチ・PR状態を保持して報告。 |

準備確認はTestbedのcleanな一時clone、default branch SHA、基準Release buildと5チェック中の既知2件の失敗を確認し、単体テストは実行しません。実行経路はrunごとのbranchをpushし、PR・Reviewer結果コメント証跡を確認した後、スクリプト側でもRelease buildと全5チェックを再実行します。Slackを有効にした場合だけ通知成功と人間の到達確認が追加合格条件になります。([E2E README](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/docs/reference/e2e/README.md#L23-L62), [Testbedシナリオ](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/docs/reference/e2e/testbed-review-cycle.md#L18-L45), [スクリプトの公開操作・確認・証跡](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/scripts/e2e/run-codex-e2e.ps1#L315-L475))

### Slack APIと画面確認の境界

プログラムが確かめられるのは、Slack Bot認証/API応答、送信成功数、宛先channel ID、投稿APIが返す識別情報（例: timestamp）と要求したrun ID等の内容です。Slack UI上でメッセージが見えること、意図したチャンネルを人が見ていること、運用上期待した見え方であることは別の人間受入項目です。現在のTestbed E2Eは後者を明示的に合否条件にしています。開発用の#79回帰確認でこのUI条件を必須にする必要はありません。Slack通知そのもののAPI契約確認が必要なら、Slack IntegrationTestを分けて使えます。通知を無効化してSlack要件を迂回するのではなく、対象検証にSlackが含まれるかと設定された通知モードを明示して、API検証と人間画面確認を混同しないようにします。([E2E README: 通知モードと到達確認](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/docs/reference/e2e/README.md#L46-L62), [README: Slack IntegrationTests](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/README.md#L186-L219))

## 5. #79の開発用検証に流用できるものと不足

`CodexIntegrationTests.RunAsync_ShouldCompleteImplementerReviewerAndPersistNeedsHumanInput` は明示opt-inの実Codexテストです。CLIはテスト実行環境にあるDLL、Release/Debug build出力から探索し、`dotnet <cli.dll>`で起動します。fixtureは一時ディレクトリにseed Git repo、runs、contractを作ります。契約は`pullRequestExpected=false`で、外部push/PRを除外しています。検証はImplementer/Reviewer各1回、終了証跡、events/report、固定差分、対象ファイルのみの変更等です。([CodexIntegrationTests](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/tests/Ateliers.Ai.Orchestrator.IntegrationTests/CodexIntegrationTests.cs#L118-L180), [CLI解決とfixture契約](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/tests/Ateliers.Ai.Orchestrator.IntegrationTests/CodexIntegrationTests.cs#L332-L345), [fixture/contract](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/tests/Ateliers.Ai.Orchestrator.IntegrationTests/CodexIntegrationTests.cs#L513-L590))

ただし、現fixtureの要求は`target.txt`にmarker行を追加する小さなタスクです。build/testを実行して記録する指示がなく、Reviewerがその実行結果を読むことも合格条件にありません。よって「Implementer→Reviewerが動く」はある程度確認できますが、「Implementerの実build/test証跡が構造化されReviewerに渡り、Reviewerがそれを根拠に独立判定できる」という#79の条件を満たした証明にはなりません。

Codex IntegrationTestは`ATELIERS_RUN_CODEX_INTEGRATION_TESTS=1`とCodex CLI絶対パスが必要で、実行するとモデルを使います。Slackは別opt-inです。現在のテストでは設定不足等を理由にSlack部分をSkipし、Slackを試すときは`testChannelId`へ送信し、3役のBot identityを照合します。この既定挙動はテストコードの事実として記録し、提案する開発検証ではSlack通知を無効化して進めることを解決策にしません。([README: Codex/Slack opt-in](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/README.md#L153-L219), [CodexIntegrationTests: Slack条件と実行](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/tests/Ateliers.Ai.Orchestrator.IntegrationTests/CodexIntegrationTests.cs#L118-L170))

### PR・Issueの状態

- **#79 — open**。求めるのはビルド・テスト証跡の最小セット、保存・参照・欠落時の扱い、Reviewer独立性、Testbed同等ケースでの回帰確認です。追加再現では、Issue起票時点の実Runでビルド証跡ができずReviewerが証跡不足としたこと、後追いbuildはReviewer未確認であることを明記しています。([Issue #79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79))
- **PR #96 — open / 未マージ**。validation証跡JSON・ログ、path/schema検査、Reviewerへの引き渡し、Fake Codexテスト等を提案しています。PR本文では全体Fake-onlyテストのローカル完了を確認できず、実エージェントTestbed回帰試験もclean-checkoutで停止してImplementer/Reviewer未起動のため、#79をcloseしないと記録しています。したがって実証完了の根拠には使えません。([PR #96](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/96))
- **#90 — open**。実Codex起動前のWindows sandbox/shell policy、Run専用path、環境変数、.NET/NuGet前提の事前診断が目的で、#79の証跡引き継ぎは明示的に対象外です。開発用検証で環境阻害を減らす隣接課題ですが、別の受け入れ目的です。([Issue #90](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/90))
- **#53 — open**。専用repoを使う認証付きGitHub E2E受入手順、明示承認、push/PR/comment証跡、誤repo防止、失敗・結果不明時の確認が目的です。現在のTestbed E2Eの最終受入層と関係が深く、ローカル開発用#79検証とは役割が異なります。([Issue #53](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/53))
- **#55 — open**。実環境検証の選択基準・権限委譲・コスト・QA責務等の広い設計検討です。個別の#79回帰条件を本Issueに広げず、方針全体の決定が必要な場合だけ関連づけるのが適切です。([Issue #55](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/55))
- **PR #103 — merged**。不正なReviewer応答形式を承認扱いせず、固定成果物に対する再試行を上限管理する変更です。E2Eスクリプトの役割や使用者は変更しません。([PR #103](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/103))

## 6. 提案: 検証段階を分け、既存受入E2Eの役割を維持する

| 段階 | 実行主体・対象 | 成功条件 | 権限/外部依存 | 未検証事項・停止条件 |
|---|---|---|---|---|
| 1. CI/Fake回帰 | 開発者・通常CI。既存Fake-only Unitに#79の証跡検証ケースを加える | 検証JSON/manifestのスキーマ、path containment、log/reference/hash、欠落・不正・サイズ超過・secret mask時のfail-closed、Reviewer promptへの証跡参照、自己申告と実行記録の区別をテスト | source buildとローカルfixtureだけ。Codex、GitHub、Slack credential、MCPなし | 実Codexが証跡を読む/正しく解釈することは未確認。失敗したassert、外部I/O検出、期待外pathで停止 |
| 2. 実Codex・ローカル開発E2E | 明示的な開発用テスト実行者。新規独立IntegrationTest、または現`CodexIntegrationTests`から分離。ソースからRelease buildしたCLI DLL/実行ファイルを使用 | 一時Git fixtureでImplementerが指定されたRelease buildと対象チェックを実行し、コマンド・終了コード・ログ参照・対象revision/hashを保存。Reviewerが同じ固定差分と保存されたログを独立に確認し、欠落/不一致なら承認に進めず不足を明示。run/report/eventsに全呼出し・状態・証跡が残る | ローカルGit、一時ディレクトリ書込み、.NET SDK/restore source。実Codex CLI絶対pathと認証、モデル利用枠/費用の可能性。GitHub CLI・push/PR権限・MCP・配布版インストールは不要 | ネットワーク依存のrestore/モデル接続、Codex sandboxの権限不足、証跡保存失敗、timeout/取消、Reviewer不合格・応答形式不正は失敗/中断として保持・報告。GitHub公開操作やUI受入は未検証 |
| 3. API境界の個別検証 | 必要な外部IFだけを対象にする（Slack integration等） | Slackを検証対象にした場合、認証APIのidentity一致、投稿APIの成功応答、宛先channel ID、message/run ID等の戻り値を確認 | Slack設定JSON・Bot token、テスト送信先。テスト対象の選択に応じた外部通知 | UI/ブラウザ上の表示・人の閲覧確認は別項目。TestbedシナリオでSlack ONなら既定方針どおり`test`を使用。`auto`は通常通知先をユーザーが明示した場合だけ。失敗時に`disabled`へ切り替えて成功扱いしない |
| 4. ユーザー最終受入 | **ユーザー本人**。既存`run-codex-e2e.ps1`を必要な受入環境で実行 | インストール済みCLI/MCPを含め、ユーザーが定める受入範囲の各結果・証跡を確認し、PRやUI等の最終判断を行う | ユーザーが管理するインストール済みCLI/MCP、Codex/GitHub権限、必要なSlack JSON/認証、対象環境・費用判断 | 最終判断はユーザーに残す。未達/結果不明/証跡不足で停止し、実行後のbranch/PR状態を確認。既存Testbedスクリプトをワーカーへ委譲しない |

### 開発用E2Eの構成詳細

1. 既存`run-codex-e2e.ps1`から独立した、開発用IntegrationTest名と起動方法を用意します。少なくとも説明・ファイル名・契約で「ローカル開発用」「外部pushなし」と識別できるようにします。既存Testbedシナリオやその成功判定は書き換えません。
2. 現行IntegrationTestの`IsolatedFixture`パターンを再利用し、一時Git seed repoと実行証跡用ディレクトリを隔離します。cleanup安全性テストは有用ですが、実Codex実行前の単独ケースとして維持します。
3. 現行fixtureのmarker追加要求を、テスト対象の小さなビルド可能プロジェクトへ置き換えるか専用fixtureを作ります。Implementerに明示コマンドを実行させ、実際の終了コード・ログ・artifact/hashを記録します。結果の自己申告値だけを真実扱いしないよう、Orchestrator側の実行証跡と区別します。
4. Reviewer入力には固定diffのhashと証跡参照を結びつけます。テストはReviewer入力内の証跡が実在し、参照先の内容/hashが一致し、Reviewerがbuild/testの結果をレビュー根拠として使ったことを確認します。証跡を抜いたnegative caseは段階1のFakeテストで安価に確認します。
5. PR必須、publisher、push、GitHub API、Testbed cloneをこの開発用契約から除外します。MCP接続・インストール版CLIもこの段階の合格条件にしません。
6. 通知はテストの都合で無効化を強制しません。CLIの設定/契約上の通知方針を保持します。Slack API送信をこの回帰試験の対象とする場合は、承認・送信先・成功応答を独立項目として扱い、TestbedのSlack ON手順では`test`を選択し、`auto`は通常送信先をユーザーが明示した場合に限ります。画面確認を開発用Reviewer証跡試験の成功条件に混ぜません。
7. `README`とテスト文書に、起動主体、必要なCodex認証/モデル費用可能性、外部GitHub/Slack操作の有無、作成物の保持/cleanup、未検証範囲、失敗時の停止条件を明記します。実Codex opt-inテストは通常CIから除外したままにします。

## 7. 依存を減らす優先順位と移行案

### 優先順位

1. **役割の境界を記録**: E2E READMEとTestbedシナリオに「ユーザー本人の最終受入用。Work/Codexワーカーの開発検証には使用しない」と明記する方針をレビュー可能にする。準備確認モードも同じ扱いにするかを同じ方針に含める（ユーザーの設計意図は既存E2E全体のワーカー使用を対象外としているため、除外と読む）。
2. **#79の安価な回帰条件を先行**: PR #96の未マージ差分と既存Unit/Fake testを別途レビューし、証跡がReviewerへ安全に結びつくコア条件をFake-only CIで確認できる形にする。#79の実エージェント回帰はその上に置く。
3. **隔離されたソースビルド開発用E2Eを整備**: 現実装のCodex IntegrationTest fixtureを拡張/分離し、実build/test証跡とReviewer確認を検証する。ここではGitHub Testbed、push/PR、MCP、インストール済みベータ版、Slack画面を依存関係にしない。
4. **Slack/GitHub API境界は個別に選択**: Slack送信APIや認証付きGitHub push/PRの受入が必要な場合だけ、専用の外部IntegrationTest/ユーザー受入手順へ進む。Slack `auto`の明示選択や人間確認を勝手に省かない。
5. **インストール済みCLI/MCPは最終受入に限定**: 配布物や接続を最終判断するユーザーの環境で確認し、ソース開発用E2Eの完了条件に含めない。

### 移行手順

- **段階A（文書上の境界）**: 既存E2Eの「準備確認」「実Codex実行」「Slack確認」の各モードの対象者を区別し、開発用テストから呼ばないことを明記する。E2Eスクリプト自体は変更しない。
- **段階B（#79のテスト設計）**: 成功条件を「実ビルド/チェック結果の証跡を独立Reviewerが検証可能」と明確化し、Fake CIケースとローカル隔離Codex E2Eのそれぞれが証明する範囲を割り当てる。
- **段階C（開発用E2E）**: source build CLI + local fixture + Codex opt-inで回帰を行い、実行記録に必要情報を残す。GitHub Testbed、Slack UI、MCPに依存させない。失敗時は証跡を保持し、どの前提/停止条件に当たったか報告する。
- **段階D（ユーザー受入）**: 必要な配布版をユーザー本人がインストールし、MCP経由の接続と最終判断を行う。これは開発テストの成否と別の受入記録にする。

## 8. Issue分割案と重複回避

これは**起票案ではありません**。今回の作業ではIssueを作成・更新しません。

1. **#79に残す主目的**: Implementerのbuild/test証跡スキーマ、保存/参照、欠落時の扱い、Reviewerが独立に検証できること。#79の受け入れ条件を「Testbed最終受入E2E実行」と同義にせず、ローカルfixtureでの証跡受け渡し回帰で達成可能にする。既存Issueの更新が必要か、新規Issueにするかは、#79の状況とPR #96の独立レビュー後に判断する。
2. **開発用E2Eの新規Issue候補**（#79から分離する場合）: source-built CLIと隔離一時repoによる実Codexのbuild/test証跡受け渡し検証。外部GitHub操作、Testbed、MCP/インストール、Slack UIは対象外とし、実Codex opt-in、費用可能性、ローカル権限、成功条件・失敗保持を記載する。#79が開いている現状では、内容を#79の受入条件/後続作業として扱う方が重複を避けやすく、新規Issueは#79の範囲を独立させる判断がされた場合に限定する。
3. **#90は現状維持**: 実Codexのshell policy/Windows/.NET/NuGet事前診断。証跡引き継ぎや、既存E2Eの最終受入ロールをここへ移さない。
4. **#53は現状維持**: 専用repoのGitHub認証付きpush/PR/comment最終受入の手順。開発用ローカルE2Eを#53へ追加しない。
5. **#55は方針レベルに限定**: 検証層の選択・役割・権限・コスト全体の原則。今回の具体的fixtureや#79回帰の実装課題は別に扱う。

## 9. 結論

### 事実

現在のTestbed E2EはローカルRelease buildから実行する公開GitHub・実Codexの受入フローであり、配布CLI/MCPのインストール確認や#79のローカル開発回帰とは別の検証です。GitHub権限、実Codex、Slack（選択時）、人間の確認まで複数の外部条件を持ちます。別の実Codex IntegrationTestは既にsource-built CLIとローカル一時repoの経路を持ちますが、現時点では#79のbuild/test証跡受け渡しまで確認していません。

### 推奨

既存の最終受入E2Eを改変・流用せず、ユーザー本人の最終受入専用とする境界を文書で明確にしてください。#79はFake-only CIで安価な契約・証跡処理を確認し、その上で必要な実Codex確認をGitHub公開操作なしのローカル開発用E2Eに分ける構成が適切です。Slack通知は契約/運用者の指定と既存方針を保持し、API応答による機械検証とSlack UIの人間確認を別の合否項目にします。

## 10. 主な参照先

- [リポジトリ AGENTS.md](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/AGENTS.md)
- [README](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/README.md)
- [E2E共通手順](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/docs/reference/e2e/README.md)
- [Testbedレビューサイクル](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/docs/reference/e2e/testbed-review-cycle.md)
- [Testbed E2Eスクリプト](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/scripts/e2e/run-codex-e2e.ps1)
- [CodexIntegrationTests](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/blob/main/tests/Ateliers.Ai.Orchestrator.IntegrationTests/CodexIntegrationTests.cs)
- [Issue #79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79) / [#90](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/90) / [#53](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/53) / [#55](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/55)
- [PR #96](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/96) / [PR #103](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/103)


