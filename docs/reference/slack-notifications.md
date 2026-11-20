# Slack通知設計

> 作成日: 2026-11-21
> 更新日: 2026-11-21
> 作成者: yuu503

## 1. 目的と状態

本書は、Ateliers AI OrchestratorのSlack通知について、合意済みの設計方針と実装状況を記録します。ここに記すRun単位のスレッドやAgent生成メッセージは設計目標であり、現在すべてが実装済みという意味ではありません。

## 2. 確定した設計方針

- Agentは自分の作業結果を受けて、個性を反映した作業完了通知文を生成し、Orchestratorへ返します。
- Slackへの認証・配送、Runと通知の関連付けはOrchestratorが担当します。AgentへSlack Tokenを渡しません。
- OrchestratorはRunごとにSlackスレッドを設け、実行中のRun関連通知をそのスレッドへ投稿します。
- 最終結果はRunスレッドへの通知に加えて、通知チャンネルにも投稿します。
- Milestone 1の通知範囲は、次節の5つのタイミングです。Agent主導の途中経過通知は将来対象です。
- Botの基本運用は1 Agent = 1 Slack Botです。ただし、Agent設定が同じSlack Bot IDを参照することをスキーマで禁止しません。
- Agent ID、Slack Bot ID、Slack Bot User IDをそれぞれ別の識別子として扱います。既存Bot IDは当面維持します。

## 3. Milestone 1の通知タイミング

| # | 通知タイミング | 本文の生成主体 | 配送先 | 補足 |
|---|---|---|---|---|
| 1 | OrchestratorがRunを開始 | Orchestrator | チャンネル（スレッド親メッセージ） | 以降のRun通知をまとめる親 |
| 2 | Agentの作業開始 | Orchestrator | Runスレッド | 実行状態から通知 |
| 3 | Agentの作業完了 | Agent | Runスレッド | Agentが生成した完了文を使う |
| 4 | OrchestratorがRunを終了 | Orchestrator | Runスレッド | 成功・失敗・人間判断待ち等の最終状態を示す |
| 5 | Runの最終結果を通知 | Orchestrator | 通知チャンネル | スレッドを開かなくても最終結果を把握できる要約 |

通知は内部ライフサイクル値の羅列ではなく、作業の開始・完了・指摘・次の対応を伝える人間向けの短い文章にします。内容は実行で確認できた作業結果だけを使い、Run ID、Role、イベント名、状態値、ISO日時を通常本文へ重ねて表示しません。通知文が利用できない場合も、実行状態に沿った短い代替文をOrchestratorが作成します。

人間の判断が必要な場合は、通常の短い通知より詳しい説明を優先します。停止理由、Reviewerが実際に報告した指摘、または最終確認が必要な作業結果を本文に含め、判断に必要な情報が記録されていない場合はその旨を明示します。

Run開始時はOrchestratorがチャンネルに親投稿を行い、その直後に同じスレッドへ `Run ID: <id>` だけを返信します。この返信はRun開始イベントに付随する情報投稿であり、追加のワークフローイベントは作成しません。通常通知や最終結果にはRun IDを含めません。親投稿・Run ID返信の配送結果とSlack投稿時刻はRun IDに結びつく `slack_delivery_result` 監査イベントへ記録し、返信に失敗した場合もRun IDと親投稿を追跡できます。

## 4. 通知内容と責務

### Agentが返す作業完了文

共通項目は `config/agents/instructions/slack-report.md` に定める暫定設計です。少なくとも担当AgentとRole、作業内容、結果、主な変更・レビュー所見、実施した確認、未完了や人間の判断が必要な事項を含めます。Agentの個性は文体や補足に反映できますが、事実や検証結果を誇張したり、必須情報を省いたりしません。

Slack向け文章とRun ID、イベント、状態、時刻、試行番号などの機械的な実行情報は別に扱います。Slack表示文は1〜3文程度にまとめます。内部実行情報は表示せず、意図しないメンションの無効化や秘密情報の安全なフォールバックを維持します。

AgentからOrchestratorへ完了文を返す構造の概念例です。プロパティ名・状態値を含め、暫定案であり、現行のAgent応答形式や確定スキーマではありません。

```json
{
  "result": {
    "status": "Succeeded",
    "summary": "実装と確認が完了しました"
  },
  "notifications": [
    {
      "event": "WorkCompleted",
      "message": "Siriusです。実装と確認が完了しました！"
    }
  ]
}
```

### Orchestratorの責務

- Run用の親投稿とスレッド識別情報を管理する。
- 通知イベントを該当Run・Agent・Roleに結び付ける。
- Agentの返した作業完了文を受け取り、選択されたSlack Botから配送する。
- 実際の実行状態をもとに開始・終了・最終結果を知らせる。
- Agent文面が得られない場合に最低限の代替文を用意する。
- Slack送信の成否を実行の成否と区別して記録する。

## 5. Bot設定と秘密情報

Agent個別設定にある `slackBotId` は `slack.json` の `bots[].id` を参照します。たとえばAgent ID `sirius` が既存Bot ID `implementer` を参照しても構いません。これは両IDが同一という意味ではありません。`bots[].botUserId` はSlack側のユーザーIDで、投稿者を切り替える認証情報の代わりにはなりません。実際の投稿者は送信に使うBot Tokenで決まります。

現行の設定ではBot Tokenを `slack.secrets.json` で管理し、リポジトリのサンプルやコミット対象に含めません。テスト送信では専用の `testChannelId` を使い、通常チャンネルと分けます。詳細は[設定サンプルの説明](../../config/README.md)を参照してください。

## 6. 現行実装で確認できる範囲

この文書作成時にコードと設定例で確認した動作は次のとおりです。

- CLIはSlack通知を無効化するか、テストチャンネル／通常チャンネル経由で使うモードを扱います。
- `SlackLifecycleNotificationDispatcher` はAgent設定のRole→Slack Bot ID対応を使い、既存の `slack.json` と `slack.secrets.json` から各Botを解決します。Agent設定がない場合は既存3 Bot IDにフォールバックします。
- Run開始時は人間向けの親投稿をチャンネルへ送り、その同じスレッドにRun IDだけを返信します。親投稿時刻をRunのメモリー内ルートとして保持し、Agent開始・完了・失敗、Orchestrator終了を同一チャンネルのスレッドへ返信します。最終結果はRun IDを含めず、別のチャンネル投稿として送ります。
- Agent完了報告はタスク応答と分離した必須ラベル付きブロックから取り出します。必須項目欠落、空文、長さ超過、または認証情報のパターンを検知した場合は、Orchestratorの短い安全な代替文を使います。Slack通知の失敗は実行状態を失敗扱いにせず、配送結果を本文なしの `slack_delivery_result` JSONLイベントへ記録します。配送監査にはRun ID、対象イベント、Slack投稿時刻、失敗した場合の段階を記録します。Run ID返信に失敗しても親投稿時刻と失敗段階を同じRunに関連付けます。終了時にキューへ残った通知もイベントごとに `abandoned` と記録します。
- Orchestrator生成文は実際のRun状態と作業概要を自然な文章で知らせます。Slack本文からRun IDやRole、イベント名、状態値、ISO日時などの機械情報を除き、内部監査ログには構造化情報を残します。
- Agent生成文面の `@channel`、`@here`、`@everyone`、`<!...>`、`<@...>` はSlackメンションとして動作しない形へ変換します。テストモードでは複数Roleで同じSlack Bot IDを参照できますが、異なるBot IDが同じBotユーザーIDを主張する設定は拒否します。
- 実装済みの通知タイミングはRun開始、Agent開始、Agent完了、Orchestrator終了、最終結果です。送信は既存の有界キューで順番に行い、スレッドIDはプロセス内だけで保持します。
- Bot Tokenと `botUserId` の一致をSlack認証確認で検証する経路は維持され、テストチャンネルは通常チャンネルと異なることを要求します。テストでは従来どおり専用の `testChannelId` を使います。

## 7. 将来構想・未確定事項

- Agentが作業途中にOrchestratorへ通知を依頼するインターフェース。
- 複数Agent割当時の選択・実行方法、共有Botの運用条件。
- RunスレッドIDの永続化、再起動・再送時の重複防止、送信失敗の再試行。
- 通知イベントのバージョン、通知文の構造化、長文処理、Slack表示規約。
- タイムアウト、キャンセルなど、Milestone 1の5タイミング以外の独立通知。

## 8. テスト時のSlack通知

- UnitTestsとFakeTestsではSlack実APIへ接続しません。FakeTestsがCLI `run` を起動する場合は `--slack-notifications disabled` を指定し、通知配送の決定論的な挙動はFake送信者で検証します。
- Slack実APIを使う検証は明示opt-inのIntegrationTestに限ります。Orchestratorのライフサイクル通知をSlackへ送るテストは `ATELIERS_RUN_ORCHESTRATOR_SLACK_INTEGRATION_TESTS=1` が必要で、全ての通知を専用 `testChannelId` に送ります。
- このIntegrationTestはSlack Bot認証IDを送信前に確認し、Run ID返信を含む配送監査を検査します。明示opt-inを付けて実行した場合は複数の実Slack投稿が発生します。

## 関連資料

- [Agent設定と指示管理](agent-configuration-and-instructions.md)
- [AIエージェントのSlack通知機能](../Feature/ai-agent-slack-notifications.md)
- [Slack E2Eの通知モード](e2e/README.md)
- [開発用IntegrationTestの実行範囲](development-integration-tests.md)
