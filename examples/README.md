# examples

> 作成日: 2026-11-18
> 更新日: 2026-11-18
> 作成者: yuu503

このフォルダには、CLI で使う TaskContract v1 のサンプル契約があります。

- `task-contract.json` は、作業範囲、入力、要件、受け入れ条件、制限などの基本的な書き方を示す最小例です。ルート README の `validate`、`start`、`run` のコマンド例や契約ガイドからも参照されています。

実行用に使うときは、`workingDirectory` を実在する作業ディレクトリの完全修飾パスへ変更し、タスクに合わせて入力、範囲、要件、受け入れ条件を記入してください。実行前に CLI の `validate` コマンドで契約を確認します。使い方と制約は[ルート README](../README.md)と[オーケストレーション契約ガイド](../src/Ateliers.Ai.Orchestrator.Core/OrchestrationGuide.md)を参照してください。
