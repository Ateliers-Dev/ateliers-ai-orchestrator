# Phase 2 STDIO MCP 検証記録

> 対象コミット: Phase 2実装PR。Release公開とPhase 3 E2Eは対象外。
> 判定: Inconclusive（対応Release CLIの配置後にPhaseゲートの統合確認が必要）。

## ゲート

- 対象: MCP専用プロジェクト、既存CLIのstdin JSON拡張、プロトコル互換性、結果/失敗伝達、機密情報・キャンセル境界、Referenceと導入手順。
- 対象外: Phase 3既定E2Eの正常終了、Git/Implementer/Reviewer機能の改修、Release tag作成・公開。
- 検証: Releaseビルド、Fake-only Unit tests、stdin JSON CLI成功/不正契約、MCPからローカルRelease相当CLIへのFakeプロセス受け渡し、旧インストール版の非互換応答確認。
- 必要な証跡: 実行コマンドと結果、CLI protocol応答、MCP返却JSON、run記録。

## 実行記録

| 検証 | 結果 | 証跡・注記 |
|---|---|---|
| `dotnet restore Ateliers.Ai.Orchestrator.sln` | Pass | SDK依存を復元。初回は環境DNS制限で失敗し、承認されたネットワーク実行で復元成功。 |
| `dotnet build Ateliers.Ai.Orchestrator.Mcp/.. --configuration Release --no-restore` | Pass | 最初の試行で64-bit propertyのコンパイルエラーを検出・修正後、0 warning/0 error。 |
| `dotnet build Ateliers.Ai.Orchestrator.Cli/.. --configuration Release --no-restore -m:1` | Pass | 0 warning/0 error。SDKの並列ビルド試行は子MSBuild起動失敗で診断なしの終了となったため、逐次ビルドで実行。 |
| Fake-only Unit cases | Pass | UnitTestCatalogの69件が合格、1件が環境依存のSkip。testhost経由の標準`dotnet test`は90秒/180秒の接続待ち後に失敗したため、UnitTestCatalogと新規テストメソッドをtesthost外から直接実行した。 |
| CLI stdin JSON 成功/不正契約 | Pass | Release CLIの直接テストfixtureで成功JSON/NeedsHumanInput/exit 0と契約エラーJSON/exit 2を確認。stdoutは1 JSON、診断はstderr。 |
| MCP→CLI Fake実行 | Pass | Release CLIファイル一式と固定インストールマニフェストを一時LOCALAPPDATAへ配置した統合fixture。`install.json`とcapabilityのCLI版照合、protocol handshake、stdin契約送信、stdout JSON読取、exit code照合、run記録作成を確認。実Codexは起動していない。 |
| UTF-8 64 KiB応答上限 | Pass | `あ`を含みUTF-8バイト数が64 KiBを超える一方、文字数は64 KiB未満の応答を捕捉し、`cli_response_too_large`を返すことを確認。 |
| Windows x64 MCP→CLI→Codex `.cmd` shim | Pass（shim限定） | `run_orchestration(agent="codex")`経由で一時Release CLIと`.cmd` shimを起動。shimの起動markerとMCPアダプターから返る構造化結果を確認。shimはテスト用に終了コード2で停止し、Codexの実処理・正常完了は実行していない。導入例は明示的なPowerShell shim対応がある`.ps1`へ変更。 |
| インストール済みv0.0.1のcapability確認 | Inconclusive | 実行環境の`LOCALAPPDATA`はCodexサンドボックス内の一時パスで、ユーザー配置先が見えず実物を照合できなかった。ユーザー報告のv0.0.1は新protocol未対応。新CLI Releaseの公開・配置はPhase 2対象外。 |

## 未解決事項

- 現行ユーザー配置CLI v0.0.1は`--capabilities`と`run --stdin-json`に未対応のため、本PRだけを配置してもMCPは契約実行を拒否する。CLI protocol v1を含む新しいCLI Releaseを別途発行・明示インストールしてから、インストール済み固定版との連携ゲートを再確認する。
- 通常のVSTest testhostはこの実行環境で接続できず、標準`dotnet test`は完了していない。Fake-onlyケースと追加protocol testsは同じコンパイル済みテストコードをtesthost外のfixture起動経路で直接実行した。CIで標準VSTest結果を再確認する。
- Codexアカウントを使う実処理、外部GitHub操作、ユーザーのCodex hostでのMCP登録後の実機起動はこのFake-only検証に含まれない。
- `.cmd` shim smoke testは起動経路と機械可読結果の返却だけを検証し、認証済み実Codex CLIによる正常完了の確認ではない。
