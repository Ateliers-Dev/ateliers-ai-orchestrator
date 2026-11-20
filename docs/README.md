# docs

> 作成日: 2026-11-18
> 更新日: 2026-11-21
> 作成者: yuu503

このフォルダには、プロジェクトの方針、計画、要求、実装・運用の参照資料、実施結果を置きます。

- [issues/](issues/): 個別課題の背景、判断経緯、関連資料
- [Feature/](Feature/README.md): 利用者向けにプロダクト機能、特徴、利用価値、制約を説明
- [plans/](plans/): 開発計画とマイルストーン
- [requirements/](requirements/): 合意・承認された要求仕様
- [reference/](reference/): 実装の仕様、導入・操作手順、E2E手順
  - [Agent設定と指示管理](reference/agent-configuration-and-instructions.md): Agentの識別、Role割り当て、指示ファイルの設計
  - [Slack通知設計](reference/slack-notifications.md): 通知イベント、Agent報告、Slack配送の設計と現状
  - [ワークフロー・品質ゲート・Run再開](reference/orchestration-workflow-and-human-resume.md): Milestone 1の将来設計と現行状態の差分
  - [テスト種別と責務](reference/testing-classification.md): UnitTests・FakeTests・IntegrationTestsの境界とAI接続テストの原則
  - [ImplementerからReviewerへの検証証跡](reference/implementer-reviewer-validation-evidence.md): 実装担当のビルド・テスト記録とレビューへの引き渡し
  - [開発用IntegrationTest](reference/development-integration-tests.md): Testbed再利用、実Codex opt-in、最終受入E2Eとの責務分担
- [requirements/milestone-1/](requirements/milestone-1/): Milestone 1自己改善ワークフローとRun再開の要求
- [reports/](reports/): 作業・検証の結果と証跡。Milestone 1は[reports/milestone-1/](reports/milestone-1/)を参照
- このフォルダ直下: 複数の分類に関わる共通方針・ゲート
- [guidelines/](guidelines/README.md): 運用・開発におけるガイドライン
  - [作業プロセスの妥当性と改善](guidelines/work-process-validity-and-improvement.md): 問題の指摘、自律修正、適切な停止と報告、再発防止
  - [エラー時の診断記録ポリシー](guidelines/error-diagnostic-recording-policy.md): 共通原則と診断手順の索引
  - [診断記録方法](guidelines/diagnostic-recording-method/README.md): 特定ケースごとの診断手順
- [Issue #79 テスト分類見直し記録](issues/issue-79-test-classification-review.md): 実Codexの自然言語assertと、テスト体系の棚卸し・是正

新しい種類の文書やフォルダを追加・変更するときは、この README と該当フォルダの README も併せて更新してください。
