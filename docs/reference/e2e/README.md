# E2E実行基盤

> 作成日: 2026-11-17
> 更新日: 2026-11-20
> 作成者: yuu503

## このフォルダについて

このフォルダには、E2E 実行基盤の共通手順と、個別シナリオの実行条件・確認項目を置きます。新しい種類の文書やシナリオを追加・変更するときは、この README も併せて更新してください。

実Codexを使うTestbedシナリオの準備確認・実行はユーザー本人が行います。開発ワーカーはこのスクリプトを呼び出しません。開発時は [IntegrationTestの実行範囲と手順](../development-integration-tests.md) を参照してください。ここで起動するのはソースからRelease buildしたCLIであり、インストール済みCLIやMCPの導入・接続確認は別の最終受入項目です。

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

準備確認が成功した後、`-RunCodex` を指定します。これは実行開始の明示指定です。スクリプトは対話入力を使わず、すべての前提を検証してからCodexを起動します。CI/CDではこのスイッチを明示したジョブだけが実行します。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1 -RunCodex
~~~

Codex CLIは `-CodexExecutable`、環境変数 `ATELIERS_CODEX_CLI_PATH`、PATH上の `codex.exe` / `codex.ps1` / `codex.cmd` の順で解決します。明示パスは絶対パスにしてください。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1 -RunCodex -CodexExecutable 'C:\Tools\Codex\codex.exe'
~~~

Milestone 1の最終E2EではSlack通知は必須で、`testChannelId` 固定です。`disabled` と `auto` は受け付けません。

通知は `%LOCALAPPDATA%\Ateliers-Dev\Ai-Orchestrator\config\slack.json` の `testChannelId` へ送ります。必要なら `-SlackConfigurationPath` と `-SlackSecretsPath` の両方でローカル設定の場所を指定できます。Bot認証と送信先を実Codex起動前に確認し、Token値は表示・記録しません。

Slack API成功件数が機械可読なCLI結果で1件以上、失敗0件であることを確認します。結果は一時実行領域の `e2e-result.json` に段階、Orchestrator呼び出し状態、Codex子プロセス起動状態、run IDとともに記録されます。子プロセス起動証跡がない場合は未起動または未確認を分けて記録します。Slack画面での受信確認はこのE2Eゲートに含めません。

Slack設定やBot認証が不足している場合は準備段階で停止し、通知を無効化したり通常通知先へ切り替えたりしません。

実行は一意な一時cloneと専用作業ブランチを使います。PR作成、Reviewer結果コメント、head/base SHA、GitHub上のPRがOPENでbaseがmainであることを確認しない限り、合格を表示しません。Slack API送信成功も合格条件です。自動マージはしません。PRは人間の確認後も開いたままにし、Testbed `main` へマージしません。

Codex開始後の失敗、中断、証跡不一致では一時cloneと実行記録を保持します。GitHub上にpush済みブランチやPRが残った可能性がある場合、人間が状態を確認してください。正常終了した場合も記録は自動削除しません。

Codex子プロセスには限定した環境変数だけを渡し、ユーザープロファイル・一時領域・.NET CLI homeを実行ごと・役割ごとに分離します。親のAPI秘密環境変数は引き継ぎません。`CODEX_HOME` は明示指定がなければ親環境の値（未設定ならユーザー既定）を使うため、開始前に表示される認証プロファイルを確認してください。Codex認証ファイルは自動複製しません。E2EではGitHubから取得したfreshなclean cloneを使用し、利用者のcheckout内の `clean` 状態やファイルを変更しません。

## 外部コマンドとPowerShell

Git、dotnet、gh、CLIは終了コードで成否を判定します。ネイティブコマンドの標準エラーは診断出力として扱い、Windows PowerShell 5.1の `$ErrorActionPreference = 'Stop'` により終了コード確認前に停止しないよう、呼び出し単位でネイティブ出力を捕捉します。機械処理に使うブランチ名やSHAの戻り値には標準出力だけを返します。
