# Milestone 1 実Codex E2E環境調査レポート

> 作成日: 2026-11-19
> 更新日: 2026-11-19
> 作成者: Codex

> 状態: Issue #84対応時に観測した実行環境の問題と、改善候補を記録する。環境事前チェックは[Issue #90](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/90)で検討する。

## 目的と範囲

実Codex E2Eで発生した環境起因の停止と、原因切り分け・修正に時間を要した箇所を記録する。主題はCodex子プロセスのWindows/.NET/NuGet実行環境であり、改善候補として事前チェックと診断の整備を挙げる。

Issue #79の「ImplementerからReviewerへの検証証跡の引き継ぎ」は別課題であり、このレポートや提案Issueの実装範囲に含めない。

## 結果の概要

Issue #84の修正では、Runごとの.NET/NuGet書込先をsandboxで許可された専用領域に置き、必要な環境変数をCodex shellへ渡すようにした。Windows NuGetのmachine-wide settings解決に必要な `PROGRAMFILES` と `PROGRAMFILES(X86)` も、読み取り専用の環境値として渡した。Reviewerには追加の書込許可や環境上書きを与えていない。

修正後の実Codex E2E Run `f6dc23ec024640f5938e2dc0ebdd6aaf` では、Implementer子プロセスが終了コード0で終了し、restore、Release build（警告0・エラー0）、5件の対象チェックが成功した。Testbedで変更されたのは `src/WordCounter/TextMetrics.cs` のみだった。差分と実行記録を独立Reviewerが確認し、PR #88を承認した。PR上のUnit Test CIもWindows・Ubuntuの両方で成功した。

- 対応PR: [#88](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/88)（2026-11-18マージ済み）
- 元の課題: [Issue #84](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/84)（完了）
- 別課題: [Issue #79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79)（証跡引き継ぎ）

## 発生した問題と切り分け

| 観測 | 影響 | 判明したこと・対応 |
|---|---|---|
| 初期のCodex起動試行（Run `d3028c3d78104881bf52892870197241`）で、既存Codex profileの初期化書込みが拒否された | Implementerが起動する前に停止した | Codex自体のprofile初期化先も、子プロセスの実行環境で書き込み可能なRun専用領域にする必要があった |
| Issue #84の再現で、Run専用 `DOTNET_CLI_HOME` がsandboxの書込許可外だった | .NET SDKが初期化・書込みできず、Implementer内のrestore/buildに進めなかった | SDK/NuGetの書込先とImplementerのsandbox writable rootsを整合させた。NuGet package、HTTP、scratch、plugin cacheをRun専用領域に置いた |
| 書込先を直した後の実Codex試行（Run `10fa650da93e4f91a2d48b6684b75b1c` など）でrestoreが `NuGet.targets: Value cannot be null. (Parameter 'path1')` により失敗した | エラー文だけでは不足している環境条件が分からず、追加診断が必要だった | NuGetの詳細スタックを追い、Windowsのmachine-wide settings path解決時に `ProgramFiles(x86)` / `ProgramFiles` が必要だが、Codex shellの環境変数許可リストから欠落していたと特定した。読み取り専用値として明示的に渡した |
| 成功したE2EでもOrchestrator内部Runは `NeedsHumanInput` になった | 内部Reviewerは成功したrestore/build/checkを確認できなかった | 固定artifactに実行証跡が含まれていなかったため。これは#79の課題として分け、Issue #84の環境修正とは混同しない。独立PR Reviewerは保存されたRun記録を照合した |
| 中間のPR CIで回帰テストが失敗した（Run #178） | 環境修正の検証が一巡追加で必要になった | 複数のCodex `--config` 引数があるとき、テストが最後の値をwritable rootsと誤認していた。`writable_roots` キーを選択するよう直し、最終CI #181でWindows・Ubuntu両方のUnit Test jobが成功した |

## 時間を要した要因

- 実Codexのsandbox・shell環境と、通常のローカル.NET実行環境が異なる。通常のbuild/testだけでは、子プロセスの環境許可不足を検出できなかった。
- 最初の失敗を直すと、次の段階でNuGet固有の環境前提が表面化した。restore前に一連の前提を点検する仕組みがなかった。
- NuGetの `path1` エラーは原因を直接説明しないため、失敗ログとスタックを段階的に深掘りする必要があった。
- CIで回帰テストの引数選択ミスが見つかった。テスト自体が実際のCodex起動引数構成を十分に再現できていなかった。
- 実行証跡の引き継ぎ不足は成功Runを `NeedsHumanInput` にするが、環境問題とは別の原因である（#79）。

## 今後の環境改善候補

以下は提案であり、実装方針の決定ではない。

1. **Codex起動前の環境事前チェック**: Run専用profile、`DOTNET_CLI_HOME`、NuGet cache、`TEMP` などの書込先が存在し、実際のsandbox許可に含まれるかを確認する。
2. **実際のshell policyを使った軽量スモーク**: Codexに依頼する前に、子プロセスへ渡る環境変数とNuGet設定解決を検証する。Windows固有値の有無も対象に含め、通常環境の成功で代替しない。
3. **原因が分かる事前診断**: 欠落した環境変数や許可外パスを、秘密値や不要な個人パスを出さずに特定できるようにする。前提未達ならLLMプロセス起動前に止める。
4. **起動契約の回帰テスト**: 複数の `--config` 値、Implementer/Reviewerごとの権限差、Run専用パスとWindows環境値を含む実際の起動引数を検証する。

優先度と具体的な設計は、[Issue #90](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/90)で検討する。#79の証跡伝達はそのIssueに取り込まず、独立して扱う。

## 証跡と制約

- 最終成功Run ID: `f6dc23ec024640f5938e2dc0ebdd6aaf`
- 最終成功Runではrestore、Release build、5件のチェックがImplementer自身の実Codex子プロセス内で成功した。
- OrchestratorのRun状態は `NeedsHumanInput` のまま。成功した検証証跡を内部Reviewerへ渡す問題は未解決で、#79の対象である。
- 詳細なRunログ・stdout・Testbed checkoutはAteliers-PoCのローカル作業領域に保存されている。GitHub PRには独立Reviewerの確認コメントと結果要約が残るが、ローカルの生ログ自体はこのリポジトリには含めていない。
- このレポート作成に伴うコード変更・テスト実行はない。