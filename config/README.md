# config

> 作成日: 2026-11-18
> 更新日: 2026-11-18
> 作成者: yuu503

このフォルダには、ローカル開発で使う Agent と Slack の設定雛形があります。実行時の設定ではなく、実際の設定を作るためのサンプルです。

- `agents.json.example` はRoleごとのAgent割当です。Milestone 1では `Implementer` と `Reviewer` にそれぞれ1 Agentを指定します。`Reviewer` の `specialty` はレビュー観点として実行入力と監査記録に含まれます。
- `agents/implementer-sirius.json.example` と `agents/reviewer-altair.json.example` はAgent個別設定です。`slackBotId` はSlack Bot IDを参照し、Agent IDとは別の識別子です。
- `agents/common-instructions.md.example` と `agents/instructions/slack-report.md.example` は共通指示とSlack報告の指示です。個別指示は `agents/custom/` に配置し、個別JSONの `customs` から相対参照できます。

- `slack.json.example` は、Slack チャンネル ID、Bot の識別情報、通知の有効・無効などを記入する雛形です。
- `slack.secrets.example.json` は、各 Bot の OAuth Token を記入する雛形です。

ローカルで Slack 通知を使う場合は、雛形をそれぞれ `slack.json` と `slack.secrets.json` にコピーし、プレースホルダーを実際の値に置き換えてください。CLI はユーザー設定フォルダの設定を優先し、開発環境ではリポジトリ内の `config` も参照します。詳しい設定手順は[ルート README](../README.md)を参照してください。

Agent設定を使う場合は、`agents.json.example` を `agents.json` にコピーし、Agent JSON、共通指示、Slack報告指示の `.example` をそれぞれ拡張子を外したファイル名へコピーします。CLIはユーザー設定フォルダの `config/agents.json` を優先し、開発環境では実行ファイルから上位へたどった `config/agents.json` も参照します。Agent IDとRoleの不一致、複数割当、agentsフォルダ外への指示参照、シンボリックリンク参照はRun開始前に拒否します。設定を変更しても、実行中のRunには反映されません。

実際の設定ファイル `slack.json`、`slack.secrets.json`、Agent JSON、Agent指示文書は `.gitignore` の対象です。Token をサンプルファイルや Git の管理対象へ記録したり、チャットや Pull Request に貼り付けたりしないでください。Slack通知の接続設定またはAgentが参照するBotが不足した場合は通知をスキップして警告を出し、RunとJSONL監査記録は継続します。
