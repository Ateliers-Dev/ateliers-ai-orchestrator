# config

> 作成日: 2026-11-18
> 更新日: 2026-11-18
> 作成者: yuu503

このフォルダには、ローカル開発で使う Slack 設定ファイルの雛形があります。実行時の Slack 設定ではなく、実際の設定を作るためのサンプルです。

- `slack.json.example` は、Slack チャンネル ID、Bot の識別情報、通知の有効・無効などを記入する雛形です。
- `slack.secrets.example.json` は、各 Bot の OAuth Token を記入する雛形です。

ローカルで Slack 通知を使う場合は、雛形をそれぞれ `slack.json` と `slack.secrets.json` にコピーし、プレースホルダーを実際の値に置き換えてください。CLI はユーザー設定フォルダの設定を優先し、開発環境ではリポジトリ内の `config` も参照します。詳しい設定手順は[ルート README](../README.md)を参照してください。

実際の設定ファイル `slack.json` と `slack.secrets.json` は `.gitignore` の対象です。Token をサンプルファイルや Git の管理対象へ記録したり、チャットや Pull Request に貼り付けたりしないでください。
