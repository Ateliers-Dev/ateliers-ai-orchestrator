# Phase 2 STDIO MCP 検証記録

> 作成日: 2026-11-18
> 更新日: 2026-11-18
> 作成者: yuu503

> 対象コミット: Phase 2実装PR。Release公開とPhase 3 E2Eは対象外。
> 判定: **Pass（2026-11-18、ユーザー承認済み）**。接続中MCPからCLIを起動し、契約を受領したCoreの実行記録まで確認した。下記の過去記録にあるInconclusiveは、その時点では固定配置環境の証跡が未取得だったことを示す履歴であり、最終実機再試験によりPhase 2判定はPassとなった。

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
| ユーザー固定配置先の実CLI（最終再試験前） | 未実施（当時） | 当時はユーザー報告の配置版v0.0.1が新protocol未対応とされ、実配置先の証跡は未取得だった。2026-11-18最終再試験ではCLI v0.0.2 / manifest validの既定LOCALAPPDATA配置から接続中MCPの疎通に成功した（下記参照）。 |
| Windows x64 MCP→CLI→Codex `.cmd` shim | Pass（shim限定） | `run_orchestration(agent="codex")`経由で一時Release CLIと`.cmd` shimを起動。shimの起動markerとMCPアダプターから返る構造化結果を確認。shimはテスト用に終了コード2で停止し、Codexの実処理・正常完了は実行していない。導入例は明示的なPowerShell shim対応がある`.ps1`へ変更。 |
| 実npm `codex.cmd` 起動 | Pass（version probe） | Windows x64上で`ProcessStartInfo.UseShellExecute=false`から実際のnpm `codex.cmd --version`を起動し、exit 0と`codex-cli 0.167.1`を確認。sandbox権限によるstderr警告があった。認証済み`exec`実行ではない。 |
| インストール済みv0.0.1のcapability確認（最終再試験前） | Inconclusive（当時） | 当時は実行環境からユーザー配置先を照合できなかった。これは最終再試験より前の履歴であり、2026-11-18に接続中MCPからCLI v0.0.2と有効manifestを確認した結果は下記に記録する。 |

## 最終実機再試験後も残る事項

以下は、2026-11-18最終実機再試験後にも未検証またはPhase 2対象外である事項に限る。最終実機再試験より前の観測は、該当箇所にその時点の記録として残す。

- 標準VSTest testhostはこの実行環境で接続できず、Phase 2検証時の標準 `dotnet test` は90秒で中止された。個別fixtureは成功し、PR #78のCIと本PRのCI結果は各PRの検証状態として確認する。
- Phase 2のFake実行は、認証済みCodexによるタスク完了や外部GitHub操作を検証しない。実Agentの追加スモークは別節に記録し、Reviewer証跡の不足とPR必須Runのpush前停止はIssue #79/#80へ引き継いだ。
- Windows x64の `.cmd` shim smoke testは起動経路と機械可読結果の返却までで、認証済みCodex CLIによる正常完了を確認していない。実npm `codex.cmd` の確認も `--version` 起動までである。
- Phase 3の既定スクリプトE2Eとオーケストレーション全体の正常終了は、本Phaseの合格条件に含めない。次の段階は[開発計画書](development-plan.md)に定める範囲に従う。


## 2026-11-18 manifest診断と明示CLIパス対応

接続中のMCPから `get_runtime_diagnostics` を呼び出したところ、MCP実行ファイルは存在し、CLI/manifestも通常の `%LOCALAPPDATA%\Ateliers-Dev\Ai-Orchestrator\bin` に存在していました。CLIを変更せず読み取り専用で確認した結果、`--version` は `0.0.2`、`--capabilities` は `protocolVersion: 1` と3 capabilityがすべて`true`でした。

接続MCPは263 byteの `install.json` を `invalid_json` / `JsonReaderException` / byte位置0と報告しました。ファイル先頭は `EF BB BF`（UTF-8 BOM）で、後続のmanifest JSON内容は有効でした。PowerShell 5.1のインストーラーは `Set-Content -Encoding UTF8` でこのBOMを生成していました。よって、この接続障害の原因は隔離された`LOCALAPPDATA`ではなく、BOMを処理しないMCP側manifest parserです。

本変更では、MCP起動設定にCLI絶対パス `ATELIERS_AI_ORCHESTRATOR_CLI_PATH` を追加し、設定済みなら指定先と同じフォルダーのmanifestを読むようにします。未設定時は既定配置を維持し、指定パスが不正でも既定先へ切り替えません。TaskContractから実行パスは変更できません。既存BOM付きmanifestは読み取り時にBOMを除去し、新規manifestはインストーラーからBOMなしUTF-8で書きます。

| 検証 | 結果 | 証跡・注記 |
|---|---|---|
| 接続中MCPの実配置診断 | Pass（原因特定） | 標準LOCALAPPDATA、CLIとmanifestの存在、263 byte、JSON byte位置0を観測。 |
| ユーザー配置CLIの読み取り専用照合 | Pass | `--version`=`0.0.2`。`--capabilities`でprotocol v1と`runStdinJson` / `contractSchema` / `orchestrationGuide`を確認。 |
| manifest先頭byte確認 | Pass（原因確定） | `EF BB BF`。後続JSONのversion=`0.0.2`、RID=`win-x64`。ファイルは変更していない。 |
| PowerShell 5.1 BOMなしmanifest writer | Pass | Windows PowerShell 5.1.26100.9818で同じ`UTF8Encoding(false)`書込を実行し、先頭`7B 0D 0A`とJSON読込を確認。 |
| Release solution build | Pass | 0 error。NU1900 vulnerability metadata取得警告が1件。 |
| Fake-only標準VSTest | Inconclusive | testhostが90秒で接続できず中止。 |
| MCP CLI adapter fixture | Pass | BOM付きmanifestを持つ明示パスCLIでSchema・guide・Fake runを実行。相対pathの拒否と既定pathへのfallbackなしも確認。 |
| Release MCP STDIO → CLI → Core Fake smoke test | Pass | `ATELIERS_AI_ORCHESTRATOR_CLI_PATH`にユーザー配置CLIを指定したMCP processで、4 Toolを認識し、Schemaとguideを取得、TaskContractを`agent="fake"`で渡してrun ID・run directoryを受け取った。Workflow stateは`NeedsHumanInput`。ユーザーのMCP登録や配置は変更していない。 |
| 接続中Codex MCPの更新後疎通（PR #78マージ前） | 未実施（当時） | PR #78マージ前の記録。2026-11-18最終実機再試験では、PR #78のマージコミットを含む接続中MCPでGuide/Schema取得とFake実行を確認した（下記参照）。CLIの明示パス設定自体は未検証。 |

## 2026-11-18 最終実機再試験 — Phase 2 Pass

接続中のMCPからGuideとSchemaを取得し、TaskContract v1を生成した。Schemaに適合する契約は受理され、MCP → CLI → Coreの実行記録が作成された。計画書のPhase 2ゲートは疎通を判定し、オーケストレーション全体の成功を求めないため、状態 `NeedsHumanInput` でも本ゲートはPassとする。

| 項目 | 観測結果 |
|---|---|
| MCP | `1.0.0+74c1ae3`（PR #78のマージコミットを含むビルド） |
| CLI | `0.0.2`。manifestは `0.0.2` / `win-x64` で `valid` |
| CLIパス解決 | `configuredCliPath=null`、`cliPathSource=localappdata`。既定のユーザー配置先を使用 |
| Guide / Schema | 接続中MCPから取得成功。TaskContract v1を作成し、Coreが受理 |
| 実行到達 | Fake実行がCLIを経由してCoreのrun記録を作成。MCP応答 `ok=true`、終了コード0 |
| 最終Run | `e0477f0e75e04bccaa4806582769c21c`、状態 `NeedsHumanInput` |

明示CLIパス設定はこの接続で検証していない。診断値がnull / localappdataであるため、明示パスによる起動の証拠として扱わない。PR [#78](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/78) でUTF-8 BOM付きmanifestの読取不具合を修正し、PRのCIと独立レビューはPassとしてマージされた（merge commit `74c1ae31dba7b31a4afd4ff2c1bc21d627fecd38`）。今回の接続は、その修正を含むMCPで既定配置から疎通した結果である。

この合格はMCP→CLI→Coreの境界までを対象とし、実Agentによるタスク完了、PR作成、push、Phase 3 E2Eを含まない。Phase 2の合格条件・対象外範囲は上記ゲートと[開発計画書](development-plan.md)を参照する。

## 2026-11-18 追加実エージェントスモーク — Phase 2判定とは別

ユーザー提供の追加スモーク報告に基づく記録。これはPhase 2の合格条件ではなく、実Agentの起動・レビューとPR必須経路の観測である。

### 主Run

- 対象: `Ateliers-Dev/ateliers-ai-orchestrator-testbed`、main起点 `0ae85f8330b1c696cdf55a1f03880cd02966c488`。課題は `CountWords` が空白文字を適切に扱わない意図的なバグ。
- Run ID: `a64d43e4e04e4934a4b7b17fbb8ea3dc`。
- 実Implementerと独立Reviewerが各1回起動し、ともにexit 0。修正は1ファイル。実行後のRelease buildと全5チェックを再確認し、成功。
- Reviewerは修正を妥当と評価したが、レビュー入力にテストログがなく受入判定は `NeedsHumanInput`。実Agent処理と検証結果のReviewerへの証跡受け渡しは別の事柄として記録する。
- push / PR作成なし。testbedの `main` は起点SHAのまま。修正は隔離cloneの専用ブランチに未コミットで残った（報告時点）。

### PR必須経路

別Run `7d7e0e60ff494ff4994e2b8f2d4e75f2` はpush前の `push.secret-scan-and-whitespace-check` がexit 2で停止し、Reviewer・push・PR作成に到達しなかった。直接原因は未確定であり、併記されたdotnet復元先やGit lockの問題との因果関係も確定していない。GitHubへの書き込みは発生していない。

### 後続課題と引継ぎ

- [Issue #77](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/77): MCP実行環境ごとのパス可視性・配置方式の将来検討。今回の成功は既定LOCALAPPDATA配置での結果であり、明示CLIパスは未検証。
- [Issue #79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79): Implementerのビルド・テスト証跡を独立Reviewerへ受け渡す。
- [Issue #80](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80): PR必須Runのpush前チェック停止を再現し、直接原因を調査する。

Phase 3の次の作業と受入基準は、既存の[開発計画書](development-plan.md)に従う。本記録はPhase 3の要件を追加・変更しない。

