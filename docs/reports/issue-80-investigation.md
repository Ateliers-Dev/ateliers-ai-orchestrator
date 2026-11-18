# Issue #80 push前チェックの追跡調査

> 作成日: 2026-11-19
> 更新日: 2026-11-19
> 作成者: Codex

> 調査日: 2026-11-19
> 対象: [Issue #80](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80) のRun `7d7e0e60ff494ff4994e2b8f2d4e75f2`

## 結論

このRunで失敗した直接の処理分類は、秘密検出ではなく `git-nonzero-exit` である。どのGitコマンドが終了コード2を返したかは確定できない。保存されていたRun記録には出力の内容ではなく長さだけがあり、Implementerの変更を使って別index経路を再現すると両Gitコマンドは成功した。従って、末尾空白が直接原因だったという仮説は支持されず、snapshot用indexまたは当時の実行条件に未確認差分が残る。原因を確定する修正は行っていないため、Issue #80は受入条件未達としてOpenのままにする。

## 確認できた事実

- Issueの記録では、Implementerはexit 0で修正を終えた後、push前チェック段階でexit 2となり、Reviewer・push・PR作成には到達しなかった。GitHubへの書き込みはなかった。
- ローカル保存されていた元Runの `report.md` と `events.jsonl` を確認した。失敗記録は `reasonCode=git-nonzero-exit`、`exceptionType=ProcessExitNonZero`、`exitCode=2`、`stdoutPresent=true` / `stdoutLength=885`、`stderrPresent=false` / `stderrLength=0`。stdout本文とsnapshot用indexは保存されていない。
- 現行の `GitHubWorkflowPublisher` は、このstageで秘密パターン検査と `git diff --cached --check` を行う。
- 秘密パターン検出は検証例外として処理され、Git子プロセスの終了コードは持たない。`git diff --cached --check` の非0終了は、Gitコマンド失敗として構造化診断に記録される。
- snapshot用Git indexは作業ツリーの `.git/index` と別の一時ファイルで作り、`GIT_INDEX_FILE` で指定する。通常のsnapshot構築・whitespace検査は `.git/index.lock` を使わない。
- 対象Runがpush前チェック段階に到達した記録は、開始時の `clean-checkout` 判定を通過していたことを示す。
- Implementer応答と保存されたTestbed checkoutから、変更は `src/WordCounter/TextMetrics.cs` の1ファイルと確認できた。実際の変更内容に対する通常の `git diff --check` は終了コード0だった。
- Run記録は `work/orchestration-runs/<runId>/` にあり、Testbed checkout `work/orchestrator-testbed-smoke/` の外側に保存されていた。今回のRunログがcloneを汚した証拠はない。

## 再現試行と仮説

最初に、Windows Git 2.44.0上の一時リポジトリで末尾空白を含む差分をステージし、`git diff --cached --check` が終了コード2を返すことを確認した。これは空白違反時のコマンド挙動を示す対照確認だが、Issue対象Runの変更内容を再現するものではない。

次に、保存されたTestbed checkoutの元のbase SHA `0ae85f8330b1c696cdf55a1f03880cd02966c488` とImplementerの実差分を使い、別ファイルに置いたsnapshot用indexで現行Publisherと同じ2つのGit diffコマンドを実行した。`git diff --cached --binary --no-ext-diff <base> --` は終了コード0・stdout 617文字、`git diff --cached --check <base> --` は終了コード0・出力なしだった。

従って、保存されたImplementer差分にwhitespace errorがあったとは確認できず、末尾空白仮説は支持されない。一方、当時のstdout本文、snapshot index、Git設定・環境は記録にない。現Runの異なる挙動を再現できておらず、失敗したsubcommandは未特定のままである。

## 指定された環境要因の切り分け

| 要因 | 確認結果 | 残る不確実性 |
|---|---|---|
| .NET環境 | 最初のbuildは隔離された `DOTNET_CLI_HOME` 配下に `.dotnet` を作れず `UnauthorizedAccessException`。`tmp/dotnet-home` へ移した後も、`NUGET_PACKAGES` と空のrestore sourceを指定したbuildがNuGet `Value cannot be null. (Parameter 'path1')` で失敗した。 | どちらもpush前stageに達する前のagent側の失敗で、Git diff終了コード2との直接因果は確認できない。dotnetによる生成差分の影響はsnapshot不在で不明。 |
| `.git/index.lock` | agentの `git switch codex-e2e-smoke-20261118` が `.git/index.lock` 作成時にPermission deniedで失敗した。これはbranch switch時の失敗であり、Publisherの別indexを使うsnapshot diffとは別の操作。 | 同じsandbox上でPublisherの別index diffがどのsubcommandで失敗したかは未確定。 |
| sandbox | Codex実行ユーザーは `CodexSandboxOffline`、clone所有者は `yuuko`。Gitは最初にdubious ownershipを報告し、agentはコマンド単位の `safe.directory` でstatus等を読み取った。`.git/index.lock`作成拒否も記録されている。 | sandbox境界がsnapshot indexの作成・読み取り、またはstdout 885文字の内容に与えた影響は特定できない。 |
| clean状態 | `safe.directory` を付けて取得した初期statusはcleanで、HEADと `main` はどちらもbase SHAだった。対象Runがpush前stageに到達した記録もclean-checkoutを通過した証拠。 | stage時点のsnapshot indexの中身までは確認できない。 |
| ログ配置 | 元Runの `report.md` / `events.jsonl` は `work/orchestration-runs/<runId>/`、Testbed checkoutは `work/orchestrator-testbed-smoke/` にあり、clone外配置を確認。別Runでclone内ログによりclean判定失敗した記録とは別の事象。 | Git publisherのstdout本文は秘密情報保護のため保存されておらず、直接subcommandの特定に不足している。 |

## 判断と残リスク

- 直接原因は特定できず、原因特定を要件とするIssue #80は完了していない。
- 実差分を使った別index再現では双方のdiffコマンドが成功した。元stdout本文とsnapshot indexがないため、追加E2Eを実行しても当時の失敗コマンドは特定できず、原因を確定する修正候補もないことから、追加の実Codex E2Eは行わなかった。
- 原因を特定した修正がないため、PR必須Runで修正後の通過確認も行っていない。
- 残リスクは、当時のstdout本文、snapshot index、Git設定が欠落し、2つのGit diffコマンドのどちらが失敗したか検証できないこと。
- 調査はここで終了する。Issue #80は受入未達を明示してOpenのままとし、追加証跡が得られる場合は調査を再開する。
