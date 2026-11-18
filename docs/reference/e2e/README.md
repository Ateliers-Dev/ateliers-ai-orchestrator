# E2E実行基盤

> 作成日: 2026-11-17
> 更新日: 2026-11-18
> 作成者: yuu503

## このフォルダについて

このフォルダには、E2E 実行基盤の共通手順と、個別シナリオの実行条件・確認項目を置きます。新しい種類の文書やシナリオを追加・変更するときは、この README も併せて更新してください。

`scripts/e2e/run-codex-e2e.ps1` は、シナリオ契約を受け取り、契約の `inputs[].reference` にある単一のGitHubリポジトリを使って準備確認または明示許可後のCodex実行を行います。参照形式はGitHubのblob、issue、commitなどを使えますが、全参照はUserInfo・query・fragmentを含まない `https://github.com/{owner}/{repo}/...` 形式で同じリポジトリを指す必要があります。形式が不明、または複数のリポジトリを指す場合は停止します。共通の起動、終了コード確認、一時領域、公開操作ゲート、証跡確認を担当します。シナリオ固有の作業範囲と受入条件は `scripts/e2e/scenarios/` の契約ファイルに置きます。

現在のシナリオは [Testbedレビューサイクル](testbed-review-cycle.md) です。新しいシナリオを追加するときは、既存CLIが受け付ける契約スキーマに沿ったJSONを追加し、実行基盤の共通動作を重複して定義しないでください。

## 準備確認

Windows PowerShell 5.1以降、.NET SDK 10、Git for Windows、GitHub CLI (`gh`) が必要です。実Codex実行を選ぶ場合はさらに Codex CLI が必要です。GitHub CLI は `github.com` に認証され、対象シナリオのリポジトリへpushしてPRを作成できる必要があります。

リポジトリルートから、引数なしで準備確認を実行します。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1
~~~

別の契約を使う場合は `-ScenarioPath` にJSONファイルを指定します。省略時はTestbedレビューサイクルを使います。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1 -ScenarioPath .\scripts\e2e\scenarios\testbed-review-cycle.json
~~~

準備確認は本体のRelease build、シナリオ対象GitHubリポジトリの認証・既定ブランチSHA・cleanな一時clone、生成した契約の現行CLI検証を行います。単体テストは実行せず、E2Eの前提ゲートにも使いません。対象プロジェクト固有のbuildとベースラインチェックは、各シナリオ手順に従ってください。実Codexを起動しません。既存checkoutのファイルを変更したりstash/resetしたりしません。

## 実Codex実行

準備確認が成功した後、`-RunCodex` を指定します。開始前に公開操作と費用可能性を表示し、確認文の完全一致を要求します。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1 -RunCodex
~~~

Codex CLIをPATHから解決できない環境では、絶対パスを指定できます。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1 -RunCodex -CodexExecutable 'C:\Tools\Codex\codex.exe'
~~~

E2EのSlack通知モードは `-SlackNotifications disabled|test|auto` で明示できます。省略時は `disabled` で、Slack設定を読み込まず、認証通信も送信もしません。通知を検証するときだけ `test` または `auto` を選びます。

`test` は `%LOCALAPPDATA%\Ateliers-Dev\Ai-Orchestrator\config\slack.json` の `testChannelId` へ `[Ateliers AI TEST]` と明示した通知を送り、`auto` は各Botの通常 `channelId` へ送ります。必要なら `-SlackConfigurationPath` と `-SlackSecretsPath` の両方でローカル設定の場所を指定できます。Bot認証と送信先を実Codex起動前に確認し、送信先IDを表示します。Token値は表示・記録しません。通常チャンネルとテストチャンネルは異なる値でなければなりません。

通知モードを明示したE2Eでは、Slack送信成功件数とrun IDの到達確認が必要です。成功を確認できない場合は、PR等の証跡があっても不合格です。確認文には選択したモードと送信先チャンネルIDが含まれます。

例: テスト用チャンネルに送る場合は `-SlackNotifications test`、通常通知先へ送る場合は `-SlackNotifications auto` を実Codex起動前の準備確認後に指定します。実行スクリプトの既定モードは `disabled` です。

実行は一意な一時cloneと専用作業ブランチを使います。PR作成、Reviewer結果コメント、head/base SHA、GitHub上のPRがOPENでbaseがmainであることを確認しない限り、合格を表示しません。`test` または `auto` のときはSlack送信と人間による到達確認も追加の合格条件です。自動マージはしません。PRは人間の確認後も開いたままにし、Testbed `main` へマージしません。

Codex開始後の失敗、中断、証跡不一致では一時cloneと実行記録を保持します。GitHub上にpush済みブランチやPRが残った可能性がある場合、人間が状態を確認してください。正常終了した場合も記録は自動削除しません。

Codex子プロセスには限定した環境変数だけを渡し、ユーザープロファイル・一時領域・.NET CLI homeを実行ごと・役割ごとに分離します。親のAPI秘密環境変数は引き継ぎません。`CODEX_HOME` は明示指定がなければ親環境の値（未設定ならユーザー既定）を使うため、開始前に表示される認証プロファイルを確認してください。Codex認証ファイルは自動複製しません。E2EではGitHubから取得したfreshなclean cloneを使用し、利用者のcheckout内の `clean` 状態やファイルを変更しません。

## 外部コマンドとPowerShell

Git、dotnet、gh、CLIは終了コードで成否を判定します。ネイティブコマンドの標準エラーは診断出力として扱い、Windows PowerShell 5.1の `$ErrorActionPreference = 'Stop'` により終了コード確認前に停止しないよう、呼び出し単位でネイティブ出力を捕捉します。機械処理に使うブランチ名やSHAの戻り値には標準出力だけを返します。
