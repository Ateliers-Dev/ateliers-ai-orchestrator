# Issue #80 push前チェックの追跡調査

> 調査日: 2026-11-19
> 対象: [Issue #80](https://github.com/Ateliers-Dev/ateliers-ai-orchestrator/issues/80) のRun `7d7e0e60ff494ff4994e2b8f2d4e75f2`

## 結論

push前チェック `push.secret-scan-and-whitespace-check` の直接原因は未特定である。末尾空白を含む差分で同じGitコマンドが終了コード2を返すことを再現したが、当時の変更snapshotと詳細ログがなく、Issue対象Runの直接原因とは確認できない。原因を確定する修正は行っていないため、Issue #80は受入条件未達としてOpenのままにする。

## 確認できた事実

- Issueの記録では、Implementerはexit 0で修正を終えた後、push前チェック段階でexit 2となり、Reviewer・push・PR作成には到達しなかった。GitHubへの書き込みはなかった。
- 元Runの詳細ログ、Git stderr/stdout、Implementerの変更snapshotは、この調査環境内で見つからなかった。
- 現行の `GitHubWorkflowPublisher` は、このstageで秘密パターン検査と `git diff --cached --check` を行う。
- 秘密パターン検出は検証例外として処理され、Git子プロセスの終了コードは持たない。`git diff --cached --check` の非0終了は、Gitコマンド失敗として構造化診断に記録される。
- snapshot用Git indexは作業ツリーの `.git/index` と別の一時ファイルで作り、`GIT_INDEX_FILE` で指定する。通常のsnapshot構築・whitespace検査は `.git/index.lock` を使わない。
- 対象Runがpush前チェック段階に到達した記録は、開始時の `clean-checkout` 判定を通過していたことを示す。

## 再現試行と仮説

Windows Git 2.44.0上の一時Gitリポジトリで、ステージ済みファイルに末尾空白を含めて `git diff --cached --check` を実行した。Gitは該当ファイル・行を表示し、終了コード2を返した。

この結果から、Issue対象Runの終了コード2がGit子プロセス由来なら、ステージ差分にGitが検出するwhitespace errorがあった可能性が高い。ただし、当時の診断に含まれる `reasonCode`・stdout/stderr有無と長さ、および実差分を確認できないため仮説にとどまる。再現試行はコマンド挙動の確認であり、元Runの再現ではない。

## 指定された環境要因の切り分け

| 要因 | 確認結果 | 残る不確実性 |
|---|---|---|
| .NET環境 | 該当push前stageに.NETコマンドはなく、直接原因とは考えにくい。現行実装はCodex子プロセス用に `DOTNET_CLI_HOME` をrun配下のruntime領域へ分離する。 | .NET restore/build時の生成差分がwhitespace checkへ影響したかは、snapshotがなく不明。 |
| `.git/index.lock` | snapshot indexは別の一時パスを使うため、通常のこのstageでは使わない。 | 併記されたアクセス拒否が別のGitコマンド、同時プロセス、または別Runのものかは原ログなしに判定できない。 |
| sandbox | Publisherのwhitespace検査はGitがstage済みsnapshot indexに対して行う。sandboxは直接の実行コマンドではない。 | sandboxが生成ファイルの内容・改行・空白を変えたかは確認できない。 |
| clean状態 | Issue対象Runはpush前stageまで進んだため、開始時のclean-checkout検査を通過した。 | 開始時のstatus全文とRunログ配置は残っていない。 |
| ログ配置 | E2Eスクリプトは `--runs` で記録を一時実行ルートへ置ける。CLI既定では作業ディレクトリ内 `.ateliers-orchestrator/runs` に置くため、publication checkout内で既定値を使うとclean-checkoutを汚し得る。clone内ログ配置でclean判定に失敗したという記録は別Runである。 | Issue対象Runの実際の `--runs` とログディレクトリは確認できない。 |

## 判断と残リスク

- 直接原因は特定できず、原因特定を要件とするIssue #80は完了していない。
- 同じE2Eを再実行しても、元Runのsnapshot・ログがない状態では、当時の失敗原因を確認できない。今回の追加E2Eは行わなかった。
- 原因を特定した修正がないため、PR必須Runで修正後の通過確認も行っていない。
- 残リスクは、当時の差分、Git診断の出力内容、実ログ配置が欠落し、whitespace error仮説を検証できないこと。
- 調査はここで終了する。Issue #80は受入未達を明示してOpenのままとし、追加証跡が得られる場合は調査を再開する。
