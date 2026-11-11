# ローカル直接実行の最小ゲート

## 方針

Codex CLIは利用者のローカルディスク上で直接実行する方式を標準とする。通常の `CODEX_HOME` と既存の開発checkoutを利用できる。Docker、VM、専用ユーザー、使い捨て `CODEX_HOME`、破棄可能なコピーは選択可能な推奨策であり、実行の絶対条件ではない。

通常環境を使う場合、その環境のCodex設定・認証情報・履歴・ログがImplementerとReviewerに共有され、Codex CLIが設定やログを更新する可能性がある。これはローカル直接実行のトレードオフとして利用者が受け入れる。隔離が必要な場合は、専用 `CODEX_HOME`、使い捨てコピー、Docker、VM、専用ユーザーを選択できる。

オーケストレーターは、Agentの実行条件と制御を保証する。Agentの判断結果および実行環境に残るリスクは、利用者の責任範囲とする。

この保証は、指定したCodex sandboxモード、Implementerの `workspace-write`、Reviewerの `read-only`、呼び出し回数、時間上限、停止条件を適用することを指す。契約の作業範囲・許可操作をAgentへ伝えることやCodex workspace sandboxは、OSレベルの完全なアクセス制御を意味しない。OSの実測隔離は完了条件に置かず、残存リスクを明記する。

## 実行ゲート

実Codex CLIを起動する前に、次を満たす。

1. 契約の `workingDirectory` に、既存の開発checkoutを含むローカルディレクトリの絶対パスを指定する。変更を破棄してよいコピーの利用を推奨するが、必須にしない。
2. 実行前に作業ツリーがcleanであることを確認する。未コミット差分や未追跡ファイルがある場合は保持して停止する。
3. 実行契約で目的、対象範囲、除外範囲、許可操作、停止条件、時間上限を明示する。これらはAgentへの指示であり、OS隔離の証明ではない。
4. Implementerには `workspace-write` を適用する。Reviewerには固定差分のみを渡し、`read-only` を適用する。
5. `CODEX_HOME` 環境変数があればそれを使い、なければ通常の `~/.codex` を使う。専用 `CODEX_HOME` を使う場合は任意で指定でき、ImplementerとReviewerで同じプロファイルを使える。
6. ImplementerとReviewerはそれぞれ最大1回だけ起動する。失敗時の再試行、自動修正、自動マージはせず、人間判断待ちで停止する。
7. timeout、停止要求、プロセス終了確認、出力stream切断を区別して記録する。終了状態が不明、証跡が不完全、永続化に失敗した場合は成功とみなさず停止する。
8. 本ゲートと具体的な実行範囲を説明した後、実Codex CLI起動について別途明示承認を得る。`--help`、`--version`、loginもCLI起動に含む。

## Issueとの関係

- [ateliers-workspace Issue #11](https://github.com/Ateliers-Dev/ateliers-workspace/issues/11) は別リポジトリのopen issueで、通常profileとの差分・使い捨てprofile・OS隔離の強い条件を含む。このローカル直接実行PoCではそれらを起動条件にしない。Issue自体の更新や完了扱いはこの変更の範囲外とする。
- Issue #11に記載されたworkspace隔離・パス境界の検証結果は、ホスト全体の隔離保証とは表現しない。既知の検証限界を報告する。
- Issue #4の永続化不整合は限定PoCで条件付き許容とするが、永続化不完全時は成功扱いしない。継続運用やログ分析を開始する前に修正する。
- Issue #5（専用Windowsユーザー隔離）は任意の将来方式として維持し、Docker/VM等との選択肢を今後評価する。
- Issue #6（APIキー認証）とIssue #7（金額上限・予算制御）は後続機能として維持する。Issue #7は理論設計とFake/モック検証が対象で、実課金、自動チャージ、実際の上限到達を検証しない。
- 実Codex CLI、ChatGPT認証、モデル呼び出し、実課金・利用枠消費は、この変更では実行しない。
