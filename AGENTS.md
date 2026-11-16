# AGENTS.md

このファイルは本リポジトリ固有の作業指示です。汎用的な作業手順は、適用される Skill に従ってください。

## 言語と表記

- コミットメッセージは、原則として日本語で記述する。
- Pull Request のタイトルと本文は日本語で記述する。
- Issue のタイトルと本文は、原則として日本語で記述する。
- コード識別子、API 名、ファイル名は翻訳しない。
- 外部プロジェクトで作業するときは、そのプロジェクトの規約を優先する。

## 作業成果の共有

- 原則として、作業エージェントは作業後に変更を push し、新しい作業では Pull Request（PR）を作成する。既存 PR に対する修正では新規 PR を作成せず、その PR のブランチを更新する。
- PR のマージは行わない。
- push または PR 作成に失敗した場合は、その事実と理由を指示者へ報告する。指示者は人間に限らず、AI やオーケストレーターの場合もある。
- この方針は、ローカルだけの変更では独立したレビュアーが内容を確認できない場合があるため、PR 上でコメントやレビューを行える状態にすることを目的とする。
- この方針は適用される上位指示、権限、承認要件、安全制約に優先しない。それらに反する push や PR 作成を許可するものではない。

## テスト

- システムに影響しない文書のみの修正では、テストは実施しなくてよい。
- テストの対象プロジェクト、起動方法、事前の restore/build は `.github/workflows/` の該当する GitHub Actions 定義を確認し、それに合わせる。

### Fake-only Unit tests（`--unit`）

- CI と同じテストをローカルで実行する場合は、まずリポジトリルートで次の restore と Release build を行う。

  ```powershell
  dotnet restore Ateliers.Ai.Orchestrator.sln
  dotnet build Ateliers.Ai.Orchestrator.sln --configuration Release --no-restore
  ```

- Fake-only Unit tests の標準起動コマンドは次のとおり。対象プロジェクトは `tests/Ateliers.Ai.Orchestrator.Tests/Ateliers.Ai.Orchestrator.Tests.csproj`。

  ```powershell
  dotnet run --project tests/Ateliers.Ai.Orchestrator.Tests/Ateliers.Ai.Orchestrator.Tests.csproj --configuration Release --no-build --no-restore -- --unit
  ```

- この起動方法は `.github/workflows/unit-tests.yml` の Windows・Ubuntu 両方の CI と一致する。Windows CI は同じ `dotnet run` を PowerShell から実行し、Unix mode に依存する所定のスキップ2件だけを許容して、その他の失敗がないことを確認する。
- `dotnet <Tests.dll> --unit` によるテスト DLL の直接起動は、標準の検証方法にしない。Windows ローカルでは、変更前の main と PR #56 の両方で DLL 直接起動時に同一の16件が失敗し、CI と同じ `dotnet run` では両方とも失敗0件だった。PR #56 の GitHub Actions は Windows・Ubuntu とも成功した。
- `Environment.ProcessPath` と子プロセス fixture の起動方法がこの差に関係する可能性が高いが、原因機序は未確定である。仮説を確定原因として報告しない。
- 標準コマンドで失敗した場合も、環境問題と断定しない。実行コマンド、コミット、OS、.NET SDK/runtime、失敗したテスト、成功・スキップ・失敗件数を記録し、テスト対象と起動条件を CI や変更前の状態と比較して調査する。原因が確認できなければ未確定として指示者に報告する。
- 他のテストプロジェクトやテスト種別には、このコマンドを流用せず、該当する CI 定義を確認する。
