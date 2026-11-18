# Phase 2 STDIO MCP 検証記録

> 対象コミット: Phase 2実装PR。Release公開とPhase 3 E2Eは対象外。
> 判定: Inconclusive（新しいPhase 2合格基準は隔離Windows fixtureでPass。ユーザーの固定配置CLIは未更新で、実配置先での疎通証跡は未取得）。

## 追加要求と失敗記録

初期の単一 `run_orchestration` Toolは、親AIに `contract` JSONの形を知らせず、利用可能な契約を生成できない要求不足があった。ユーザー承認により「要件定義不足、ツールを使うための必須実装の考慮漏れ」として記録し、3 Tool構成へ変更する。Schema/ガイドを取得できることと、AIがそれらを実際に参照して妥当な契約を生成することは別ゲートとする。

## ゲート

- 合格基準（ユーザー承認による更新）:
  1. MCPが `get_contract_schema`、`get_orchestration_guide`、`run_orchestration` の3 Toolを認識できる。
  2. Schemaとガイド取得が実際に正常終了し、有効なSchema JSONとガイド本文を返す。
  3. 親AIが取得したSchemaに適合するTaskContractを生成できる。AIがガイドを自発参照したかは観測・記録するが、毎回の参照を合格条件にしない。
  4. `run_orchestration` がMCP経由でCLIを起動し、契約を受け渡し、CLIの実行処理へ到達したことを証跡で確認できれば疎通Pass。CLI内のGit等の既知不具合による `Failed` / `NeedsHumanInput` やオーケストレーションの非正常終了はPhase 2の不合格とせず、後続課題として記録する。
  5. CLI起動失敗、契約未受領、MCP→CLI通信失敗はFail。
- 対象: MCP専用プロジェクト、既存CLIのstdin JSON拡張、プロトコル互換性、結果/失敗伝達、機密情報・キャンセル境界、Referenceと導入手順、上記のMCP→CLI疎通確認。
- 対象外: Phase 3既定E2Eの正常終了、Git/Implementer/Reviewer機能の改修、Release tag作成・公開。
- 検証: Releaseビルド、Fake-only Unit tests、Schema/ガイドのCLI公開とMCP中継、Schema/Core validator整合、stdin JSON CLI成功/不正契約、旧CLI非対応時の構造化エラー、MCPからCLIへのFake実行。
- 実機ゲート: 3 Toolの認識、Schema/ガイド取得の正常応答、親AIが生成した契約のSchema適合、MCP→CLIの起動・契約受領・実行到達を確認する。AIのガイド自発参照は観測結果として記録する。CLIの内部処理結果・タスク正常終了は合否に用いない。
- 必要な証跡: 実行コマンドと結果、Tool認識と利用順序、Schema/ガイドの有効応答、生成契約のSchema適合結果、CLI起動・契約受領・実行到達のCLI/MCP応答およびrun記録。契約本文は証跡に不要な重複保存をしない。

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
| MCP STDIO transport → CLI（今回） | Pass（隔離Windows fixture） | MCPサーバーをSTDIO起動し、`tools/list`で3 Toolを確認。`tools/call`のSchema/guide取得は有効なJSON Schema Draft 2020-12とガイド本文を返した。Schemaに適合するfixture契約を渡すとMCPがCLIを起動し、CLI応答・run ID・run記録ディレクトリを返した。オーケストレーション状態は`NeedsHumanInput`で、今回の合格基準では疎通Pass。 |
| Release CLI / MCP Release build（今回） | Pass | 両プロジェクトを`--no-restore -m:1`で個別Release build。各0 warning / 0 error。既存restore assetsを使っている。 |
| `dotnet restore Ateliers.Ai.Orchestrator.sln`（今回） | Inconclusive | SDK初期化中にService Control Managerへのアクセスが拒否され、restoreは終了コード1。ビルドは既存restore assetsを使用した。 |
| Fake-only標準VSTest Unit（今回） | Inconclusive | testhost接続が90秒で失敗し実行中止。 |
| CLI stdin・MCP adapter apphost fixture（今回） | Pass | `--stdio-json-cli-fixture`と`--mcp-cli-adapter-fixture`が両方終了コード0。schema/guide中継、契約stdin、Fake実行、Codex shim等を確認。 |
| ユーザー固定配置先の実CLI | 未実施 | 現在の配置版はユーザー報告のv0.0.1で、新protocol未対応。Release/tag発行とCLIインストールは承認対象のため、この作業では行っていない。実配置先での確認は別途明示承認後に実施する。 |
| Windows x64 MCP→CLI→Codex `.cmd` shim | Pass（shim限定） | `run_orchestration(agent="codex")`経由で一時Release CLIと`.cmd` shimを起動。shimの起動markerとMCPアダプターから返る構造化結果を確認。shimはテスト用に終了コード2で停止し、Codexの実処理・正常完了は実行していない。導入例は明示的なPowerShell shim対応がある`.ps1`へ変更。 |
| 実npm `codex.cmd` 起動 | Pass（version probe） | Windows x64上で`ProcessStartInfo.UseShellExecute=false`から実際のnpm `codex.cmd --version`を起動し、exit 0と`codex-cli 0.167.1`を確認。sandbox権限によるstderr警告があった。認証済み`exec`実行ではない。 |
| インストール済みv0.0.1のcapability確認 | Inconclusive | 実行環境の`LOCALAPPDATA`はCodexサンドボックス内の一時パスで、ユーザー配置先が見えず実物を照合できなかった。ユーザー報告のv0.0.1は新protocol未対応。新CLI Releaseの公開・配置はPhase 2対象外。 |

## 未解決事項

- 現行ユーザー配置CLI v0.0.1は`--capabilities`と`run --stdin-json`に未対応のため、本PRだけを配置してもMCPは契約実行を拒否する。CLI protocol v1を含む新しいCLI Releaseを別途発行・明示インストールしてから、インストール済み固定版との連携ゲートを再確認する。
- 通常のVSTest testhostはこの実行環境で接続できず、今回の標準`dotnet test`は90秒で中止された。CLI stdin / MCP adapter fixturesと、MCP STDIO transportから実CLIへ接続する隔離Windows fixtureは個別に成功した。CIで標準VSTest結果を再確認する。
- Codexアカウントを使う実処理、外部GitHub操作、ユーザーのCodex hostでのMCP登録後の実機起動はこのFake-only検証に含まれない。
- `.cmd` shim smoke testは起動経路と機械可読結果の返却だけを検証し、認証済み実Codex CLIによる正常完了の確認ではない。Phase 2ではこの正常完了を合格条件にしない。
- 実npm `codex.cmd` の確認は`--version`起動まで。認証済みCodexによるオーケストレーション結果は後続確認事項であり、Phase 2の疎通Passには要求しない。
- ユーザーの固定インストール先 `%LOCALAPPDATA%\Ateliers-Dev\Ai-Orchestrator\bin` はこの実行環境から更新・検証していない。実機確認は、Phase 2対応CLIをビルドしたPRのレビュー後、Release/tag発行とインストーラー実行に対する別途承認を得てから行う。

