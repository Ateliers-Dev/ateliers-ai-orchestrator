# Issue #79・#80の受入条件見直しと限定検証

> 作成日: 2026-11-20
> 作成者: Codex

## 判断

個別Issueの受入条件を元の要求に対して必要十分な範囲に定め、Milestone全体の最終E2Eとは分ける。

- **#79はクローズ可能**。要求は、Implementerの検証証跡を固定成果物に紐づけて安全にReviewer要求へ渡し、欠落・不正を検出できること。PR #96の保存・受け渡し実装とFakeTestsの決定論的検証がこの要求を満たす。実AIによるReviewer応答や判断品質の正しさは再現性のあるIssue合否条件にせず、最終E2Eで観察する。
- **#80は限定検証を通過した場合にクローズ可能**。元Run `7d7e0e60ff494ff4994e2b8f2d4e75f2` の `push.secret-scan-and-whitespace-check` exit 2の直接原因は未確定のまま記録する。PR #101後のPublisher push前チェックについて、隔離Git fixtureで通常CRLF差分が通り、意図した末尾空白が拒否されることを確かめる。Reviewer起動、実push、PR作成はこのIssueの条件に含めず、Milestone最終E2Eに分ける。
- Milestone #109はMilestone全体の独立した最終E2Eゲートとして管理する。#79・#80を閉じてもMilestone完了とはみなさない。

## #79の証跡

- PR [#96](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/96) で、証跡JSON・ログを専用ディレクトリに保存し、サイズ・パス・参照・スキーマを検査したうえでReviewer要求へ渡す実装をマージ済み。
- FakeTestsの `CodexFakeProcessesCompleteIndependentReviewSlice` は、Reviewer要求内の `validationEvidenceReference` と固定成果物SHAを確認する。欠落・不正形式の処理も決定論テストで扱う。
- PR [#106](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/pull/106) は、実Codex接続テストを応答到達までに限定し、Windows/UbuntuのUnit/Fake CIを通過してマージ済み。ユーザー環境で実Codex IntegrationTest成功も確認されたが、Reviewerの証跡判断を実証したとは記録しない。
- 独立調査の完了条件は、保存・安全な受け渡し・欠落検出とする。自然言語のReviewer評価を必要条件にしても決定論的な保証は増えず、PR #96/106のFakeTestsと重複するため、これはMilestone E2Eの観察項目にとどめる。

## #80の限定検証記録

以下はPR #101後の安全なローカルPublisher相当チェックで確認する。元Runを再現した、または直接原因を特定したとは扱わない。

| 項目 | 記録 |
| --- | --- |
| 検証対象SHA | `7f1bf4cca1fa82a88b1cfa8e8cf1ed6bd3297cd1`（PR #106 merge commit、検証コードを含むmain） |
| 対象段階 | Publisherのpush前 `secret-scan-and-whitespace-check` 相当。隔離Git index上の `git diff --cached --check` |
| チェック名 | `PublisherNormalizesCrLfSnapshotAndRejectsTrailingWhitespace`（FakeTests） |
| GitHub/Slack/実AIへの接続 | なし |
| 実行環境・コマンド | Windows、`dotnet run --project tests/Ateliers.Ai.Orchestrator.FakeTests/Ateliers.Ai.Orchestrator.FakeTests.csproj --configuration Release --no-build --no-restore -- --run-fake-case-direct "PublisherはWindows CRLF snapshotを正規化し意図した末尾空白を拒否する"` |
| 通常CRLF差分 | テスト内で本番PublisherのWindows Git config overlay (`core.autocrlf=true`) を確かめ、CRLF改行snapshotに対する隔離indexの `git diff --cached --check` が終了コード0で通過 |
| 意図的な末尾空白 | 同チェックが終了コード2・出力ありで拒否することをテスト内で確認 |
| 結果 | Fake case passed、コマンド終了コード0 |
| 実行ログ | `work/issue-80-publisher-pre-push-check.log`（ローカル生成。リポジトリには追加しない） |
| 判定 | 限定検証は合格。元Runの直接原因は未確定のまま残す |

同じSHA・Windows上の既存Release buildからFakeTests直接実行経路 `--run-fake-cases-direct` も実行し、86 passed / 3 skipped / 0 failedで終了しました。Skipはこの既存run報告どおりWindowsで利用できないUnix modeまたはjunction作成ケースです。新しい作業checkoutではNuGet restoreが完了せず詳細診断も得られなかったため、上記2つの実行は同一SHA `7f1bf4c` の既存ビルド済みcheckoutを使いました。PRのWindows/Ubuntu必須CIで現行差分の品質ゲートを確認します。

### 未確認・残るリスク

- 元Runの直接原因・再現条件は判明していない。CRLF問題は後続Runで観測された別問題であり、元Runの原因と同一とは断定しない。
- 隔離fixtureはPublisherの関連snapshot/whitespace処理を検証する。実Reviewer、Slack、push、GitHub PR作成までの公開E2Eは検証しない。
- Milestone最終E2EにPublisherを含む実際のPR必須経路の確認が残る場合、#109のゲートで実施する。
