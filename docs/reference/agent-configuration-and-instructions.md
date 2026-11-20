# Agent設定と指示管理

> 作成日: 2026-11-21
> 更新日: 2026-11-21
> 作成者: yuu503

## 1. 目的と適用範囲

本書は、Ateliers AI OrchestratorでAgentの個体、担当Role、実行設定、指示文書を管理するための合意済み設計と暫定スキーマを記録します。これは設計Referenceであり、ここに示すAgent設定一式が現行実装で利用できることを意味しません。実装状況は「現在の実装との対応」を参照してください。

## 2. 用語と確定事項

| 用語 | 意味 |
|---|---|
| Role | `Implementer` や `Reviewer` など、作業上の役割 |
| Agent ID | Roleや表示名とは独立したAgent個体の安定識別子 |
| Agent name | 人が読む表示名 |
| Assignment | OrchestratorがRoleへAgentを割り当てる設定 |
| Slack Bot ID | Slack設定内のBot定義を参照する識別子 |
| Slack Bot User ID | SlackがBotユーザーに割り当てるID |

次の方針を設計上の確定事項とします。

- RoleとAgent個体の識別子を分離します。Agent IDにRoleを埋め込みません。
- `assignments` のキーは単数形のRole名とし、値は配列にします。1件だけの場合も配列を使います。
- Reviewerの種類はRole名を増やさず、割当項目の `specialty` で表します。
- Agent設定からSlack Botを参照する場合も、Agent ID、Slack Bot ID、Slack Bot User IDを別の識別子として扱います。
- 運用方針は原則1 Agent = 1 Slack Botです。ただし、設定形式やバリデーションでBot共有を禁止しません。
- 既存のSlack Bot ID（`orchestrator`、`implementer`、`reviewer` など）は当面維持し、Agent IDとの対応だけを明示します。
- 共通指示のファイル名は `common-instructions.md` とします。Agent共通指示のファイルに `AGENTS.md` という名前を使いません。
- Orchestratorは共通指示とAgent個別指示を明示的にAgentへ渡します。

## 3. 暫定的な設定構成

以下は会話で合意した論理配置案です。リポジトリ内の現在の配置や実装済みファイルを示すものではなく、最終的なJSONスキーマも確定していません。

```text
config/
├── agents.json
└── agents/
    ├── common-instructions.md
    ├── instructions/
    │   └── slack-report.md
    ├── implementer-sirius.json
    ├── reviewer-altair.json
    └── custom/
        ├── sirius-tone.md
        └── altair-review-style.md
```

Slackの接続先と秘密情報は、既存の `config/slack.json` と `config/slack.secrets.json` の責務を維持します。Tokenなどの秘密情報をAgent設定やMarkdown、Git管理対象のサンプルに含めません。

### 3.1. `agents.json` の例

```json
{
  "assignments": {
    "Implementer": [
      { "agentId": "sirius" }
    ],
    "Reviewer": [
      { "agentId": "altair", "specialty": "code" },
      { "agentId": "vega", "specialty": "security" }
    ]
  }
}
```

この形は暫定案です。配列へ複数Agentを記述できることは、Milestone 1で複数Agentを実行・選択できることを意味しません。複数割当時に全員を実行するか、専門性などで選ぶかは将来の選択規則です。Milestone 1の実行は各Roleにつき1 Agentを前提とし、複数割当を実行する仕様は含めません。

### 3.2. Agent個別JSONの例

```json
{
  "id": "sirius",
  "name": "Sirius",
  "role": "Implementer",
  "slackBotId": "implementer",
  "defaultModel": "<model-id>",
  "defaultModelReasoningEffort": "high",
  "customs": [
    "custom/sirius-tone.md"
  ]
}
```

これはプロパティ名と配置を検討するための暫定例です。Agent IDの表記は小文字英数字とハイフンを基本案とし、一意かつ原則変更しない識別子とします。モデル名、推論強度、指示参照の形式、未知プロパティの扱い、相対パスの基準、スキーマバージョンの要否は最終確定していません。

## 4. 指示ファイルの役割

- `common-instructions.md`: すべてのAgentに共通する振る舞いと報告の原則。
- `instructions/slack-report.md`: Slack向け作業完了報告に必要な情報と表現の共通規約。
- `custom/*.md`: 特定Agentの個性、文体、追加の作業上の観点。

個別指示は共通指示や上位の安全・権限制約を上書きしません。Slack報告の必須項目を省略させず、表現や追加説明の範囲で個性を持たせます。

指示適用の設計上の順序は、上位の安全・権限・リポジトリ必須規約、Agent共通指示、用途別指示（Slack報告など）、Agent個別指示です。各指示をどのように結合し、実行環境へ渡すかは実装時に定めます。

## 5. 設定読込の暫定方針

設定JSONと参照MarkdownはRun開始時点で読み込み、そのRunでは同じ内容を使う案とします。実行中のファイル変更を同一Runに途中適用しません。読み込み時の検証、設定スナップショットの証跡化、エラー時の扱いは実装前に定めます。

## 6. 現在の実装との対応

この文書の設計と現在の実装を混同しないよう、リポジトリで確認した範囲を記録します。

- `config/slack.json.example` はSlack Bot ID、表示名、Slack Bot User ID、送信チャンネル、有効・無効を定義します。Tokenは別の `slack.secrets.json` にあります。
- Slackライフサイクル配送は現在、`orchestrator`、`implementer`、`reviewer` のBot IDをRoleへ固定対応しています。
- この確認時点では、`agents.json`、Agent個別JSON、Agent指示Markdownの読込機能は見つかりません。
- Agent IDとSlack Bot IDを関連付ける設定も、一般化された複数Agent割当も、現行のSlack通知経路では利用されていません。

## 7. 将来の検討事項

- `assignments` に複数Agentがある場合の決定的な選択・実行規則。
- Agentが複数Roleを持つことを許すか、許す場合のRole記述方法。
- JSONスキーマの統廃合、バージョン管理、互換性と検証方法。
- 設定ファイルとMarkdownの配布場所、パス解決、改ざん・秘密情報の扱い。
- 実行に用いたモデル、設定、指示の記録方法。

## 関連資料

- [Slack通知設計](slack-notifications.md)
- [AIエージェントのSlack通知機能](../Feature/ai-agent-slack-notifications.md)
- [Slack設定サンプルの説明](../../config/README.md)
