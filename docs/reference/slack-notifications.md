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

この5項目は通知タイミングの設計です。各項目のイベント名、Slack APIペイロード、投稿順序保証、再試行・重複排除の仕組みは別途実装仕様として定めます。通知文がない、空、または利用できない場合の最小限の代替文もOrchestratorが作成し、実行状態の通知が失われないようにする案です。

## 4. 通知内容と責務

### Agentが返す作業完了文

共通項目は `config/agents/instructions/slack-report.md` に定める暫定設計です。少なくとも担当AgentとRole、作業内容、結果、主な変更・レビュー所見、実施した確認、未完了や人間の判断が必要な事項を含めます。Agentの個性は文体や補足に反映できますが、事実や検証結果を誇張したり、必須情報を省いたりしません。

Slack向け文章とRun ID、イベント、状態、時刻、試行番号などの機械的な実行情報は別に扱います。Slack表示での最終的な構造化、文字数制限、改行・リンク、メンションの扱いは未確定です。

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
- `SlackLifecycleNotificationDispatcher` は `orchestrator`、`implementer`、`reviewer` のBot設定を読み込み、Role別に固定形式の通知を送ります。
- 現行イベントは `started`、`completed`、`failed`、`needs_human_input` で、Run ID、Role、状態、時刻等の実行情報を本文に含みます。
- Bot Tokenと `botUserId` の一致をSlack認証確認で検証する経路があり、テストチャンネルは通常チャンネルと異なることを要求します。
- この確認時点では、Agentが生成した完了文をSlack本文に使う経路、RunごとのSlackスレッド作成・返信、最終結果専用のチャンネル投稿は確認できません。
- このため、現行の固定ライフサイクル通知は、上記Milestone 1設計の5タイミングが完成していることを示しません。

## 7. 将来構想・未確定事項

- Agentが作業途中にOrchestratorへ通知を依頼するインターフェース。
- 複数Agent割当時の選択・実行方法、共有Botの運用条件。
- RunスレッドIDの永続化、再起動・再送時の重複防止、送信失敗の再試行。
- 通知イベントのバージョン、通知文の構造化、長文処理、Slack表示規約。
- タイムアウト、キャンセル、修正・再レビュー開始など、Milestone 1の5タイミング以外の独立通知。

## 関連資料

- [Agent設定と指示管理](agent-configuration-and-instructions.md)
- [AIエージェント機能の概要](../Feature/README.md)
- [Slack E2Eの通知モード](e2e/README.md)
