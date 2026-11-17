# E2E実行基盤

`scripts/e2e/run-codex-e2e.ps1` は、シナリオ契約を受け取り、契約の `inputs[].reference` にある単一のGitHubリポジトリを使って準備確認または明示許可後のCodex実行を行います。共通の起動、終了コード確認、一時領域、公開操作ゲート、証跡確認を担当します。シナリオ固有の作業範囲と受入条件は `scripts/e2e/scenarios/` の契約ファイルに置きます。

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

準備確認は本体のRelease build、Fake-only Unit tests、シナリオ対象GitHubリポジトリの認証・既定ブランチSHA・cleanな一時clone、生成した契約の現行CLI検証を行います。対象プロジェクト固有のbuildとベースラインチェックは、各シナリオ手順に従ってください。実Codexを起動しません。既存checkoutのファイルを変更したりstash/resetしたりしません。

## 実Codex実行

準備確認が成功した後、`-RunCodex` を指定します。開始前に公開操作と費用可能性を表示し、確認文の完全一致を要求します。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1 -RunCodex
~~~

Codex CLIをPATHから解決できない環境では、絶対パスを指定できます。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\e2e\run-codex-e2e.ps1 -RunCodex -CodexExecutable 'C:\Tools\Codex\codex.exe'
~~~

確認文:

    RUN CODEX E2E WITH PUSH AND PR

実行は一意な一時cloneと専用作業ブランチを使います。PR作成、Reviewer結果コメント、head/base SHAの証跡が確認されても、自動マージはしません。シナリオ固有の後片付けや教材ブランチの扱いは各シナリオ手順に従ってください。

Codex開始後の失敗、中断、証跡不一致では一時cloneと実行記録を保持します。GitHub上にpush済みブランチやPRが残った可能性がある場合、人間が状態を確認してください。正常終了した場合も記録は自動削除しません。

## 外部コマンドとPowerShell

Git、dotnet、gh、CLIは終了コードで成否を判定します。ネイティブコマンドの標準エラーは診断出力として扱い、Windows PowerShell 5.1の `$ErrorActionPreference = 'Stop'` により終了コード確認前に停止しないよう、呼び出し単位でネイティブ出力を捕捉します。
