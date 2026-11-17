# E2E実行基盤

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

実行前に本番Slack設定を読み、通知先チャンネルIDを表示します。`config/slack.json` と `config/slack.secrets.json` が既定の場所にない場合は、両パスを `-SlackConfigurationPath` と `-SlackSecretsPath` で明示します。秘密ファイルの値は出力しません。設定がなければ通知を無効化せず停止します。確認文にはTestbedと本番通知先のチャンネルIDが含まれます。

準備中に `verify-slack` を使って3つの本番Botの `auth.test` と設定済みBot IDを照合します。この確認は通知を投稿しません。さらにTestbed `main` のclean cloneで課題プロジェクトをRelease buildし、既知の `tabs and newlines` と `whitespace only` だけが失敗することをCodex起動前に確認します。基準状態が異なる場合は停止し、cloneと診断ログを保持します。

現在のTestbedシナリオでは次の形です。

    RUN CODEX E2E FOR ATELIERS-DEV/ATELIERS-AI-ORCHESTRATOR-TESTBED WITH PUSH AND PR AND SLACK TO <本番チャンネルID>

実行時は `auto` で設定済みの本番通知先に送ります。Slack送信成功件数がない場合や送信失敗がある場合は、PR等の証跡があっても不合格です。E2E後にSlackでrun IDを含む通知を確認し、表示された確認文を入力します。到達確認を行えない場合は不合格として証跡を保持します。

実行は一意な一時cloneと専用作業ブランチを使います。PR作成、Reviewer結果コメント、head/base SHA、GitHub上のPRがOPENでbaseがmainであること、Slack送信と人間による到達確認をすべて確認しない限り、合格を表示しません。自動マージはしません。PRは人間の確認後も開いたままにし、Testbed `main` へマージしません。

Codex開始後の失敗、中断、証跡不一致では一時cloneと実行記録を保持します。GitHub上にpush済みブランチやPRが残った可能性がある場合、人間が状態を確認してください。正常終了した場合も記録は自動削除しません。

Codex子プロセスには限定した環境変数だけを渡し、ユーザープロファイル・一時領域・.NET CLI homeを実行ごと・役割ごとに分離します。親のAPI秘密環境変数は引き継ぎません。`CODEX_HOME` は明示指定がなければ親環境の値（未設定ならユーザー既定）を使うため、開始前に表示される認証プロファイルを確認してください。Codex認証ファイルは自動複製しません。E2EではGitHubから取得したfreshなclean cloneを使用し、利用者のcheckout内の `clean` 状態やファイルを変更しません。

## 外部コマンドとPowerShell

Git、dotnet、gh、CLIは終了コードで成否を判定します。ネイティブコマンドの標準エラーは診断出力として扱い、Windows PowerShell 5.1の `$ErrorActionPreference = 'Stop'` により終了コード確認前に停止しないよう、呼び出し単位でネイティブ出力を捕捉します。機械処理に使うブランチ名やSHAの戻り値には標準出力だけを返します。
