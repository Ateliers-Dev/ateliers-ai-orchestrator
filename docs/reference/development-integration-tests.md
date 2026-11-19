# 開発用IntegrationTestの実行範囲

> 作成日: 2026-11-19
> 更新日: 2026-11-19
> 作成者: Codex

## 目的

開発用IntegrationTestは、ローカルの隔離fixtureでコンポーネント間の連携を確認します。Testbedはテスト教材・シナリオを提供し、IntegrationTestとE2Eはそのシナリオを検証する実行方法です。同じシナリオを別の層で再利用できます。

## Issue #79の実Codex検証

`tests/Ateliers.Ai.Orchestrator.IntegrationTests` の明示opt-inテストは、TestbedのWordCounter課題（`main` revision `e205cd40166b8e54fd7cb01eb98cc0a57a882426`）と同じ5チェックをローカルの一時Git fixtureに作成します。GitHub Testbedへの接続・push・PRは行いません。

このケースでは実Codexを使ってOrchestratorのImplementer→Reviewer経路を実行します。合否条件はCodexプロセスの起動、通信、応答到達、正常終了だけです。Reviewerへの証跡入力、状態遷移、保存処理といったプログラム上の振る舞いはFakeTestsで決定論的に検証し、AIの自由記述や判定の正しさを実Codex IntegrationTestの合否条件にしません。

この開発用検証は、ソースをbuildしたCLI assemblyを直接使います。インストール済みCLI/MCPの検証、GitHub公開操作、Slack実通知、ブラウザーやSlack画面の確認は対象外です。Slack通知は無効です。

## 実行前提と手順

- WindowsまたはLinuxの開発環境、.NET SDK 10、Gitが必要です。
- 認証済みCodex CLIの実行ファイルと、実行者の明示opt-inが必要です。実行ではImplementerとReviewerがモデルを利用するため、利用枠または費用が発生する可能性があります。
- このテストは通常のUnit/Fake CIや `dotnet test` の全件実行では起動しません。実Codex検証は明示実行時のみ行います。
- `ATELIERS_RUN_CODEX_INTEGRATION_TESTS=1` と `ATELIERS_CODEX_CLI_PATH` にCodex CLIの絶対パスを設定し、`RunAsync_ShouldConnectToCodexAndReceiveResponse` を個別に実行します。
- 実行前にbuild/restoreが必要です。標準コマンドと環境設定例はリポジトリのREADME「Codex IntegrationTests」を参照してください。

失敗時はfixtureとRun証跡を保持し、IntegrationTestの失敗を未検証または失敗として扱います。Codexが起動しない、応答を受信しない、または正常終了しない場合は成功とみなしません。ユーザー専用の最終受入E2Eを代替経路として実行しません。

## テスト層との境界

| 検証 | 実行主体 | 確認する内容 |
|---|---|---|
| UnitTests / FakeTests | 開発者・Windows/Ubuntu CI | 内部ロジック、証跡形式、パス、マスキング、Reviewerへの決定論的な証跡受け渡し |
| 実Codex IntegrationTest | 開発者が明示opt-in | 実Codexプロセスの起動、通信、応答到達、終了 |
| Slack IntegrationTest | Slack APIを明示的に検証する実行者 | テストチャンネルへの実送信とAPI応答 |
| Testbed最終受入E2E | ユーザー本人 | 公開PRを含む受け入れフロー。通知を選んだ場合はAPI送信と人間による到達確認 |
| CLI/MCP導入受入 | ユーザー本人 | インストール済み配布物とMCPの導入・接続 |

Slack API応答と人間による画面上の到達・見え方の確認は別の条件です。Testbed E2Eが使うのもソースからRelease buildしたCLIであり、インストール済みCLI/MCP導入の確認は別途必要です。
