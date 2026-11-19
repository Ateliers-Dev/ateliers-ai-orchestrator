# 開発用IntegrationTestの実行範囲

> 作成日: 2026-11-19
> 更新日: 2026-11-19
> 作成者: Codex

## 目的

開発用IntegrationTestは、ローカルの隔離fixtureでコンポーネント間の連携を確認します。Testbedはテスト教材・シナリオを提供し、IntegrationTestとE2Eはそのシナリオを検証する実行方法です。同じシナリオを別の層で再利用できます。

## Issue #79の実Codex検証

`tests/Ateliers.Ai.Orchestrator.IntegrationTests` の明示opt-inテストは、TestbedのWordCounter課題（`main` revision `e205cd40166b8e54fd7cb01eb98cc0a57a882426`）と同じ5チェックをローカルの一時Git fixtureに作成します。GitHub Testbedへの接続・push・PRは行いません。

このケースでは、Implementerが対象コードを修正し、Release buildと5チェックを実行して実際の出力ログを証跡として保存します。Orchestratorは証跡マニフェストとログ参照を検査し、ログを含む非信頼データとして独立Reviewerの要求へ渡します。テストは保存されたコマンド・終了コード・ログ内容、Reviewer要求に含まれた証跡、およびReviewer応答が検証結果に言及していることを確認します。自己申告の終了コードとログは、Orchestratorがコマンドを独立実行した証明ではありません。

この開発用検証は、ソースをbuildしたCLI assemblyを直接使います。インストール済みCLI/MCPの検証、GitHub公開操作、Slack実通知、ブラウザーやSlack画面の確認は対象外です。今回の承認済みIssue #79シナリオではSlack通知を無効にします。別の実行で通知ONが指定されている場合は、その指定を維持してください。

## 実行前提と手順

- WindowsまたはLinuxの開発環境、.NET SDK 10、Gitが必要です。
- 認証済みCodex CLIの実行ファイルと、実行者の明示opt-inが必要です。実行ではImplementerとReviewerがモデルを利用するため、利用枠または費用が発生する可能性があります。
- このテストは通常のFake-only CIや `dotnet test` の全件実行では起動しません。CIはFake-only Unitを品質ゲートとし、実Codex検証は明示実行時のみ行います。
- `ATELIERS_RUN_CODEX_INTEGRATION_TESTS=1` と `ATELIERS_CODEX_CLI_PATH` にCodex CLIの絶対パスを設定し、`RunAsync_ShouldCompleteImplementerReviewerAndPersistNeedsHumanInput` を個別に実行します。
- 実行前にbuild/restoreが必要です。標準コマンドと環境設定例はリポジトリのREADME「Codex IntegrationTests」を参照してください。

失敗時はfixtureとRun証跡を保持し、IntegrationTestの失敗を未検証または失敗として扱います。Codexが起動していない、build/test証跡が欠けている、またはReviewerが証跡を確認していない場合、成功とみなしません。ユーザー専用の最終受入E2Eを代替経路として実行しません。

## テスト層との境界

| 検証 | 実行主体 | 確認する内容 |
|---|---|---|
| Fake-only Unit | 開発者・Windows/Ubuntu CI | 証跡形式、パス、マスキング、引き渡しロジック |
| 実Codex IntegrationTest | 開発者が明示opt-in | ローカルfixtureでのImplementer→Reviewerと実証跡参照 |
| Slack IntegrationTest | Slack APIを明示的に検証する実行者 | テストチャンネルへの実送信とAPI応答 |
| Testbed最終受入E2E | ユーザー本人 | 公開PRを含む受け入れフロー。通知を選んだ場合はAPI送信と人間による到達確認 |
| CLI/MCP導入受入 | ユーザー本人 | インストール済み配布物とMCPの導入・接続 |

Slack API応答と人間による画面上の到達・見え方の確認は別の条件です。Testbed E2Eが使うのもソースからRelease buildしたCLIであり、インストール済みCLI/MCP導入の確認は別途必要です。
