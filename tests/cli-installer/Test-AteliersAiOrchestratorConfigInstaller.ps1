$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ateliers-config-installer-test-' + [Guid]::NewGuid().ToString('N'))
$fixtureRoot = Join-Path $tempRoot 'fixture'
$installerDirectory = Join-Path $fixtureRoot 'scripts\cli-installer'
$sourceDirectory = Join-Path $fixtureRoot 'config'
$localAppData = Join-Path $tempRoot 'local-app-data'
$destinationDirectory = Join-Path $localAppData 'Ateliers-Dev\Ai-Orchestrator\config'
$installerPath = Join-Path $installerDirectory 'Install-AteliersAiOrchestratorConfig.ps1'
$sourceConfigPath = Join-Path $sourceDirectory 'slack.json'
$sourceSecretsPath = Join-Path $sourceDirectory 'slack.secrets.json'
$global:injectPartialBackupFailure = $false
$global:partialBackupPath = $null

function Assert-Equal {
    param([string]$Expected, [string]$Actual, [string]$Message)
    if ($Expected -cne $Actual) { throw $Message }
}

function Assert-PowerShellSourcesHaveCompatibleEncoding {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $sourceDirectories = @(
        (Join-Path $repositoryRoot 'scripts'),
        (Join-Path $repositoryRoot 'tests')
    )
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $sourceFiles = @($sourceDirectories | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -File | Where-Object {
            $_.Extension -in @('.ps1', '.psm1', '.psd1')
        }
    })

    foreach ($sourceFile in $sourceFiles) {
        $bytes = [System.IO.File]::ReadAllBytes($sourceFile.FullName)
        $hasUtf8Bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $offset = if ($hasUtf8Bom) { 3 } else { 0 }
        try {
            $sourceText = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
        }
        catch {
            throw ("PowerShellソースをUTF-8として読み取れません: {0}" -f $sourceFile.FullName)
        }

        if (-not $hasUtf8Bom -and $sourceText -match '[^\x00-\x7F]') {
            throw ("Windows PowerShell 5.1向けにUTF-8 BOMが必要です: {0}" -f $sourceFile.FullName)
        }

        if ($sourceFile.Name -eq 'Install-AteliersAiOrchestratorConfig.ps1' -and
            ($sourceText -notmatch 'Slack設定ファイルの更新に失敗しました' -or
                $sourceText -notmatch '配置元にSlack設定ファイルがないため')) {
            throw '設定インストーラーの日本語メッセージが正しくありません。'
        }

        $tokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            $sourceFile.FullName,
            [ref]$tokens,
            [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count -gt 0) {
            throw ("PowerShellソースの構文解析に失敗しました: {0} ({1} errors)" -f $sourceFile.FullName, $parseErrors.Count)
        }
    }
}

Assert-PowerShellSourcesHaveCompatibleEncoding

$cliInstallerPath = Join-Path $PSScriptRoot '..\..\scripts\cli-installer\Install-AteliersAiOrchestrator.ps1'
$cliInstallerSource = [System.IO.File]::ReadAllText($cliInstallerPath, [System.Text.Encoding]::UTF8)
if ($cliInstallerSource -notmatch '(?m)^\s*& \$configInstaller -PreserveExisting\s*$') {
    throw 'CLIインストーラーが既存設定を維持して設定インストーラーを呼び出していません。'
}

function Copy-Item {
    [CmdletBinding()]
    param(
        [string]$LiteralPath,
        [string]$Destination,
        [switch]$Force
    )

    if ($global:injectPartialBackupFailure -and $Destination -match '\\.slack\.secrets\.json\..+\.pending$') {
        $global:partialBackupPath = $Destination
        [System.IO.File]::WriteAllText($Destination, 'incomplete')
        throw 'Injected partial backup failure.'
    }

    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}

function Invoke-WithPartialBackupFailure {
    param([string]$ScriptPath)

    $global:partialBackupPath = $null
    $global:injectPartialBackupFailure = $true
    $errorText = ''
    try { & $ScriptPath | Out-Null }
    catch { $errorText = $_.Exception.Message }
    finally { $global:injectPartialBackupFailure = $false }
    [pscustomobject]@{ ErrorText = $errorText; PartialPath = $global:partialBackupPath }
}

try {
    New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\scripts\cli-installer\Install-AteliersAiOrchestratorConfig.ps1') -Destination $installerPath
    $env:LOCALAPPDATA = $localAppData
    Set-Content -LiteralPath $sourceConfigPath -Value '{"version":"source-1"}' -NoNewline
    Set-Content -LiteralPath $sourceSecretsPath -Value '{"tokens":{"fixture":"source-1"}}' -NoNewline

    $output = @(& $installerPath)
    Assert-Equal '{"version":"source-1"}' (Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw) '初回コピーでslack.jsonが一致しません。'
    Assert-Equal '{"tokens":{"fixture":"source-1"}}' (Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.secrets.json') -Raw) '初回コピーでsecretsが一致しません。'

    $oldConfig = Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw
    $oldSecrets = Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.secrets.json') -Raw
    Set-Content -LiteralPath $sourceConfigPath -Value '{"version":"source-2"}' -NoNewline
    Set-Content -LiteralPath $sourceSecretsPath -Value '{"tokens":{"fixture":"source-2"}}' -NoNewline
    $backupRoot = Join-Path $destinationDirectory 'backups'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $collisionTime = [DateTime]::Now
    foreach ($offset in 0..3) {
        $collisionPath = Join-Path $backupRoot ($collisionTime.AddSeconds($offset).ToString('yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Path $collisionPath -Force | Out-Null
    }

    $output = @(& $installerPath)
    Assert-Equal '{"version":"source-2"}' (Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw) '更新でslack.jsonが置き換わりません。'
    $backupPath = $output[-1] -replace '^既存設定のバックアップ: ', ''
    Assert-Equal $oldConfig (Get-Content -LiteralPath (Join-Path $backupPath 'slack.json') -Raw) '更新前slack.jsonのバックアップが一致しません。'
    Assert-Equal $oldSecrets (Get-Content -LiteralPath (Join-Path $backupPath 'slack.secrets.json') -Raw) '更新前secretsのバックアップが一致しません。'
    if ((Split-Path -Leaf $backupPath) -notmatch '^\d{8}-\d{6}-\d{3}$') { throw '同秒バックアップの衝突を別フォルダーで回避できません。' }

    Set-Content -LiteralPath $sourceConfigPath -Value '{"version":"source-3"}' -NoNewline
    Set-Content -LiteralPath $sourceSecretsPath -Value '{"tokens":{"fixture":"source-3"}}' -NoNewline
    $beforePreserve = Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw
    $preserveOutput = @(& $installerPath -PreserveExisting)
    Assert-Equal $beforePreserve (Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw) 'PreserveExistingが既存設定を上書きしました。'

    $beforeFailureConfig = Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw
    $beforeFailureSecrets = Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.secrets.json') -Raw
    Set-Content -LiteralPath $sourceConfigPath -Value '{"version":"source-4"}' -NoNewline
    Set-Content -LiteralPath $sourceSecretsPath -Value '{"tokens":{"fixture":"source-4"}}' -NoNewline
    $failure = Invoke-WithPartialBackupFailure -ScriptPath $installerPath
    Assert-Equal $beforeFailureConfig (Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw) 'バックアップ失敗でslack.jsonが変化しました。'
    Assert-Equal $beforeFailureSecrets (Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.secrets.json') -Raw) 'バックアップ失敗でsecretsが変化しました。'
    if ([string]::IsNullOrWhiteSpace($failure.ErrorText)) { throw 'バックアップ失敗がエラーとして報告されませんでした。' }
    if ($failure.ErrorText -notmatch 'バックアップ未完了.*元設定は変更されていません') { throw 'バックアップ未完了時の状態がエラーに明示されていません。' }
    if ([string]::IsNullOrWhiteSpace($failure.PartialPath)) { throw '部分コピー失敗の注入が実行されませんでした。' }
    if ($failure.PartialPath -and (Test-Path -LiteralPath $failure.PartialPath)) { throw '不完全な一時バックアップが残っています。' }
    $failedBackupDirectory = Split-Path -Parent $failure.PartialPath
    Assert-Equal $beforeFailureConfig (Get-Content -LiteralPath (Join-Path $failedBackupDirectory 'slack.json') -Raw) '先行して完成したバックアップが保持されていません。'
    if (Test-Path -LiteralPath (Join-Path $failedBackupDirectory 'slack.secrets.json')) { throw '失敗したsecretsバックアップが確定扱いされています。' }
    if ($failure.ErrorText -match 'source-[1234]') { throw '設定JSONの値がエラーに現れました。' }

    Remove-Item -LiteralPath $sourceConfigPath, $sourceSecretsPath -Force
    $missingOutput = @(& $installerPath)
    Assert-Equal $beforePreserve (Get-Content -LiteralPath (Join-Path $destinationDirectory 'slack.json') -Raw) '配置元の欠落で既存設定が変化しました。'
    $allOutput = @($output) + @($preserveOutput) + @($missingOutput)
    if (($allOutput -join [Environment]::NewLine) -match 'source-[123]') { throw '設定JSONの値が標準出力に現れました。' }

    Write-Output '設定インストーラー: 初回配置、バックアップ付き更新、同秒衝突、既存維持、配置元欠落、部分バックアップ失敗時の元設定維持、値の非表示を確認しました。'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

