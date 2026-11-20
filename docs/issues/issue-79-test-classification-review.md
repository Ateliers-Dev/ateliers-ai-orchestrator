# Issue #79 実Codexテストとテスト分類の見直し記録

> 作成日: 2026-11-20
> 更新日: 2026-11-20
> 作成者: Codex

## 目的

Issue #79の実Codex IntegrationTestがモデルの自由記述に依存していた問題を記録し、議論で合意したテスト責務を確定する。後続の開発で同じ分類・設計誤りを繰り返さないよう、既存テストの棚卸し、分類最適化、修正結果も追記する。

## 問題と経緯

Issue #79は、Implementerが行ったbuild・教材チェックの証跡を独立Reviewerが確認できる形で受け渡す課題である。PR #96は証跡を保存しReviewer要求へ組み込む実装を追加したが、当時の実Codex IntegrationTestは証跡引き渡しの成立に加えて、Reviewerの自由記述応答に `build`、`test`、`All word-count checks passed.` が含まれることを `Assert.Contains` で確認していた。

この判定は、実エージェントが通信可能かではなく、モデルが特定の英単語・定型文を返すかに依存する。意味が同じ別表現、モデルやプロンプトの違いでも落ち、逆に文言を含みながら証跡を確認していない応答も通り得る。応答のどれを「想定外」とみなすかをモデルに左右されるため、IntegrationTestの合否条件として不適切である。

議論では「Reviewerが正しい判定を返すか」「特定の回答形式・表現を守るか」はモデルやエージェントの性能評価に属し、接続試験で保証できないと整理した。Orchestratorのプログラム上の処理は、固定入力とFakeを使い、受け取った値の解釈、状態遷移、証跡記録・引き渡しを決定論的に検証できる。

## 根本原因

- テスト構成がFakeを使う決定論的な連携確認と、実外部システムへ接続する検証を明確に分けていなかった。
- 「Reviewerへ証跡が届くこと」と「Reviewerが証跡を正しく評価すること」を一つの実AIテストの成功条件に混ぜていた。
- 自然言語の部分一致を証跡確認の代理にしており、要求・入力の受け渡しそのものを検証できていなかった。
- 既存のテスト名・カテゴリ・プロジェクト配置が実際の依存境界を必ずしも表しておらず、後続の追加時に誤分類を踏襲しやすかった。

## 決定事項

最優先原則は **「マイルストーンの完遂より、Ateliers 開発プロセスの成熟を優先します。」** とする。ワークフロー最適化、判断の記録、再発防止を機能拡張や短期的なテスト通過より優先する。

テスト種別は [テスト種別と責務](../reference/testing-classification.md) に定義する。UnitTestsは内部ロジック、FakeTestsはFakeを利用する決定論的な受け渡し・状態遷移・異常系・証跡検証、IntegrationTestsは実外部システムとの接続・通信・実行完了を検証する。

作業の進め方にも同じ優先原則を適用する。検証方法が信頼できない場合に無理に成功へ進めず、問題を見つけて停止・報告したことと、原因・再発防止の記録を成果として扱う。詳細は[作業プロセスの妥当性と改善](../guidelines/work-process-validity-and-improvement.md)を参照する。

実AI IntegrationTestsの合否条件に、自由記述の内容、判断の正しさ、モデルの指示追従・回答品質を含めない。実際に応答が到達したことや呼び出しが完了したことの確認に限定する。Fakeで確認できる処理はIntegrationTestsに重複させず、モデル性能評価は別責務として扱う。

## 既存テスト棚卸し

初期確認では、次の混在を確認した。

| 現状 | 確認内容 | 分類上の論点 |
|---|---|---|
| `Ateliers.Ai.Orchestrator.UnitTests` | CLIの版情報テストなど内部ロジックを確認する。 | UnitTestsとして維持する。 |
| `Ateliers.Ai.Orchestrator.FakeTests` | OrchestratorのFake駆動テスト、Fake Codex/gh、子プロセスfixture、MCP/CLIのFake接続テスト。 | FakeTestsへ移動し、`Category=Fake` を付ける。GitHub・実Codex等に接続しない隔離fixture上のGit CLI試験もここへ分類する。 |
| `Ateliers.Ai.Orchestrator.IntegrationTests` | `CodexIntegrationTests`のファイルシステムfixture試験、実Codex opt-in試験、実ローカルGit CLI試験を含む。 | Fakeで検証する状態・report・evidenceのassertを除去。実Codexは接続と応答到達・終了を確認する。ローカルGit試験を外し、実外部接続の分類に限定する。 |
| 同上 | Reviewer応答中にbuild、test、教材の定型文があることを検証する。 | モデルの自由記述に依存し、証跡受け渡しの検証の代替になっていない。 |
| `Ateliers.Slack.IntegrationTests` | 実Slack Web API送信テストと、テスト設定ファイル探索・テストチャンネル選択のローカルテストが同居する。 | 設定探索・値選択テストは外部接続がなく、UnitTestsへ分類する。 |
| `Ateliers.Slack.UnitTests` | Slack設定探索、Fake HTTP handlerによるAPI client/dispatcherの要求・応答テストが同居する。 | 設定探索をUnitTestsに残し、Fake HTTP handlerによる連携検証をSlack FakeTestsへ移動する。 |

この棚卸しの初期観察に対する実際の移動・分割内容を下記へ記録する。

## 実行・検証に関する既知事項

PR #96のローカル環境では、Fake-onlyテストと実Codex IntegrationTestのtesthostが90秒・180秒待機しても接続できず中断された。一方、GitHub Actions run `44543822258` ではWindows・Ubuntuの既存ビルドと単体テストが成功した。最小xUnit再現でもローカル接続待ちが起き、その原因は未特定としてIssue #105へ切り出された。

このローカルtesthost阻害を今回のテスト分類不備やコードの失敗と混同せず、検証不可なら環境阻害として証跡を報告する。Issue #79の実Codex最終接続確認は、ユーザーの通常Windows環境で行う。

## 未決事項・停止条件

- Issue #79は実証跡引き渡しの回帰条件が満たされるまでオープンとする。PR #96は既にマージ済み。
- 実Codex接続テストの通常Windows環境での最終実行はユーザーが担う。
- `scripts/e2e/run-codex-e2e.ps1` はユーザー専用であり、本作業では実行しない。
- Slack実通知はOFFとし、実Slack APIへ投稿しない。
- `work/` 配下に事前に存在する未追跡成果物は変更・削除しない。
- 独立レビュー後も、PRの最終確認とマージ判断はユーザーに残す。

## 再発防止と作業順

1. この記録とテスト種別の正式定義を先にリポジトリへ残す。
2. `AGENTS.md`、開発ガイド、実行手順を分類に合わせる。
3. 既存テストを個々の依存と合否条件で棚卸しし、FakeTestsを含む構造へ最適化する。異なる責務を同じテストへ残さない。
4. 移行範囲を確定してから、Issue #79の実Codexテストから自由記述assertを除去する。
5. FakeTestsで証跡参照・Reviewer要求の内容を決定論的に検証する。
6. ビルド、該当テスト、CI、独立レビューを確認し、実Codex接続テストは通常Windows環境での確認事項として残す。

## 分類最適化の結果

### ファイル・プロジェクトの移動

- `Ateliers.Ai.Orchestrator.FakeTests` を追加し、Fakeを使うOrchestratorの旧単一テストプロジェクトから、Fake Codex/gh、ワークフロー、MCP/CLI shim、stdio CLIテストを移動した。既存の大規模catalogはテスト区分をFakeとして明示し、Issue #79の証跡引き渡しをReviewer要求の `validationEvidenceReference` で確認するassertを追加した。
- `Ateliers.Slack.FakeTests` を追加し、Fake `HttpMessageHandler` によるWebhook、Bot client、通知dispatcherの要求・応答・タイムアウト・秘匿動作テストを移動した。
- `SlackBotSettingsLocatorTests` とその探索helperをSlack UnitTestsへ移動し、IntegrationTestsはhelper sourceを共有参照する。設定探索テスト自体はSlackへの接続を行わない。
- Orchestrator IntegrationTestsの実Codexシナリオから、`NeedsHumanInput`、呼出し回数、report/events構造、ファイル差分、証跡の意味内容とReviewer応答に対するassertを除去した。成功条件はOrchestratorプロセスのexit code 0と、Implementerの応答ファイルが空でなく保存されたことだけとし、自然言語の内容は評価しない。Slack通知引数は常に`disabled`。
- 初期整理ではローカルGitを使うテストをIntegrationTests側に残していたが、これは実外部接続をIntegrationTestsに限定する合意と矛盾していた。今回、決定論的なローカルGit CLIテストとfixture cleanupテストを `Category=Fake` のFakeTestsへ移し、共有fixture型だけを `tests/Shared/IsolatedFixture.cs` に切り出した。IntegrationTestsは実Codexの明示opt-inテストのみを保持し、FakeTestsへのプロジェクト参照を持たない。
- Windows/Ubuntuの必須CIはUnitTestsとFakeTestsを実行する。別stepのCodex IntegrationTestsは明示opt-inがなければSkipされ、通常ゲートで外部接続を起動しない。Release workflowもUnit/Fakeプロジェクトを反映する。

### 未解決

- `OrchestratorTestCatalog` は歴史的に一つの大きなファイル/実行fixtureへ集約されている。分類・実行ゲートは分離したが、catalog内の各caseを小さな独立fixtureファイルへ整理する作業は今後の保守課題であり、今回は目的とするテスト種別の誤分類・重複を優先して解消した。
- 通常Windows環境での実Codex接続テストの実行は保留。PR #96以前のtesthost問題（Issue #105）と混同せず、今回の変更をユーザー環境で確認する。

### 検証結果

2026-11-20時点の確認:

- `dotnet restore Ateliers.Ai.Orchestrator.sln` はこの環境で終了コード1となり、MSBuild/NuGetの原因詳細を出しませんでした。NuGet脆弱性フィードも到達不可（NU1900）でした。既存の復元済みプロジェクト資産を使ってRelease solution buildを確認し、**成功・0 errors・7 NU1900警告**でした。これは通常のCI restore成功を代替しません。
- 標準 `dotnet test` でOrchestrator UnitTestsを実行したところ、VSTestは90秒後にtesthostへ接続できず中止しました。これはPR #96と最小再現で記録されIssue #105へ切り出されたtesthost阻害と一致する現象です。今回の実装やテスト分類の失敗とは断定しません。
- FakeTestsの既存testhost外直接実行経路では **86 passed、3 skipped、0 failed**。SkipはWindowsで利用できないUnix modeまたはjunction作成ケース。Issue #79用FakeケースのReviewer要求 `validationEvidenceReference` 検査と追加したGit安全性ケースも実行に含まれました。
- testhostを介するSlack Unit/FakeとIntegrationTestsはこの環境で未実行です。通常CIのWindows/Ubuntuでのrestore/test実行結果は未確認です。
- 実Codex接続IntegrationTest、ユーザー専用 `scripts/e2e/run-codex-e2e.ps1`、Slack実通知は実行していません。
- 独立レビューで、当初の応答確認がOrchestrator自身の標準出力のみを見ており、Codex応答の到達を保証しないという指摘があった。応答記録ディレクトリから空でないImplementer応答の存在だけを見るよう修正した。また、分類移動時に実Codex opt-inテスト本体まで削除されたことを検出し、元のテストを復元した。追加レビュー、PR更新、CI確認は続行中。
