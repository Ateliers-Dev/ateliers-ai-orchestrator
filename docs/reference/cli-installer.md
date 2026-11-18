# CLIインストーラー技術仕様

> 対象実装: `scripts/cli-installer/Install-AteliersAiOrchestrator.ps1`、`.github/workflows/release-cli.yml`、CLI `--version` コマンド。Phase 1 の実装状態を記録する。

## 1. 対象と境界

この実装は Windows x64 向けのAteliers AI Orchestrator CLIを、指定されたGitHub Releaseからユーザー単位で配置する。管理者権限、PATH変更、レジストリ登録、サービス登録は行わない。自動更新、複数バージョン切替、MCP本体、タスクのオーケストレーション機能変更、Phase 3のE2E起動確認は対象外。

## 2. パス構造

環境変数 `LOCALAPPDATA` が存在し、絶対パスであることを前提とする。

```text
%LOCALAPPDATA%\Ateliers-Dev\Ai-Orchestrator\
└── bin\
    ├── Ateliers.Ai.Orchestrator.Cli.exe
    ├── その他の.NET自己完結publishファイル
    ├── release-manifest.json
    └── install.json
```

`bin` がインストール物の単位で、MCP等の後続プロセスは常に `bin\Ateliers.Ai.Orchestrator.Cli.exe` を指定する。起動バージョンは同階層のマニフェストとCLI自身の `--version` で確認できる。インストール先はPATHへ追加しない。

実行時データはこのディレクトリへインストーラーから作成・移動しない。現行CLIの `start` / `run` は `--runs` 指定がない場合、カレントディレクトリの `.ateliers-orchestrator\runs` を使う。`run` のライフサイクルログは `--lifecycle-log` の指定先（既定値は同じくカレントディレクトリの `.ateliers-orchestrator\lifecycle.jsonl`）を使う。Slack設定ファイル、Codexホーム、作業領域などは既存CLI引数・契約で指定される。インストーラーはそれらを生成しない。ユーザー設定を将来アプリ既定値として永続化する場合のみ `%APPDATA%\Ateliers-Dev\Ai-Orchestrator` を検討し、LocalLowは使わない。

## 3. Release生成と配布物

`.github/workflows/release-cli.yml` は `v*.*.*` タグのpushで起動する。workflowは.NET 10でソリューションをrestore/buildし、Fake-only Unitテスト（`Category=Unit`）を実行した後、CLIプロジェクトを `win-x64`、self-contained、Releaseでpublishする。プロジェクトの `Version` と `InformationalVersion` に `v` を除いたタグ値を埋め込み、ReleaseソースSHAを記録する。

GitHub Releaseに次の2つの資産を作る。

- `Ateliers.Ai.Orchestrator-win-x64-v<version>.zip`: publishファイルと `release-manifest.json`
- `Ateliers.Ai.Orchestrator-win-x64-v<version>.zip.sha256`: ZIP全体のSHA-256とファイル名

`release-manifest.json` は `version`、`runtimeIdentifier` (`win-x64`)、`commit` を持つ。通常のPRビルドはReleaseを作成しない。タグ作成・Release公開は当該タグのpushにより明示的に行う。

## 4. インストールと更新

PowerShell 5.1以降で、バージョンを明示して実行する。

```powershell
& .\scripts\cli-installer\Install-AteliersAiOrchestrator.ps1 -Version 1.2.3
```

`Version` は `1.2.3` または `v1.2.3` 形式（SemVerのmajor.minor.patchと任意のprerelease識別子）を受け付ける。スクリプトは先頭の `v` を除去して `v<version>` タグと固定Release URLを組み立てる。`latest` 検索はしない。該当Release ZIPと`.sha256`を一時フォルダーへダウンロードし、SHA-256を照合する。

展開物のReleaseマニフェスト、Windows x64 RID、実行ファイルの存在を確認し、ZIP内のCLIを `--version` で実行して要求バージョンと一致することを確認する。その後 `install.json` を作成する。既存 `bin` があれば同じ親ディレクトリ内の一時名へ移し、検査済みstageを `bin` に改名する。切替後にもファイル・マニフェスト・実行バージョンを再確認する。確認に失敗すると新しい `bin` を退避し、直前の `bin` があれば復元する。ZIP展開や事前検査に失敗した場合、既存 `bin` は切替前のまま残る。

同一バージョンの再実行も許可する。インストーラーはstageとダウンロード用一時フォルダーを通常終了時・失敗時に削除する。既存バックアップの削除に失敗した場合は操作を失敗として報告し得るため、`bin` と `.backup-*` の状態を確認する。

`install.json` は `version`、`runtimeIdentifier`、`releaseTag`、`releaseUrl`、`installedAtUtc` をUTF-8（BOMなし）で記録する。これはインストール操作の記録であり署名ではない。既存ファイルの読込側はPowerShell 5.1が生成したUTF-8 BOMも受け付ける。

## 5. 配置・バージョン確認

```powershell
& .\scripts\cli-installer\Install-AteliersAiOrchestrator.ps1 -Verify
```

`-Verify` はインストール先の実行ファイル、両JSONマニフェスト、記録バージョンの一致、実行ファイルの起動、CLI表示バージョンとの一致を確認する。成功時は `配置先:` と `バージョン:` を表示し、終了コード0を返す。欠損・破損・実行失敗・バージョン不一致はエラーを標準エラーへ表示し、終了コード1を返す。

CLIの `--version` と `version` は、現在の実行アセンブリの `AssemblyInformationalVersion` を1行で表示して終了コード0を返す。Release workflowがタグ由来の値を設定する。ソースから通常ビルドした場合はSDK既定のInformationalVersion（ソース管理情報が付くことがある）を表示し、Release版の番号を意味しない。

## 6. 失敗時の挙動と真正性

失敗理由にはLOCALAPPDATAの不備、ネットワーク/HTTP失敗、配布物・チェックサム取得失敗、SHA-256不一致、ZIP形式/内容/RID/バージョン不一致、CLI起動失敗、ファイル配置・アクセス失敗が含まれる。どの場合も成功として終了せず、非ゼロコードで失敗メッセージを出す。既存インストールの切替前後の失敗は上記の退避・復元処理を通る。

HTTPSでGitHub Releaseから取得し、同じReleaseに添付したSHA-256で転送・破損を確認する。チェックサムとZIPは同じ配布元・Release権限で管理されるため、チェックサム照合は署名・独立した真正性証明ではない。現在、署名、署名者鍵、GitHub Artifact Attestationの検証は実装していない。

## 7. 後続STDIO MCPとの連携境界

Phase 1が提供する安定境界は固定インストール先の実行ファイルパスと `install.json` / `release-manifest.json` / `--version` である。後続MCPはこのパスからCLIを起動し、未配置・破損・起動失敗を識別できる。Phase 1ではMCPコード、設定登録、STDIO通信、契約の受け渡し、プロセス終了・出力のMCP応答への変換を実装していない。Phase 2はRelease CLIへ後方互換の `--capabilities` と `run --stdin-json` を追加し、従来のファイル入力コマンドを維持する。MCPは `install.json` のインストール版と `--capabilities` のプロトコル版を実行ごとに確認する。

CLIとMCPは個別にRelease/バージョン管理される。CLIの固定パスと既存契約インターフェースが保たれる限り、CLI更新はインストーラーの明示実行だけで行え、MCPの再ビルド・再インストールを要求しない。Capability handshakeにより未対応の古いCLIは明示的な互換性エラーとして扱う。現在インストールされているv0.0.1はPhase 2の新stdin protocolを含まないため、Phase 2対応CLI Releaseを別途発行・配置するまでMCP実行は拒否される。

## 8. 検証範囲と未実施事項

UnitTestsとローカルのPowerShell構文・確認系検証はPhase 1の実装検証対象である。GitHub Actions上でタグからReleaseを実際に作ること、GitHub Release資産をネットワーク越しにインストールすること、非管理者の別Windows環境で検証することはタグ公開を伴うため、この変更の通常テストでは行わない。Phase 3の既定E2Eスクリプト起動確認も対象外である。PR報告では実測結果、未確認事項、失敗を区別する。
