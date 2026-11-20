# ワークフロー・品質ゲート・Run再開の設計

> 作成日: 2026-11-21
> 更新日: 2026-11-21
> 作成者: Codex

> 設計状態: 将来設計。現行実装の説明ではない。人間が文書を確認するまで本体コード実装と最終E2Eを開始しない。

要求元は[Milestone 1自己改善ワークフローとRun再開要求](../requirements/milestone-1/self-improvement-workflow-and-resume.md)。この文書は契約案・互換性・失敗時挙動を定める。

## 1. 現行状態の確認

2026-11-21のmain確認では、RunStateにPrepared/Implementing/Reviewing/Revising/ReadyForHumanReview/NeedsHumanInput/Failedがある。ReadyForHumanReview、NeedsHumanInput、Failedはいずれも状態遷移先がなく終端。MCPはrun_orchestrationとGuide/Schema/runtime診断を持つが、既存Runを指定するresume Toolはない。現行runは固定Implementer/Reviewerサイクルで、5段階設計は未実装。PR最終承認・マージは人間が行う。

## 2. 5 Phaseと証跡

| Phase | 変更範囲 | 成功ゲート/証跡 |
| --- | --- | --- |
| 1 実装 | 本体コード。テスト変更なし | 開始/終了SHA、許可範囲内のdiff、実行記録。テストbuild失敗を許容 |
| 2 コードレビュー | 変更なし | Implementerと異なるReviewer Agent ID/実行ID。対象SHA/diff digestと結果 |
| 3 テスト | テストコードのみ | 本体コード差分なしをGit情報で判定。テスト実行記録 |
| 4 全体レビュー | 変更なし | Phase 2とは別の実行。設計版、本体/テストSHA/diff、指摘結果 |
| 5 PR/最終レビュー | PR作成とレビュー | PR head/base SHA、別Reviewer ID/実行、差分digest、CIと指摘解消。成功後ReadyForHumanReview |

レビュー後に対象SHAが変わればそのレビューを失効とする。Reviewer IDはrole名やRun IDではなく起動元が発行した識別子とする。Agent起動基盤が識別子を安定して提供できない場合はゲート不合格/NeedsHumanInput。

## 3. ゲートと差し戻し

標準順序はPrepared→Phase1→2→3→4→5→ReadyForHumanReview。Phase 2の実装指摘はPhase 1、Phase 4/5の本体指摘はPhase 1、テスト指摘はPhase 3へ戻す。すべて担当Phaseと対象SHA、理由、試行回数を記録する。

ゲートはRun/phase ID、branch/SHA、diff/artifact digest、変更path、必要なテスト/build終了コードと件数、Reviewer ID/実行/対象SHA/結果を検証する。証跡の無い条件はunknownで不合格。reviewer ID重複、対象SHA差、未解決指摘、変更範囲違反で先に進まない。上限値は既存のPRレビュー最大3回・gate再試行1回を候補とし、Phase横断値は実装前の未決。

## 4. 状態と保存

| 状態 | 意味/遷移案 |
| --- | --- |
| Prepared | 契約検証/初期checkpoint。Phase 1へ |
| Implementing | Phase 1/3をphase IDで区別。review/停止/失敗へ |
| Reviewing | Phase 2/4/5をphase IDで区別。次Phase、差戻し、停止、完了へ |
| Revising | 指摘を受けて実装/テスト修正。Phaseに応じて戻る |
| NeedsHumanInput | 再開可能な判断待ち。停止質問と安全checkpoint必須 |
| ReadyForHumanReview | PRとPhase 5レビュー完了。人間の最終確認待ち、AI再開なし |
| Failed | 自動継続不能。通常resumeなし |

推奨はRun directory内の版付きcheckpoint manifestと既存JSONL event logの追記を使う。manifestはcurrent checkpointを示し、JSONLはtransition/decision/effect履歴を保つ。新DBは提案しない。manifestにrun/contract hash、state revision、phase/attempt、質問/選択肢、branch/base/head SHA、worktree/diff digest、artifact refs/digests、pending decision/effect ledgerを含める。ファイル名、atomic replace/fsync、lock方式は未決。

NeedsHumanInputを非終端にする変更は既存clientが終端扱いする場合に影響する。isTerminal/canResumeの明示fieldかprotocol version変更かを決め、旧Runは再開checkpointがない限りlegacy-not-resumableとする。

## 5. MCP再開契約案

新規MCP Tool名はresume_orchestrationを候補とし、対応CLIコマンド/プロトコル契約を追加する。これは現行Toolではない。WorkはRun状態と保留質問を取得し、人間の回答を同じRun IDでMCPへ渡す。Slack経路は提供しない。

要求する論理field: runId, decisionId, expectedStateRevision, expectedPhase, expectedHeadSha, expectedDiffDigest, answer.choice/answer.text。ID、revision、checkpoint SHA、回答を必須とする。正確なJSON名・choice schemaは未決。

応答はaccepted/duplicate、same runId/decisionId、state、resumedPhase、new state revision、安定したerror code/current revisionを返す案。成功でもRun IDを変えない。拒否時は状態/成果物を変更しない。

### 再開順序

1. 入力/回答/IDを検証しRun単位の排他を取得する。
2. NeedsHumanInput、保留質問、許可された判断、状態revisionとPhaseを確認する。
3. contract/checkpoint integrity、branch/base/head SHA、worktree status/diff digestと全参照artifact digestを停止時記録と照合する。変更を自動resetしない。
4. decisionId ledgerを確認する。同じID/内容は過去の結果を返す。同じID/異なる内容と競合revisionは拒否する。
5. 回答受理とaudit記録を永続化してからagentを起動する。永続化が不確実なら開始しない。
6. 保存済みsafe checkpointからだけ再開する。side effectはoperation ID/evidenceで照合し、結果が不明なら停止し重複実行しない。
7. 受理/再開/完了/拒否をauditへ追記する。

FilesystemとGitHubの外部副作用は単一transactionにできない。pending/confirmed effect journalでreconcileする。保証は二重回答受理を防ぎ、盲目的な副作用再送を防ぐことであり、外部系のexactly-onceを主張しない。

## 6. 失敗時の挙動

- 不正判断、decision ID内容衝突、古いrevision、状態不一致: 拒否、Runは不変。
- Branch/SHA/artifact/diff mismatch: NeedsHumanInputを維持し、証跡を記録して人間へ返す。
- 欠落/未知の旧checkpoint: 自動推定せず再開不可を返す。
- checkpoint破損、回復不能な保存障害: 診断後Failed。回答を受け付けない。
- 外部副作用結果の不確実性: 照合できなければ再送せず判断待ちへ。
- audit永続化失敗: 回答を受理せずagentを起動しない。

## 7. 検証計画

本設計文書PRでは実施しない。実装時にUnit/FakeTestsでPhase順/allowlist、独立ReviewerとSHA、gate判断、差戻し/上限、状態遷移、旧checkpoint、通常再開/mismatch、重複/並行回答、監査/中断復旧、重複push/PR/comment防止を決定論的に検証する。MCP→CLI→Coreのrequest/response、互換性、秘匿もfakeで確認する。実Codex/Slack/GitHub副作用は文書確認後の別E2Eだけで扱う。

## 8. Issue/PR

- #109: Milestone 1最終Testbed E2E。5 Phase/resume受入を追加するか文書確認後に判断。ここでは変更しない。
- #79: Implementer検証証跡を再利用する。
- #80: push前検査を再利用し、過去の原因未確定を維持する。
- #84: 解決済み子プロセス.NET環境との整合を受入確認する。
- #36: Slack通知とresume操作を区別。Slack resumeは対象外。
- PR #116: 最終レビュー後のマージ判断・操作は人間。
- PR #118: Slack通知変更はresume経路を提供しない。