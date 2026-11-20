# Milestone 1 自己改善ワークフローとRun再開要求

> 作成日: 2026-11-21
> 更新日: 2026-11-21
> 作成者: Codex

> 状態: 要求合意済み。詳細設計の未決事項は第8節。実装前に人間が文書を確認する。

## 1. 目的と変更理由

従来のワークフローでは、Milestone 1の目標である「Orchestratorが自身の改善を完遂する」について機能的成功は可能でも、要求する品質基準を達成できない可能性がある。5段階の標準と客観的な品質ゲートを導入し、Runの人間判断待ちから安全に再開できる設計を定める。

## 2. 範囲

含む: 5段階ワークフロー、独立レビュー証跡、客観的なPhaseゲート/差戻し/再試行上限、ChatGPT Work→MCP→Orchestratorの判断受渡しと再開、状態/成果物/ブランチ/SHA検証、冪等性と監査。

対象外: Slackからの再開、軽量/代替ワークフローの実装、自律マージ。この文書PRでは本体コード実装と最終E2Eを行わない。

## 3. 5段階標準

1. **本体機能実装**: 本体コードだけを変更し、テスト変更なし。テストビルド失敗は許容。変更範囲と開始/終了SHAを記録する。
2. **本体コードの独立レビュー**: 実装者と異なるReviewer Agentを明示起動し、Agent ID、実行ID、対象コミット/差分、結果を保存する。
3. **テスト追加・修正**: テストコードだけを変更し、本体コードの変更を禁止する。
4. **設計・本体・テストの独立全体レビュー**: Phase 2とは別のReviewer Agentを明示起動し、設計と全差分を確認する。
5. **PR作成・最終独立レビュー**: PR作成後のhead/base SHAを対象に別Reviewer Agentが確認する。修正は担当Phaseへ戻す。完了後はReadyForHumanReviewで人間の最終確認を待ち、マージは人間が判断する。

Phase 2/4/5は自己レビューを禁止する。ロール名が異なるだけでは独立性を満たさない。Reviewer Agent ID、別実行、対象SHA/差分、結果を検証可能なRun証跡にする。

## 4. 品質ゲート

Phase遷移はレビュー文だけでなく、Phase番号/試行数、変更ファイルと許可範囲、対象コミット/branch/SHA、diff/artifact digest、必要なビルド/テストの終了コード・件数、レビュー実行記録等の客観証跡で判定する。証跡が不明なら成功として進めない。問題は担当Phaseへ差し戻す。

既存方針にはPR修正/再レビュー最大3回、品質ゲート再試行最大1回がある。Phase横断の上限の数え方・追加上限は提案と未決事項を分け、実装前に確認する。上限超過または判断が必要ならNeedsHumanInputで停止する。

## 5. 状態と再開

- **NeedsHumanInput**: Runを質問・理由・Phase/checkpointとともに一時停止。人間の判断を受けた後、同じRunを安全な地点から再開可能。
- **ReadyForHumanReview**: PR作成と最終独立レビューを完了し、人間の最終確認を待つ。通常のresume対象ではない。
- **Failed**: 自動継続不能。通常の判断回答で再開しない。

ChatGPT WorkからMCPへ渡す判断には、Run ID、decision ID（冪等性キー）、判断内容、質問/Phase、停止時の期待revision/SHAを含める。回答は元Run IDに結び付け、新規Runにせず停止Phaseの安全な地点から再開する。

再開時に状態、質問、Phase、成果物とhash、branch、HEAD/base SHA、状態revisionを停止時証跡と照合する。内容不一致や回答形式不正では状態・成果物を変更しない。自由形式回答で安全ゲートを迂回させない。

同じdecision ID・同じ内容の再送は副作用を重複させず過去の結果を返す。同じIDで異なる内容は競合として拒否する。並行回答はrevision比較/Runロックで一件だけ受理する。push/PR等の外部副作用結果が不明なときは照合し、確証なしに再実行しない。

受理/拒否、実行者、時刻、質問・回答または秘匿化表現、Run/Phase、対象SHA、再開結果を追記型監査に残す。監査記録の永続化に失敗したら再開しない。

## 6. 互換性と失敗

現行RunStateではNeedsHumanInputは終端であり、現行MCPに既存Runの再開APIはない。新しい明示的なMCP再開ToolとCLI契約を設ける案とし、run_orchestrationに暗黙の再開動作を追加しない。

保存形式に版を持たせる。旧RunにPhase/checkpoint/証跡が無ければ自動推定せず再開不可とする。NeedsHumanInputを終端とみなす既存クライアントとの互換、status能力表示/protocol version、移行失敗時挙動は未決。

- 不正入力/古いrevision/許可外判断: Run状態を変えず拒否。
- Branch/SHA/artifact不一致: 自動再開せずNeedsHumanInputを維持し、差分を監査へ記録。
- checkpoint破損/回復不能な永続化障害: 診断証跡を残しFailed。
- 外部副作用の結果不明: 重複実行せず人間判断待ち。

## 7. 受け入れ条件

Unit/FakeTestsでPhase順・範囲、別Agent ID/実行/対象SHA、証跡に基づくゲート、差戻しと上限、合法な状態遷移、再開と旧Run拒否、branch/SHA/artifact mismatch拒否、同一/相反decision ID、並行回答、監査失敗/中断復旧、重複push/PR/comment防止を決定論的に確認する。MCP→CLI→Core契約と秘匿もFakeで確認する。最終E2Eは文書確認と実装の後に別実施し、PRはマージしない。

## 8. 実装前の未決事項

- Phase/Run全体に対する再試行上限と数え方。
- Phase 1でテストビルド失敗を許容しつつ、後段で必要なbuild/test gateをいつ必須にするか。
- Reviewer Agent IDの発行元、安定性、独立性の保証方法。
- 再開Tool/CLI名、引数/応答、state revisionとprotocol version。
- Workから届く判断の信頼主体、呼出元/人間の実行者識別と監査 provenance を何で確立するか。callerが自己申告する値を認証証拠にしない。認証機構を追加するかは現行stdio/MCP trust boundaryを確認してから決める。
- JSONL監査と既存run artifactsにcheckpointを追加する方法、原子性/lock/復旧方式。
- #109の最終受入条件に5段階・resume確認を加えるか、別のゲートにするか。

## 9. 関連Issue/PR

- #109は現行Milestone 1最終Testbed E2E。本文はこのPRでは変更しない。設計承認後、要求追加の要否を判断する。
- #79のImplementer検証証跡を再利用し、別証跡機構の重複を避ける。
- #80のpush前検査を再利用し、過去の原因未確定を改変しない。
- #84の子プロセス環境は解決済み前提。再開時の環境継承を受入で確認する。
- #36のSlack通知と人間判断受付は別責務。Slack resumeは対象外。
- PR #116の人間によるPR最終承認/マージ方針を維持する。
- PR #118のSlack通知変更は再開経路を提供しない。