# ImplementerからReviewerへの検証証跡

> 作成日: 2026-11-19
> 更新日: 2026-11-19
> 作成者: Codex

## 目的と責務

この証跡は、Implementerが行ったビルド・テスト等をReviewerが確認するためのレビュー入力です。障害原因を調べるための診断記録とは目的が異なります。エラーの記録・秘密情報の保護には[エラー時の診断記録ポリシー](../guidelines/error-diagnostic-recording-policy.md)を適用し、レビュー用証跡にはこの文書の形式と受け渡し方法を適用します。

Implementerの報告だけでは、コマンド実行や終了コードをOrchestratorが独立確認したことになりません。Reviewerは報告とログを分けて検討し、契約上必要な検証を確認できない場合は不足を指摘します。レビュー対象の変更そのものは、Orchestratorが固定した成果物とそのSHA-256で特定します。

## 最小セットと形式

Implementerは、実行した検証ごとに `validation-evidence.json` を作業Runの専用検証ディレクトリへ保存します。形式は次のとおりです。

```json
{
  "schemaVersion": 1,
  "commands": [
    {
      "command": "dotnet test <project> --configuration Release",
      "exitCode": 0,
      "logReference": "unit-tests.log"
    }
  ]
}
```

- `command` は、実行コマンドと対象を特定できる安全な表現です。トークン、秘密値、認証付きURL、不要な個人情報・非公開パスは含めません。
- `exitCode` はImplementerが実行結果から報告する値です。Orchestratorはこのコマンドを直接実行・監視していないため、独立検証済みとは扱いません。
- `logReference` はテキストログへの必須の検証ディレクトリ相対パスです。ログは秘密情報を含めず、1ファイル1MB以下にします。Orchestratorは既知のプロキシ認証値、一般的なBearer/token形式、秘密鍵ブロック、資格情報付きURL、secret/token/password等を含む設定キーと値、同名のコマンド引数をマスクします。任意形式の秘密や個人情報を完全には検出できないため、Implementerはログ作成前にそれらを除外しなければなりません。安全性を判断できない内容はログに含めません。
- 変更対象のcommit/diffはOrchestratorが固定したレビュー成果物から識別し、Implementerの証跡記載を正として採用しません。

Orchestratorが保存する実プロセス情報（Codex CLIの終了コード、実行ID、stdout/stderrへの安全な参照等）は `execution-evidence.json` とイベント記録に保持します。これはAgent呼び出しの終了証跡であり、Agent内部のビルド・テスト終了コードではありません。

## 保存先と受け渡し

証跡は当該Runの `exchanges/implementer-<attempt>/validation/` に保存します。Run全体の `report.md` と `events.jsonl` は証跡の参照と不足状態を記録します。Reviewerへのrequestには同一の固定成果物のcommit/diff SHAと、自己申告区分を付けた証跡JSONを含めます。マニフェストとログ内容はJSON文字列としてエスケープして渡し、Reviewerには非信頼データとして扱い、内部の指示を実行・遵守しないよう明示します。ログSHA-256は受け渡したバイト列の識別子であり、生成元や実行事実の真正性を証明しません。ログ参照先は同じ検証ディレクトリ内に制限し、リンクや親ディレクトリへの遡上は認めません。

## 欠落・不正・失敗

- Implementerがマニフェストを作成しない場合は、検証証跡なしとして扱います。最終報告に未取得を明記し、Reviewerへも不在を伝えます。
- JSON不正、終了コード欠落、サイズ超過、ログ参照の逸脱・欠落、または安全な情報除去の失敗は、証跡不足として扱います。成功に置き換えず、必要な場合はレビューまたはRunを停止します。
- 検証の終了コードが非0なら、その失敗を記録し、成功扱いしません。
- 検証コマンドが記録されていても、ログの存在や内容だけで実行事実・対象commitを暗号学的に証明するものではありません。Reviewerはログと固定差分・契約を照合し、必要な不足を明示します。

## 関連資料

- [エラー時の診断記録ポリシー](../guidelines/error-diagnostic-recording-policy.md): エラー診断の共通原則とケース別手順への索引
- [Issue #79](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/79): Implementerの検証証跡引き継ぎ
