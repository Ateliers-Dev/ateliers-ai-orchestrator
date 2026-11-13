# Ateliers AI オーケストレーター

.NET 10 のローカルCLIです。バージョン付きJSON契約の検証、状態遷移モデル、JSONL実行記録に加え、Fake AgentまたはCodex CLI子プロセスによるワークフローを実行できます。GitHubへの書込とmergeは行いません。


## 今回のPoC完了地点

今回のPoCの完了地点は、**Codex Implementer → Codex Reviewer → 結果集約 → 停止**です。実行結果とレビュー結果を記録した後、人間の判断待ちで止まります。

Codex経路はImplementerを最大1回、独立Reviewerを最大1回だけ起動し、成果物SHA-256、応答、停止理由を記録して `NeedsHumanInput` で停止します。Implementer/Reviewerの失敗、レビュー指摘、レビュー合格のいずれの場合も、自動修正・再レビュー・再試行やPR作成へ進みません。Fake経路の既存反復シナリオは維持します。

実Codex CLIによる受入は未実施であり、実証済み・運用可能とはみなしません。通常のビルドとUnit/Fakeテストは実Codex CLIやモデル通信を起動しません。実Codex IntegrationTestsは、`ATELIERS_RUN_CODEX_INTEGRATION_TESTS=1` とCodex CLIの絶対パスが指定された場合だけ起動します。

オーケストレーターは、Agentの実行条件と制御を保証する。Agentの判断結果および実行環境に残るリスクは、利用者の責任範囲とする。

ここでいう実行条件と制御には、契約で指定した作業領域と操作範囲をAgentへ伝えること、実行時のsandboxモード設定、ImplementerとReviewerを各1回までに制限すること、時間上限を適用すること、終了後は人間判断待ちで停止することを含みます。Agentへの指示やworkspace sandboxは、OSレベルで指定範囲外へのアクセスが完全に不可能であることを保証しません。ローカル実行に残るリスクは利用者が受容する前提です。

### 後続で必須の安全機能・実行条件

以下は現在のPoC完了条件ではなく、後続で維持する機能です。

- APIキー方式の認証（Issue #6）
- 金額上限の理論設計・Fake/モック検証（Issue #7）。実課金、自動チャージ、実際の上限到達による検証は対象外です。

実行方式はローカルディスク上でのCodex CLI直接実行を標準とし、DockerやVM、専用Windowsユーザーを利用者へ必須とはしません。Docker・VM・専用Windowsユーザーによる隔離は任意の将来方式です（専用WindowsユーザーはIssue #5で扱います）。実Codex CLI起動、ChatGPT認証、実モデル呼び出し、課金・利用枠消費は、このPoC変更の承認範囲外です。

### Phase 1: Dockerから独立した開発・検証

Phase 1のCore、CLI、通常テストはDockerプロジェクトに依存しません。標準ソリューションにはCore、CLI、Fakeとプロセス偽物を使う通常テストだけを含め、テストから外部実行基盤の起動や可用性確認を行いません。Docker実装は将来Phase 2で再検討する任意拡張としてソースに残しますが、Phase 1のビルド、テスト、完了判定には含めません。

## 必要環境

- .NET SDK 10

追加NuGetパッケージはありません。

## 使い方

契約を検証します。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- validate examples/task-contract.json
```

契約を検証して実行準備記録を作成します。`workingDirectory` は既存の絶対パスを指定してください。Agentは起動しません。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- start examples/task-contract.json --runs ./.ateliers-orchestrator/runs
```

出力先に実行IDのディレクトリを新規作成し、`events.jsonl` と `report.md` を保存します。既存ディレクトリや既存イベントを上書きしません。契約本文は実行記録にコピーせず、SHA-256だけをレポートに記録します。

Fake Agentの縦断フローを実行します。シナリオは `Success`、`RevisionThenSuccess`、`Stop`、`ImplementerFailure`、`ReviewerFailure`、`EmptyResponse`、`ReviewerEmptyResponse`、`Timeout`、`ReviewerTimeout` から選べます。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- run examples/task-contract.json --scenario RevisionThenSuccess --runs ./.ateliers-orchestrator/runs
```

ライフサイクルJSONLの保存先は `--lifecycle-log` で指定できます。未指定時は実行時のカレントディレクトリに `.ateliers-orchestrator/lifecycle.jsonl` を使います。実行フォルダや一時fixtureの外に置くパスを指定すると、fixture削除後も履歴を保持できます。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- run examples/task-contract.json `
  --scenario RevisionThenSuccess `
  --runs ./.ateliers-orchestrator/runs `
  --lifecycle-log C:/ateliers-data/lifecycle.jsonl
```

このファイルは複数runのイベントをUTF-8 JSONL（1行1JSON）で追記します。各行に `runId`、UTCの`timestampUtc`、`role`、`eventType`、`status`、ワークフロー`state`、必要な試行IDと固定の短い要約を記録します。Orchestrator・Implementer・Reviewerの開始、完了、失敗と、人間判断待ちを記録します。プロンプト、応答本文、環境変数、Token、API応答本文は含めず、要約もコード内の固定文だけを使います。書込みに失敗した場合は警告を出し、元のワークフロー結果を変更しません。

実Codex CLIは明示opt-inされたIntegrationTestsだけが起動します。テストを実行するとImplementerとReviewerを各1回起動し、モデル利用枠や費用が発生する場合があります。Slack送信は別のopt-inと専用テストチャンネル設定を両方必要とします。

実行対象は、契約の `workingDirectory` に指定した既存の開発checkoutを含むローカルディレクトリです。開始前に作業ツリーがcleanであることを確認します。変更を破棄してよいコピーの利用は推奨しますが必須ではありません。Implementerには `workspace-write` を適用します。Reviewerには固定差分を入力として渡し、書込可能な作業ディレクトリをレビュー対象のcheckoutから分離します。レビュー対象のソースコード・設計書は変更せず、必要な任意成果物は実行記録配下へ保存します。契約の `scope` と操作範囲はAgentへの指示です。ローカル実行では、その範囲を超えるアクセスをOSが完全に封じるものではありません。

CODEX_HOMEは `CODEX_HOME` 環境変数、または通常のユーザープロファイル（`~/.codex`）を既定で使います。既存の認証情報、設定、履歴、ログを利用し、Codex CLIが更新する場合があります。ImplementerとReviewerに同じCODEX_HOMEを使います。専用CODEX_HOMEを分ける場合は、任意の追加引数で指定できます。使い捨てCODEX_HOMEや認証情報のコピーは必要ありません。

通常のCODEX_HOMEを使うと、そのユーザーのCodex設定・認証状態を両方の呼び出しで共有します。設定や認証情報へのアクセスを分離したい場合は、専用のCODEX_HOMEを指定できますが、PoCの実行条件ではありません。

```powershell
dotnet run --project src/Ateliers.Ai.Orchestrator.Cli -- run examples/task-contract.json `
  --agent codex `
  --codex-exe "C:/path/to/codex.exe" `
  --runs ./.ateliers-orchestrator/runs
```

Implementerは契約の作業領域で `workspace-write`、Reviewerは固定成果物を入力として受け取り、実際のcheckoutから分離した `exchanges/reviewer-N/workspace` にある専用Git作業領域から実行します。この作業領域の `artifacts` サブディレクトリが任意成果物の保存先です。実行前に固定成果物のSHA-256を照合し、Reviewerへ渡す内容が固定時のバイト列と一致することを確認します。Reviewer起動時はCodex CLIの `--config sandbox_workspace_write.writable_roots=[]` で、既存のユーザー・プロジェクト設定による追加書込ルートを無効にします。Reviewerにはレビュー対象のソースコード・設計書を変更しないよう指示し、必要な成果物を任意のファイル名・形式で保存できます。生成ファイルは `artifactReferences` として実行イベントに記録されます。Reviewerへ直接渡すのは契約と固定差分で、元のcheckoutは複製しません。契約にはcheckoutのパスが含まれるため、Reviewerがその中のファイルを読み取れるかはCodex sandboxの実効権限に従います。workspace-writeはOSレベルの完全な隔離ではなく、範囲外の読み書きを完全に防ぐ保証ではありません。Agentの処理状態と成果物参照は分けて扱い、成果物の種類やファイル形式は定めません。Codex CLIを各役割につき1回だけ起動し、失敗時の再試行、自動修正、自動マージは行わず、人間判断待ちで停止します。子プロセスには許可した環境変数だけを渡し、APIキー等の認証環境変数は渡しませんが、HTTP(S)プロキシ変数は接続に必要なため引き継ぎます。既知のHTTP(S)・ALL_PROXY URLとuser-info内の認証情報は、CLIの標準出力・標準エラーを実行記録へ保存する前にマスキングします。これは既知の値と認証部分に限る保護であり、未知の秘密値、CLI側で変形・分割された断片、別形式で出力された値の完全な秘匿は保証しません。生ログと応答はこの範囲外の秘密情報を含む可能性があるため、実行ディレクトリのアクセスと保持を管理してください。`--config approval_policy="never"` を指定するため、非対話run中に承認待ち画面は出ません。Codexのsandboxモード設定は行いますが、OS全体の隔離境界としては扱いません。拒否された操作はAgent失敗として記録します。子Agent/再委譲を無効化できることは実証していないため、完全に記録できたとは扱いません。timeoutとCtrl+C停止要求を区別し、kill結果、終了確認、stream切断を分けて保存します。killはCodex turn中断の保証ではありません。Unattended `codex exec` の例は[OpenAI Cookbook](https://developers.openai.com/cookbook/examples/codex/build_iterative_repair_loops_with_codex)を参照しています。

実行ごとにCLI JSONL、stderr、最終応答、process/thread識別子、terminal event、終了コード、取得できたtoken usageを保存します。CLI版情報は追加プロセスを起動せず「未取得」と記録します。stderr警告単独では成功・失敗を判定しません。token数は実費・請求額・硬い予算上限ではありません。生ログと応答は秘密情報を含む可能性があるため、実行ディレクトリのアクセスと保持を管理してください。GitHub push・PR作成・mergeはこのコマンドから行わず、人間が最終PR判断を行います。

Fake Agentは実行ごとに同じ入力へ同じ応答を返します。成果物は版ごとに別ファイルへ保存し、保存後の同じバイト列のSHA-256をReviewer要求とイベントに記録します。役割別の要求・応答、試行数、状態遷移、最終報告は実行ディレクトリへ保存されます。初回Reviewer入力にはImplementerの自己評価を含めません。

## 契約

`examples/task-contract.json` が最小例です。実行前に `workingDirectory` を実在する作業ディレクトリへ書き換えてください。`schemaVersion` は `1` に固定し、未知項目、必須値の欠落、含める範囲と除外範囲の重複、不正または存在しない作業ディレクトリを拒否します。操作範囲はタスク単位で定義でき、許可操作を細かく分割するルールは設けません。

## 状態と実行記録

状態は `Prepared → Implementing → Reviewing → Revising → Implementing ...` の流れと、`ReadyForHumanReview`、`NeedsHumanInput`、`Failed` の終端状態を定義しています。禁止遷移は拒否されます。`ReadyForHumanReview` への遷移には、固定成果物ハッシュと一致する独立レビュー記録、タスクの全受け入れ条件の検証記録、GitHub PR URLと作成操作記録が必要です。3種類の記録ファイルを読み、種別と内容を照合します。Fakeモードは実PRや受け入れ結果を作らないため、証跡が別途与えられない限り `NeedsHumanInput` で停止します。Agentの自己申告だけで遷移する経路はありません。人間によるマージ状態はモデルに含まず、CLIからマージする機能もありません。

`--review-evidence` を使う場合は、`HumanReviewEvidence` のJSONと、それが参照する3記録を指定します。レビュー記録は `evidenceKind=independent-review`、対象成果物・レビュー入力のハッシュ、異なる実装・レビュー担当ID、`selfAssessmentExcluded=true`、`verdict=approve` を含めます。受け入れ記録は `evidenceKind=acceptance-verification`、契約の受け入れ条件一覧ハッシュ、全条件数と合格数を含めます。PR操作記録は `evidenceKind=github-pull-request-created`、実PR URL、操作IDを含めます。CLIは記録内容と相互参照を検査して、参照先と記録ファイルのSHA-256を実行イベントへ残します。タイムアウトシナリオはFake応答による再現です。記録の発行元を暗号学的に認証する仕組みはなく、実GitHub操作を行う機能でもありません。

JSONLには実行ID、連番、UTC時刻、役割、イベント種別、状態、入出力参照、対象コミット／成果物ハッシュ、経過時間、結果、エラー、未取得情報の欄を持たせます。Fake呼び出しの要求・応答は実行ごとの参照ファイルへ保存します。取得していない値は推測せず未取得として明示します。ローカルJSONLは耐改ざん・完全監査の台帳ではありません。

`events.jsonl` は実行ごとの詳細な実行記録、`--lifecycle-log` のJSONLは複数実行にまたがる開始・終了・人間判断待ちの記録です。後者の保存先は実行ディレクトリとは独立して指定でき、同じファイルに追記されます。どちらもローカルファイルであり、耐改ざん・完全監査の台帳ではありません。

## テスト

依存パッケージを使わないUnit/Fakeテストを実行します。Coreの状態・保存処理とCLI/Codex実行境界をFakeで検証し、実モデルや外部実行基盤を起動しません。

```powershell
dotnet run --project tests/Ateliers.Ai.Orchestrator.Tests
```

実Codex連携を一時Gitリポジトリで確認するIntegrationTestsは、xUnitプロジェクトです。通常の `dotnet test`、Visual Studioの全件実行、通常CIでは実Codex連携テストがSkipされ、モデル通信は起動しません。Issue #20のUnit/Fakeテストプロジェクトは今回変更していません。

明示的に実Codexを使う場合は、Windowsのユーザー環境変数に `ATELIERS_RUN_CODEX_INTEGRATION_TESTS=1` と `ATELIERS_CODEX_CLI_PATH=C:\絶対パス\codex.exe` を設定し、認証済みCodex CLIが使える環境でテストを実行してください。Visual Studioを使う場合は、環境変数を設定してからVisual Studioを起動し直し、テストエクスプローラーから個別テストまたは全件を実行します。環境変数が設定されていなければテストはSkipされます。

```text
dotnet test tests/Ateliers.Ai.Orchestrator.IntegrationTests/Ateliers.Ai.Orchestrator.IntegrationTests.csproj --configuration Release
```

opt-in時はImplementerとReviewerを各1回呼び出すため、モデル利用枠や費用が発生する場合があります。テストは一時ディレクトリにGit seed、task contract、実行記録先を作り、成功時に削除します。失敗時はfixtureとrun証跡を保持し、テスト失敗メッセージに保存場所とOrchestratorのstdout/stderrを含めます。`NeedsHumanInput` はCLI標準出力文字列だけに依存せず、永続化された `report.md` と `events.jsonl` の状態遷移・停止イベントで確認します。Implementer/Reviewerの各1回、終了証跡、イベント/report、契約対象ファイルだけの変更、固定差分と最終Git差分、成果物参照先も検証します。原因を確認できていない以前の失敗を成功扱いしません。OSレベルの完全隔離を証明するテストではありません。

ソリューション全体のビルドは次の通りです。

```powershell
dotnet build Ateliers.Ai.Orchestrator.sln
```

### 実行ライフサイクルのSlack通知

通常の `run` は、`config/slack.json` と `config/slack.secrets.json` がリポジトリ内にあり、3役すべてのBot定義とTokenが有効な場合に限り、ライフサイクルイベントを各Bot定義の `channelId` へ送信します。雛形の通常送信先は `#ateliers-ai-notifications` です。`slack.json` がない環境は通知無効として正常に実行します。設定ファイルがあるのにsecretsまたはBot設定が不足・不正な場合は、秘密やパスを含めない警告を出して全通知をスキップします。設定探索は実行ディレクトリとCLI出力ディレクトリの親をたどるため、Visual Studioからも環境変数なしで利用できます。明示的に止める場合は `--slack-notifications disabled` を指定します。

送信者をSlackの `from:<@U...>` で検索できるよう、Orchestrator、Implementer、Reviewerに1体ずつ独立したSlackアプリとBotを使います。3体とも `chat:write` のみを付与し、通常は `#ateliers-ai-notifications` に招待してください。各BotのUser IDと通常チャンネルIDは `config/slack.json`、各Bot TokenはGit除外対象の `config/slack.secrets.json` に記録します。雛形は `config/slack.json.example` と `config/slack.secrets.example.json` にあります。

1. Slack APIの[アプリ作成画面](https://api.slack.com/apps)から Orchestrator、Implementer、Reviewer用のアプリを3つ作り、それぞれBot Userを追加します。
2. 各アプリの **OAuth & Permissions** でBot Token Scope `chat:write` を設定し、ワークスペースへインストールします。
3. 3つのBotを `#ateliers-ai-notifications` に招待し、各BotのBot User ID（`U...`）とチャンネルID（`C...`）をローカルの `config/slack.json` へ設定します。
4. `config/slack.secrets.example.json` を `config/slack.secrets.json` にコピーし、`tokens.orchestrator`、`tokens.implementer`、`tokens.reviewer` に対応するBot User OAuth Tokenを保存します。TokenをチャットやPRへ貼り付けたり、Gitへ追加したりしないでください。

投稿者ごとの確認には、Slack検索欄で `from:<@U...> in:#ateliers-ai-notifications` を使います。`U...` を `slack.json` に登録した各Bot User IDへ置き換えると、Orchestrator、Implementer、Reviewerの投稿を個別に検索できます。

ワークフローの開始・完了・失敗・人間判断待ちと、Implementer/Reviewerの開始・完了・失敗を対応するBotから通知します。本文はrun ID、役割、イベント、状態、UTC時刻だけです。prompt、応答、任意のsummary、Token、設定値は含めません。イベント記録時は通知をメモリキューへ積むだけで、Slackの送信待ちは実行時間上限やキャンセル応答に含めません。ワークフロー終了後に最大2秒だけ通知を排出し、キャンセル時は待たずに終了します。Slack送信はイベントごとに1回で、再試行しません。失敗・タイムアウトは安全な警告だけを出し、実行結果とJSONL記録を変えません。JSONL追記もSlack送信もそれぞれ独立して失敗を処理します。

この連携は既存の `Ateliers.Slack` Incoming Webhook APIと `ATELIERS_SLACK_WEBHOOK_URL` 設定を変更しません。Bot Tokenはソース・PR・ビルド成果物へ含めません。

### Slack IntegrationTests（テストチャンネル・明示opt-in）

`Ateliers.Slack` は既存のIncoming Webhookと並行して、Slack Web API `chat.postMessage` を使うBot送信を提供します。Bot定義は `config/slack.json.example`、Token例は `config/slack.secrets.example.json` を参照してください。ローカルで使う場合は例を `config/slack.json` と `config/slack.secrets.json` にコピーし、実際のBot情報を設定します。両方の実ファイルは `.gitignore` 対象です。秘密Tokenはテスト出力へコピーせず、配布用NuGetにも含めません。設定ロードAPIは引き続き絶対パスを受け取ります。

Slack側の準備:

1. `#ateliers-test-notifications` のチャンネルID（`C...`）を取得し、`config/slack.json` のルートにある `testChannelId` へ設定します。この値が実Slackテストの唯一の送信先です。3役それぞれの通常 `channelId` と違う値にしてください。
2. Orchestrator、Implementer、Reviewerの3つのBotを `#ateliers-test-notifications` に招待してください。各Botの `botUserId` は別々の値を維持し、TokenはGit除外対象の `config/slack.secrets.json` に置きます。テストは通常の `channelId` を使いません。
3. 単体のSlack Bot疎通テスト用には、`Ateliers Test Bot` を作成し、Bot Token Scope `chat:write` を付けてワークスペースへインストールします。Botを同じテストチャンネルへ招待し、`tokens.test-bot` と `bots` 内の `test-bot` 設定もローカルに用意します。
4. TokenをチャットやPRへ貼り付けたり、Gitへ追加したりしないでください。テスト結果ではToken、設定内容、Slack応答本文を表示しません。

Slack Bot IntegrationTestsは、テストアセンブリの場所からリポジトリルートを特定し、`config/slack.json` と `config/slack.secrets.json` を読み込みます。`ATELIERS_RUN_SLACK_INTEGRATION_TESTS=1` がなければSkipします。Botテストは `test-bot` のTokenで `testChannelId` に1件投稿します。Webhookテストも同じopt-inに加え、`ATELIERS_SLACK_TEST_CHANNEL_NAME=#ateliers-test-notifications` の明示確認がなければSkipします。Webhook URLはこのテストチャンネル専用にしてください。

実Codexと3役のBot通知をまとめて検証する場合は、両方のopt-inを指定します。CodexテストがImplementer→Reviewerを1回ずつ呼び出し、開始・終了・判断待ちイベントを実行記録に保存します。3役すべての設定・Tokenが読め、testChannelIdが通常送信先と異なる場合だけ、Slack通知を `#ateliers-test-notifications` へ送ります。通常の `ATELIERS_RUN_CODEX_INTEGRATION_TESTS=1` だけならSlack通知は無効です。

```text
dotnet test tests/Ateliers.Slack.IntegrationTests/Ateliers.Slack.IntegrationTests.csproj --configuration Release --filter FullyQualifiedName~SlackBotClientIntegrationTests
```

実Codex＋ライフサイクルSlack E2Eは、認証済みCodex CLIを使って一つのImplementer→Reviewerワークフローを実行し、Slackへ最大6件を送ります。PowerShellでは、準備済みの `codex.ps1` の絶対パスを使い、プロセス限定でopt-inして起動できます。

```powershell
$env:ATELIERS_RUN_CODEX_INTEGRATION_TESTS = '1'
$env:ATELIERS_CODEX_CLI_PATH = 'C:\Users\<user>\AppData\Roaming\npm\codex.ps1'
$env:ATELIERS_RUN_SLACK_INTEGRATION_TESTS = '1'
dotnet test tests/Ateliers.Ai.Orchestrator.IntegrationTests/Ateliers.Ai.Orchestrator.IntegrationTests.csproj --configuration Release --filter FullyQualifiedName~RunAsync_ShouldCompleteImplementerReviewerAndPersistNeedsHumanInput
```

Codex opt-inだけの場合はSlack送信を無効にします。Slack opt-inがあっても、設定ファイル不足、testChannelId不足、3役のテスト送信先の不一致、通常チャンネルとの一致、Bot User ID重複があればSlack部分をSkipし、通常チャンネルへフォールバックしません。成功時のCLI出力に送信成功件数が表示され、3役の投稿はSlack検索で個別に確認できます。

ファイルや設定値が不足・不正な場合は外部通信せずSkipします。空でない誤TokenはSlack APIへ送信を試みた後に認証エラーで失敗します。成功時はテストチャンネルにRun ID付きメッセージを投稿し、Slack Web APIの成功応答と投稿先IDを検証します。API応答からTokenに対応するBot User IDは検証しません。投稿後、Slackの検索欄で `from:<@U...> in:#ateliers-test-notifications "Slack Web API integration test"`（`U...` は設定したBot User ID）を検索し、実際の投稿者を手動で確認してください。ライフサイクルE2Eでも、Orchestrator/Implementer/Reviewerそれぞれの `botUserId` を使って `from:<@U...> in:#ateliers-test-notifications "[Ateliers AI]"` を確認します。Slackは `from:<@UserID>` 形式をサポートしています。[検索方法](https://api.slack.com/methods/search.messages)

このBotテストは送信・識別の疎通確認だけを対象とし、AIオーケストレーション連携は行いません。既存のIncoming Webhook、`ATELIERS_SLACK_WEBHOOK_URL` と `ATELIERS_SLACK_TEST_WEBHOOK_URL` も維持します。
