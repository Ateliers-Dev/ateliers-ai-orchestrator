# Phase 2 STDIO MCP 検証記録

> 対象コミット: Phase 2実装PR。Release公開とPhase 3 E2Eは対象外。
> 判定: Inconclusive（Schema/ガイド取得機能は単体・fixture検証対象。実AI参照、契約適合、MCP実機連携のゲート確認が残る）。

## 追加要求と失敗記録

初期の単一 `run_orchestration` Toolは、親AIに `contract` JSONの形を知らせず、利用可能な契約を生成できない要求不足があった。ユーザー承認により「要件定義不足、ツールを使うための必須実装の考慮漏れ」として記録し、3 Tool構成へ変更する。Schema/ガイドを取得できることと、AIがそれらを実際に参照して妥当な契約を生成することは別ゲートとする。

## ゲート

- 対象: MCP専用プロジェクト、既存CLIのstdin JSON拡張、プロトコル互換性、結果/失敗伝達、機密情報・キャンセル境界、Referenceと導入手順。
- 対象外: Phase 3既定E2Eの正常終了、Git/Implementer/Reviewer機能の改修、Release tag作成・公開。
- 検証: Releaseビルド、Fake-only Unit tests、Schema/ガイドのCLI公開とMCP中継、Schema/Core validator整合、stdin JSON CLI成功/不正契約、旧CLI非対応時の構造化エラー、MCPからCLIへのFake実行。
- Phaseゲート追加: AIのガイド参照と契約生成前のSchema取得順序、生成契約のCLI validator適合、MCP→CLI実行結果を同一実機試行で確認する。ログはTool名・順序中心とし契約本文を不要に複製しない。
- 必要な証跡: 実行コマンドと結果、CLI protocol応答、MCP返却JSON、run記録。

## 実行記録

| 検証 | 結果 | 証跡・注記 |
|---|---|---|
| `dotnet restore Ateliers.Ai.Orchestrator.sln` | Pass | SDK依存を復元。初回は環境DNS制限で失敗し、承認されたネットワーク実行で復元成功。 |
| `dotnet build Ateliers.Ai.Orchestrator.Mcp/.. --configuration Release --no-restore` | Pass | 最初の試行で64-bit propertyのコンパイルエラーを検出・修正後、0 warning/0 error。 |
| `dotnet build Ateliers.Ai.Orchestrator.Cli/.. --configuration Release --no-restore -m:1` | Pass | 0 warning/0 error。SDKの並列ビルド試行は子MSBuild起動失敗で診断なしの終了となったため、逐次ビルドで実行。 |
| `dotnet build Ateliers.Ai.Orchestrator.sln --configuration Release --no-restore -m:1`（今回） | Pass | 0 error。NuGet vulnerability metadata endpointへのアクセス失敗によりMCP projectでNU1900が1件。 |
| 暫定MCP installer 構文・配置範囲 | Pass（mock publish） | PowerShell AST parseに成功。別の作業ディレクトリからスクリプトを起動するmock publish fixtureで、古いMCPファイルの全置換、publish成果物一式のコピー、隣接する`bin`の保持を確認。実publishは試行したが、`api.nuget.org`の名前解決失敗（NU1301）で復元できず、実成果物の配置確認は未実施。 |
| installer パス・reparse point拒否 | Pass（隔離mock fixtures） | `C:relative`、`C:\`、ユーザープロファイル直下をpublish開始前に拒否。隔離fixtureで配置先、祖先、配置先内のjunctionを検出して失敗終了し、それぞれのリンク先sentinelが保持されることを確認。通常ディレクトリでのMCP全置換、成果物コピー、隣接`bin`保持も再確認。 |
| Fake-only Unit cases | Pass | UnitTestCatalogの69件が合格、1件が環境依存のSkip。testhost経由の標準`dotnet test`は90秒/180秒の接続待ち後に失敗したため、UnitTestCatalogと新規テストメソッドをtesthost外から直接実行した。 |
| CLI stdin JSON 成功/不正契約 | Pass | Release CLIの直接テストfixtureで成功JSON/NeedsHumanInput/exit 0と契約エラーJSON/exit 2を確認。stdoutは1 JSON、診断はstderr。 |
| MCP→CLI Fake実行 | Pass | Release CLIファイル一式と固定インストールマニフェストを一時LOCALAPPDATAへ配置した統合fixture。`install.json`とcapabilityのCLI版照合、protocol handshake、stdin契約送信、stdout JSON読取、exit code照合、run記録作成を確認。実Codexは起動していない。 |
| UTF-8 64 KiB応答上限 | Pass | `あ`を含みUTF-8バイト数が64 KiBを超える一方、文字数は64 KiB未満の応答を捕捉し、`cli_response_too_large`を返すことを確認。 |
| CLI Schema / guideコマンドと整合テスト | Pass | `--contract-schema`のJSON parse、required/type/additionalProperties/taskId pattern/int範囲を確認。単一行・複数行・空白のみの文字列についてSchema patternとCore validatorの受理を照合。既存`examples/task-contract.json`の作業ディレクトリを実在パスへ差し替えた契約がCore validatorを通過。`--orchestration-guide`出力をCore resourceと照合し、能力フラグを確認。
| MCP Schema / guide中継 | Pass（Windows fixture） | 固定配置を模したRelease相当CLIに対して両方のCLI出力がMCP adapterから取得でき、`--capabilities`のCLI版・protocolVersion・該当するtrue capabilityを確認してからresourceを起動することを確認。能力JSONのフラグ欠落/false/CLI版不一致は能力判定fixtureで確認し、resource取得経路は対応CLIで中継成功を確認。不在CLIとキャンセルも構造化エラーを確認。旧CLI非対応・起動失敗・不正出力・timeoutはfixtureで未検証。
| CLI stdin / Schema / guide fixtures（今回） | Pass | apphost fixture経由で既存stdin成功/不正契約テストと、新規Schema/guide/capability、Core DTOとのrequired/properties整合テストを直接実行。 |
| MCP adapter resource / stdin fixtures（今回） | Pass | apphost fixture経由で固定インストールCLIのSchema・guide取得、不在/キャンセルエラー、既存MCP→CLI Fake実行、Codex shim、出力上限テストを直接実行。 |
| 標準VSTest Unit（今回） | Inconclusive | testhostへの接続待ちが90秒後に失敗し、実行が中断された。 |
| 実AIのガイド参照、Schema順序、契約validator適合、MCP実行 | 未実施 | Phase 2追加統合ゲート。Tool利用可能性とは独立に証跡を取得する必要がある。 |
| Windows x64 MCP→CLI→Codex `.cmd` shim | Pass（shim限定） | `run_orchestration(agent="codex")`経由で一時Release CLIと`.cmd` shimを起動。shimの起動markerとMCPアダプターから返る構造化結果を確認。shimはテスト用に終了コード2で停止し、Codexの実処理・正常完了は実行していない。導入例は明示的なPowerShell shim対応がある`.ps1`へ変更。 |
| 実npm `codex.cmd` 起動 | Pass（version probe） | Windows x64上で`ProcessStartInfo.UseShellExecute=false`から実際のnpm `codex.cmd --version`を起動し、exit 0と`codex-cli 0.167.1`を確認。sandbox権限によるstderr警告があった。認証済み`exec`実行ではない。 |
| インストール済みv0.0.1のcapability確認 | Inconclusive | 実行環境の`LOCALAPPDATA`はCodexサンドボックス内の一時パスで、ユーザー配置先が見えず実物を照合できなかった。ユーザー報告のv0.0.1は新protocol未対応。新CLI Releaseの公開・配置はPhase 2対象外。 |

## 未解決事項

- 現行ユーザー配置CLI v0.0.1は`--capabilities`と`run --stdin-json`に未対応のため、本PRだけを配置してもMCPは契約実行を拒否する。CLI protocol v1を含む新しいCLI Releaseを別途発行・明示インストールしてから、インストール済み固定版との連携ゲートを再確認する。
- 通常のVSTest testhostはこの実行環境で接続できず、標準`dotnet test`は完了していない。Fake-onlyケースと追加protocol testsは同じコンパイル済みテストコードをtesthost外のfixture起動経路で直接実行した。CIで標準VSTest結果を再確認する。
- Codexアカウントを使う実処理、外部GitHub操作、ユーザーのCodex hostでのMCP登録後の実機起動はこのFake-only検証に含まれない。
- `.cmd` shim smoke testは起動経路と機械可読結果の返却だけを検証し、認証済み実Codex CLIによる正常完了の確認ではない。
- 実npm `codex.cmd` の確認は`--version`起動まで。MCP→インストール済みRelease CLI→認証済みCodex CLIによるタスク実行・結果返却は未確認。
