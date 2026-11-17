# Phase 1 CLIインストーラー検証記録

> 状態: 実装ブランチでの検証記録。GitHub Release作成を伴う確認は未実施。

## 実行環境と対象

- Repository: `Ateliers-Dev/ateliers-ai-orchestrator`
- Base: `origin/main` の `664c8970719af5b87d5d3953db5e7abd05b48379`（PR #67 merge commit）
- OS: Windows x64
- SDK: .NET SDK 10.0.403、Runtime 10.0.14
- PowerShell: Windows PowerShell 5.1（`powershell.exe`）およびPowerShell 7（構文パーサー）
- 検証で使用した一時バージョン: `1.2.2`、`1.2.3`。どちらもこの検証用にローカル生成したZIPであり、公開Releaseではない。

## 検証結果

| 項目 | 実行・観測 | 結果 |
|---|---|---|
| restore | `dotnet restore Ateliers.Ai.Orchestrator.sln` | 成功（sandbox内ではWindowsサービスアクセス制限で失敗。許可レビュー後に再実行して成功） |
| Release build | `dotnet build Ateliers.Ai.Orchestrator.sln --configuration Release --no-restore` | 成功、警告0・エラー0 |
| Fake-only Unit tests | `dotnet test tests/Ateliers.Ai.Orchestrator.UnitTests/Ateliers.Ai.Orchestrator.UnitTests.csproj --configuration Release --no-build --no-restore --filter Category=Unit` | 成功71、Skip 2、失敗0、合計73（WindowsでUnix mode依存の既存2件がSkip。新規CLIバージョンテストを含む） |
| Release publishと版番号 | `dotnet publish ... --runtime win-x64 --self-contained true -p:Version=1.2.3 -p:InformationalVersion=1.2.3 -p:IncludeSourceRevisionInInformationalVersion=false` の出力CLIで `--version` | 成功。標準出力 `1.2.3` を確認 |
| Pull Request CI | GitHub Actions run #89（run ID `44101434230`、HEAD `577c352e13f887c54f3d957930912b8016b661bb`） | Windows・Ubuntu両ジョブ成功（restore、Release build、Fake-only Unit、Slack Unit） |
| Windows PowerShell実行と配置確認 | ローカル配布ZIPを使って `-Version 1.2.2`、続けて `-Verify` | 成功。固定パス、実行ファイル、両マニフェスト、CLIの版番号を確認 |
| 明示更新 | 別のローカル配布ZIPで `-Version 1.2.3`、続けて `-Verify` | 成功。確認結果が `1.2.3` へ変わり、通常終了後にstage/backup残留なし |
| Releaseマニフェスト不一致 | `1.2.4` を要求し、別版マニフェスト入りZIPを与える | 非成功を確認。既存 `1.2.3` の配置と版番号は維持 |
| SHA-256不一致 | ZIPと合わないchecksumを与える | 非成功を確認。既存 `1.2.3` の配置と版番号は維持 |
| CLI実体の版番号不一致 | `install.json` とRelease情報を食い違わせて `-Verify` | Windows PowerShell 5.1から終了コード1と具体的なエラーを確認 |

実際のダウンロード手順を呼び出すスクリプトは変更せず、ローカルZIPを返すテスト用 `Invoke-WebRequest` 関数をPowerShellセッション内で差し替えて、チェックサム検査、展開、stage検査、切替、再確認、更新を通した。これはGitHubネットワーク配布の代用ではなく、配布物取得後のインストール処理のローカル統合確認である。

## 修正した問題と検証上の中断

- 初回のWindows PowerShell 5.1確認で、UTF-8 BOMのない日本語PowerShellスクリプトがWindowsの既定コードページとして読み込まれ、構文解析に失敗した。スクリプトをUTF-8 BOM付きに変更し、Windows PowerShell 5.1で `-Verify` の成功・失敗経路を再実行して解消を確認した。
- 最初のsandbox内restoreはWindowsサービスアクセス制限のため失敗した。権限付き再実行でrestore/build/testに成功した。
- 最初の完全統合検証では、Codexのコマンドごとに一時領域パスが異なるため、別コマンドで作成したZIPを読めなかった。ZIP/publish出力をcheckoutの一時作業領域へ置き、同一コマンド内で再実行して成功した。この問題は実装不具合ではない。
- エラー終了コードを観測する最初のラッパーは、PowerShellの子スクリプトの `exit` 状態をホスト終了コードへ伝播しなかった。ラッパー側で `$?` を明示的に変換して再検証し、マニフェスト不一致・checksum不一致を終了コード1相当として観測した。

## 未確認事項・残課題

- PRマージ前のため、`.github/workflows/release-cli.yml` をタグpushでGitHub Actions実行していない。GitHub Releaseの作成、生成ZIPの実際のasset名・内容、checksum添付は未確認。
- 公開GitHub Releaseからの実ダウンロード、および通常ユーザー権限の別Windows環境での初回インストール・更新は未確認。今回のローカルZIP試験は配布元への認証ではなく、検証関数を置き換えたもの。
- SHA-256はZIPと同じGitHub Release権限で公開される。署名、署名者鍵、独立したアテステーション検証は未実装。
- PR最新HEAD `577c352e13f887c54f3d957930912b8016b661bb` の通常CI run #89（run ID `44101434230`）はWindows・Ubuntu両方成功した。タグpush時にReleaseを作るworkflow自体はまだ実行していない。
- MCPによる固定パス解決・プロセス起動・応答変換はPhase 2対象のため未実装・未検証。Phase 3の既定E2E起動確認も未実施。

これらは未確認事項として扱い、成功やPhase 3完了を意味しない。
