[CmdletBinding()]
param(
    [switch]$RunCodex,
    [string]$CodexExecutable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    & $FilePath @Arguments
    $nativeExitCode = $LASTEXITCODE
    if ($nativeExitCode -ne 0) {
        throw ("{0} に失敗しました (exit code {1})。" -f $Description, $nativeExitCode)
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contractTemplatePath = Join-Path $PSScriptRoot 'task-contract.template.json'
$cliProjectPath = Join-Path $repositoryRoot 'src\Ateliers.Ai.Orchestrator.Cli\Ateliers.Ai.Orchestrator.Cli.csproj'
$cliExecutable = Join-Path $repositoryRoot 'src\Ateliers.Ai.Orchestrator.Cli\bin\Release\net10.0\Ateliers.Ai.Orchestrator.Cli.exe'
$tempContractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('ateliers-issue-17-' + [Guid]::NewGuid().ToString('N'))
$tempContractPath = Join-Path $tempContractDirectory 'task-contract.json'

Push-Location $repositoryRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git'))) {
        throw ("Gitリポジトリが見つかりません: {0}" -f $repositoryRoot)
    }
    if (-not (Test-Path -LiteralPath $contractTemplatePath -PathType Leaf)) {
        throw ("契約テンプレートが見つかりません: {0}" -f $contractTemplatePath)
    }

    $gitCommand = Get-Command 'git.exe' -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) { $gitCommand = Get-Command 'git' -ErrorAction SilentlyContinue }
    if ($null -eq $gitCommand) { throw 'Git が PATH にありません。Git for Windows をインストールしてください。' }

    $gitStatus = @(& $gitCommand.Source -C $repositoryRoot status --porcelain=v1 --untracked-files=all 2>&1)
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0) {
        throw ("Git の状態を確認できませんでした。作業を停止します。診断: {0}" -f ($gitStatus -join ' '))
    }
    if ($gitStatus.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace(($gitStatus -join ''))) {
        throw '作業ツリーに変更があります。変更を保持したまま停止しました。Issue #17 の実行には clean な既定ブランチが必要です。'
    }

    $dotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) { $dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue }
    if ($null -eq $dotnetCommand) { throw '.NET SDK 10 の dotnet が PATH にありません。' }

    Write-Host 'Release ビルドを実行します。'
    Invoke-CheckedNative -FilePath $dotnetCommand.Source -Arguments @(
        'build', $cliProjectPath, '--configuration', 'Release', '--nologo'
    ) -Description 'Release ビルド'

    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf)) {
        throw ("Release CLI が見つかりません: {0}" -f $cliExecutable)
    }

    $contract = Get-Content -LiteralPath $contractTemplatePath -Raw | ConvertFrom-Json
    $contract.workingDirectory = $repositoryRoot
    New-Item -ItemType Directory -Path $tempContractDirectory -Force | Out-Null
    $contractJson = $contract | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText(
        $tempContractPath,
        $contractJson,
        (New-Object System.Text.UTF8Encoding($false))
    )

    Write-Host 'Release CLI で契約を検証します。'
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments @('validate', $tempContractPath) -Description '契約検証'

    if (-not $RunCodex) {
        Write-Host '準備と検証が完了しました。実Codex E2Eを行う場合は -RunCodex を指定してください。'
        return
    }

    if ([string]::IsNullOrWhiteSpace($CodexExecutable)) {
        $codexCommand = Get-Command 'codex.exe' -ErrorAction SilentlyContinue
        if ($null -eq $codexCommand) {
            throw 'Codex CLI (codex.exe) が PATH にありません。-CodexExecutable で絶対パスを指定してください。'
        }
        $CodexExecutable = $codexCommand.Source
    }
    if (-not [System.IO.Path]::IsPathRooted($CodexExecutable) -or
        -not (Test-Path -LiteralPath $CodexExecutable -PathType Leaf) -or
        [System.IO.Path]::GetExtension($CodexExecutable) -ne '.exe') {
        throw '-CodexExecutable には存在する codex.exe の絶対パスを指定してください。'
    }

    $ghCommand = Get-Command 'gh.exe' -ErrorAction SilentlyContinue
    if ($null -eq $ghCommand) { $ghCommand = Get-Command 'gh' -ErrorAction SilentlyContinue }
    if ($null -eq $ghCommand) { throw 'GitHub CLI (gh) が PATH にありません。' }
    $null = & $ghCommand.Source auth status --hostname github.com 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI が github.com に認証されていません。認証状態を確認してください。' }

    $runRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'AteliersAiOrchestrator\issue-17\runs'
    $lifecycleLog = Join-Path ([System.IO.Path]::GetTempPath()) 'AteliersAiOrchestrator\issue-17\lifecycle.jsonl'
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $runArguments = @(
        'run', $tempContractPath,
        '--agent', 'codex',
        '--codex-exe', $CodexExecutable,
        '--runs', $runRoot,
        '--lifecycle-log', $lifecycleLog,
        '--slack-notifications', 'disabled'
    )

    Write-Host ''
    Write-Host '実行すると、現在の既定ブランチから専用ブランチを作成し、Codexが変更・commit・pushを行い、PRとReviewerコメントを作成します。自動mergeはしません。'
    Write-Host '作業ツリーに変更がある場合、既存の変更は破棄せずに停止します。実行記録は一時フォルダーに保存します。'
    $confirmation = Read-Host '続行するには RUN ISSUE-17 CODEX E2E と入力してください'
    if ($confirmation -cne 'RUN ISSUE-17 CODEX E2E') {
        throw '確認文字列が一致しないため、実Codex E2Eを開始しませんでした。'
    }

    Write-Host 'Codex E2E を開始します。'
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments $runArguments -Description 'Issue #17 Codex E2E'
}
catch {
    [Console]::Error.WriteLine(("Issue #17 検証を停止しました: {0}" -f $_.Exception.Message))
    exit 1
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $tempContractDirectory) {
        Remove-Item -LiteralPath $tempContractDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
