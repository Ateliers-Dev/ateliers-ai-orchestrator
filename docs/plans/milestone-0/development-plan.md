# Ateliers AI Orchestrator 開発計画書

> 作成日: 2026-11-17
> 更新日: 2026-11-18
> 作成者: yuu503

> 状態: 承認済み。Phase 1・Phase 2完了。Phase 3の進行基準。

## 1. 目的と進め方

本書は、Ateliers AI Orchestrator の立て直しにおける作業順序、各Phaseの完了条件、進行・変更管理を固定する。Phaseごとの完了判定は、そのPhaseに記載した条件だけに基づく。レビュー合格、Phase完了、最終リリース受入はそれぞれ別に記録する。

本計画書とPhase 1対象の[CLIインストーラー要求仕様](../../requirements/milestone-0/cli-installer-requirements.md)はユーザー確認済みである。Phase 2では[承認済みMCP要求仕様](../../requirements/milestone-0/mcp-requirements.md)を着手・実装基準とする。長期的な拡張性・保守性を優先し、現行CLIに不足する構造化プロセス境界は後方互換を保って拡張する。仕様や完了条件は、ユーザーが変更を承認し記録するまで変更しない。

## 2. 固定フェーズ

| Phase | 作業 | 完了条件 |
|---|---|---|
| 0 | Issue #20: UnitTests の xUnit 移行 | **完了** — Issue #20 を満たし、xUnit移行・Test Explorer確認・CI確認を経てPR #67 と Issue #20 を完了した。 |
| 1 | Release CLI インストーラー | 承認済み要求仕様に従い、選択した固定バージョンのRelease CLIをユーザー単位のインストール先へ配置し、起動対象バージョンを一意に識別できる。 |
| 2 | STDIO MCP | **完了（2026-11-18、ユーザー承認）** — 承認済み受入条件に基づくMCP→CLI→Core実機疎通がPass。最終実機結果は[Phase 2検証記録](phase2-stdio-mcp-verification.md)を参照。 |
| 3 | 既定スクリプトによるRelease CLI起動確認 | `run-codex-e2e.ps1` を追加パラメータなしで実行し、Release CLIプロセスが起動したことを証跡で確認する。CLIの正常終了やオーケストレーション完了は要求しない。 |

### Phase 0 — Issue #20

Issue #20「UnitTestsプロジェクトの命名明確化とxUnitへの移行」を基準とし、要求を拡張しない。テストプロジェクトを `Ateliers.Ai.Orchestrator.UnitTests` に改名し、既存のUnit/FakeテストをxUnitへ移行する。ソリューション、参照、README、CIの必要な参照を整合させる。テストの検証内容は維持し、IntegrationTestsは別プロジェクトとして維持する。実Codexを起動しない。

Phase 0の完了記録には、変更された対象、`dotnet test` の結果、CIの結果、IntegrationTestsと実Codex起動を自動実行しない確認を含める。Issue本文のVisual Studio Test Explorer実行確認もIssueの完了条件として扱う。

### Phase 1 — CLIインストーラー

固定バージョンRelease CLIをユーザー単位でインストールする。確定パスは `%LOCALAPPDATA%\Ateliers-Dev\Ai-Orchestrator\bin\Ateliers.Ai.Orchestrator.Cli.exe`。対象は Windows x64 自己完結型ZIPで、指定バージョンのGitHub Releaseから取得する。タグ起動のRelease workflowでZIP、マニフェスト、SHA-256ファイルを作成・添付する。インストーラーはSHA-256、対象RID、マニフェスト、CLI自身の `--version` を検証してから `bin` を切り替え、`-Verify` で実体とバージョンを再確認する。

Phase 1の完了条件は、UnitTests成功、Release publish/package定義の確認、指定バージョンを使う配置手順と更新/再実行の確認、配置・バージョン確認と失敗ケースの検証、およびこの実装を根拠とした技術仕様書の作成とする。Phase 1の検証記録（[CLIインストーラー検証記録](../../reports/phase1-cli-installer-verification.md)）では、実行したコマンド、件数、失敗・Skip、未確認事項を区別する。MCP実装、Phase 3 E2E、オーケストレーション機能追加は含めない。

### Phase 2 — STDIO MCP

MCPは既存契約スキーマをCLIへ中継する薄いアダプターとする。初期要求には契約構造を親AIへ渡す手段がなく、親AIが `run_orchestration(JsonElement contract)` 用の契約を生成できなかった。この失敗を「要件定義不足、ツールを使うための必須実装の考慮漏れ」と記録し、承認済み要求変更として単一Toolから `get_contract_schema` / `get_orchestration_guide` / `run_orchestration` の3 Toolへ変更する。Schema・ガイドはCoreで単一管理し、CLI公開コマンドと薄いMCP中継を設ける。CLIのオーケストレーション機能をMCPに複製しない。

MCPは分離可能な専用プロジェクトとし、公式C# MCP SDKを使う。CLIとの内部境界は契約JSON stdin、結果JSON stdout、診断stderrとする。CLI既存のファイル引数経路は維持する。

Phase 2の合格条件は、(1) MCPが3 Toolを認識する、(2) `get_orchestration_guide` と `get_contract_schema` が正常終了して有効な内容を返す、(3) 親AIがSchemaに適合するTaskContractを生成できる、(4) `run_orchestration` がMCPからCLIを起動し、契約を受け渡して実行処理へ到達した証跡があることとする。AIがガイドを自発参照したかは観測・記録するが必須条件にはしない。CLI内部のGit等の既知エラーで `Failed` / `NeedsHumanInput` となっても、起動・契約受領・実行到達を確認できれば疎通Passとし、内部不具合は後続課題へ記録する。CLI起動失敗、契約未受領、MCP→CLI通信失敗はFailとする。Phase 2はオーケストレーション本体の正常終了を要求せず、既知の内部不具合の修正へ範囲を拡大しない。詳細な証跡要件は[Phase 2検証記録](phase2-stdio-mcp-verification.md)を参照する。Phase 3の既定スクリプト起動確認は含めない。

### Phase 2 完了判定（2026-11-18）

**Pass — ユーザー承認済み。** 接続中MCPからGuide / Schemaを取得し、TaskContract v1を生成して受理させ、Fake実行がCLIを経由してCoreのrun記録まで到達したことを確認した。最終Runは `e0477f0e75e04bccaa4806582769c21c`、状態は `NeedsHumanInput`、終了コードは0。これは計画で定めた疎通条件の合格であり、オーケストレーション全体の正常完了やPhase 3の完了を意味しない。判定根拠と追加スモークテストは[Phase 2検証記録](phase2-stdio-mcp-verification.md)に記載する。

### Phase 3 — 既定スクリプトでCLIが起動することの確認

実行前の確認と実Codex CLI起動に必要な承認は、既存のローカル実行ゲートに従う。Phase 3独自の追加承認手順は設けない。既存ゲートの事前条件が満たされない場合は実行せず、No-Goと根拠を記録する。

完了条件は、既定の `run-codex-e2e.ps1` を追加パラメータなしで実行し、Release CLIが起動することとする。起動確認の証跡は、スクリプトの記録とCLIプロセス起動の観測など、CLIが実際に起動したと判断できる記録とする。

CLI起動を確認した後のオーケストレーション失敗、`NeedsHumanInput`、タイムアウト、または非ゼロ終了は、Phase 3の不合格条件にしない。結果と証跡は記録し、最終リリース受入で別途評価する。CLIの起動前に失敗した場合はPhase 3未達とする。

Phase 3では、オーケストレーション正常終了、Implementer/Reviewerの成功、Git操作や外部連携の成功を要求しない。Phase 3で見つかった不具合を、記録なしに同Phaseの追加条件へ変換しない。

## 3. Phaseゲートと記録

各Phaseの開始前に、対象、対象外、完了条件、実行する検証、必要な承認、証跡の保存場所を短いチェックリストとして記録する。ゲートの条件を満たしたか判定し、結果を次のいずれかで記録する。

- **Pass**: 事前に定めた完了条件をすべて証跡で確認した。
- **Fail**: 完了条件を満たさなかった。観測した事実、環境、入力、エラー、再現条件、影響を記録する。
- **Inconclusive**: 証跡不足、実行中断、結果不明などで判定できない。成功扱いせず、未取得事項を記録する。

失敗・中断・不明の結果は、原因が確定していなくても記録する。報告者からの結果と、独立に再実行・観測して確認した結果を区別する。実行記録、ログ、契約、作業コピー等は、失敗原因と成果の判断に必要な範囲で保持し、削除を成功の代替条件にしない。

PhaseのPassを記録した後、後続Phaseや最終リリースの条件を遡って追加しない。追加条件が必要な場合は、既存判定を維持した上で、変更理由、影響するPhase、完了条件の変更案、受入への影響を記録してユーザー承認を得る。

## 4. 仕様変更、レビュー、提案の扱い

- 要求変更は、変更内容・理由・影響範囲・対象文書・完了条件を記録し、ユーザーの明示承認後に反映する。
- Issue、PRレビュー、監査からの要求外の改善提案は、指摘として記録し、承認されるまで実装スコープへ加えない。
- 指摘が現行要求への不適合を示す場合、実際の要求と証跡を照合して不適合の有無を判定する。提案と必須修正を混同しない。
- レビュー合格はコード変更のレビュー結果であり、Phase完了やリリース受入を自動的に意味しない。
- 本計画に明記されていない作業をPhase完了の追加条件にするときは、ユーザー承認を得る。

## 5. 最終リリース受入（Phase 3とは別）

最終リリース受入条件は、既定スクリプト `run-codex-e2e.ps1` によりRelease CLIのオーケストレーションが正常終了することとする。これはPhase 3の「Release CLIが起動する」条件とは別の後続判定であり、Phase 3を未完了へ戻す条件ではない。

最終受入の判定で用いる「正常終了」の具体的な観測基準、必要な実行証跡、環境条件は、最終受入を実施する前に別途明記し、ユーザー確認を得る。受入の結果もPass、Fail、Inconclusiveのいずれかで証跡とともに記録する。

## 6. Phase 1 技術仕様

Phase 1の実装詳細は[CLIインストーラー技術仕様](../../reference/cli-installer.md)に記録する。技術仕様は実装された動作・制約を記述し、後続Phaseの計画機能を実装済みと扱わない。

## 7. Phase 2 技術仕様と検証記録

Phase 2の実装仕様は[STDIO MCP技術仕様](../../reference/stdio-mcp.md)、Phaseゲートの実測結果は[STDIO MCP検証記録](phase2-stdio-mcp-verification.md)に記録する。Phase 2計画時点では、stdin JSON protocolに対応したReleaseの発行・配置を別の明示的なRelease/installer操作としていた。現在のCLI変更後のベータタグ運用は[リポジトリ作業指示](../../AGENTS.md)に従う。ベータタグはGitHub Releaseを作成しない。

