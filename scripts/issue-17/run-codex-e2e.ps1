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

function Get-CheckedNativeOutput {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $nativeOutput = @(& $FilePath @Arguments 2>&1)
    $nativeExitCode = $LASTEXITCODE
    if ($nativeExitCode -ne 0) {
        throw ("{0} に失敗しました (exit code {1}): {2}" -f $Description, $nativeExitCode, ($nativeOutput -join ' '))
    }
    return (($nativeOutput -join "`n").Trim())
}

function Write-TaskContract {
    param(
        [Parameter(Mandatory = $true)]$Contract,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $Contract.workingDirectory = $WorkingDirectory
    $json = $Contract | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText(
        $Path,
        $json,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contractTemplatePath = Join-Path $PSScriptRoot 'task-contract.template.json'
$cliProjectPath = Join-Path $repositoryRoot 'src\Ateliers.Ai.Orchestrator.Cli\Ateliers.Ai.Orchestrator.Cli.csproj'
$cliExecutable = Join-Path $repositoryRoot 'src\Ateliers.Ai.Orchestrator.Cli\bin\Release\net10.0\Ateliers.Ai.Orchestrator.Cli.exe'
$tempContractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('ateliers-issue-17-contract-' + [Guid]::NewGuid().ToString('N'))
$tempContractPath = Join-Path $tempContractDirectory 'task-contract.json'
$executionRoot = $null
$executionSucceeded = $false

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

    $dotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) { $dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue }
    if ($null -eq $dotnetCommand) { throw '.NET SDK 10 の dotnet が PATH にありません。' }

    if ($RunCodex) {
        $gitStatus = @(& $gitCommand.Source -C $repositoryRoot status --porcelain=v1 --untracked-files=all 2>&1)
        $gitExitCode = $LASTEXITCODE
        if ($gitExitCode -ne 0) {
            throw ("Git の状態を確認できませんでした。作業を停止します。診断: {0}" -f ($gitStatus -join ' '))
        }
        if ($gitStatus.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace(($gitStatus -join ''))) {
            throw '実Codex E2Eにはcleanな既定ブランチが必要です。既存の変更は保持して停止しました。'
        }
    }

    Write-Host 'Release ビルドを実行します。'
    Invoke-CheckedNative -FilePath $dotnetCommand.Source -Arguments @(
        'build', $cliProjectPath, '--configuration', 'Release', '--nologo'
    ) -Description 'Release ビルド'

    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf)) {
        throw ("Release CLI が見つかりません: {0}" -f $cliExecutable)
    }

    $contract = Get-Content -LiteralPath $contractTemplatePath -Raw | ConvertFrom-Json
    New-Item -ItemType Directory -Path $tempContractDirectory -Force | Out-Null
    Write-TaskContract -Contract $contract -WorkingDirectory $repositoryRoot -Path $tempContractPath

    Write-Host 'Release CLI で契約を検証します。'
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments @('validate', $tempContractPath) -Description '契約検証'

    if (-not $RunCodex) {
        Write-Host '準備と検証が完了しました。実Codex E2Eは起動しておらず、ブランチも切り替えていません。実行する場合は -RunCodex を指定してください。'
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

    $repositoryIdentity = Get-CheckedNativeOutput -FilePath $ghCommand.Source -Arguments @(
        'repo', 'view', '--json', 'nameWithOwner', '--template', '{{.nameWithOwner}}'
    ) -Description 'GitHubリポジトリの確認'
    if ([string]::IsNullOrWhiteSpace($repositoryIdentity)) {
        throw 'GitHubリポジトリを特定できません。'
    }
    $githubUrl = 'https://github.com/' + $repositoryIdentity + '.git'

    Write-Host ''
    Write-Host '実Codex E2Eでは、既存checkoutのブランチとファイルを維持し、%TEMP% に一時Gitクローンを作成して実行します。専用ブランチの作成、commit、push、PRとReviewerコメントの作成は一時クローン内で行われます。自動mergeはしません。'
    Write-Host '実行に失敗した場合は一時クローンを削除せず、調査用に残します。'
    $confirmation = Read-Host '続行するには RUN ISSUE-17 CODEX E2E と入力してください'
    if ($confirmation -cne 'RUN ISSUE-17 CODEX E2E') {
        throw '確認文字列が一致しないため、実Codex E2Eを開始しませんでした。'
    }

    $executionRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AteliersAiOrchestrator\issue-17\checkout-' + [Guid]::NewGuid().ToString('N'))
    $checkoutPath = Join-Path $executionRoot 'repository'
    New-Item -ItemType Directory -Path $executionRoot -Force | Out-Null
    Invoke-CheckedNative -FilePath $gitCommand.Source -Arguments @(
        'clone', '--shared', '--no-checkout', $repositoryRoot, $checkoutPath
    ) -Description '一時Gitクローンの作成'
    Invoke-CheckedNative -FilePath $gitCommand.Source -Arguments @(
        '-C', $checkoutPath, 'remote', 'set-url', 'origin', $githubUrl
    ) -Description '一時クローンのGitHub接続設定'
    Invoke-CheckedNative -FilePath $gitCommand.Source -Arguments @(
        '-C', $checkoutPath, 'fetch', '--no-tags', 'origin',
        ('+refs/heads/{0}:refs/remotes/origin/{0}' -f $defaultBranch)
    ) -Description '一時クローンの既定ブランチ更新'
    Invoke-CheckedNative -FilePath $gitCommand.Source -Arguments @(
        '-C', $checkoutPath, 'switch', '--detach', ('refs/remotes/origin/' + $defaultBranch)
    ) -Description '一時クローンの既定ブランチ準備'

    Write-TaskContract -Contract $contract -WorkingDirectory $checkoutPath -Path $tempContractPath
    Write-Host '一時クローンを対象に、Release CLIで契約を再検証します。'
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments @('validate', $tempContractPath) -Description '一時クローン契約の検証'

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

    Write-Host 'Codex E2E を開始します。元のcheckout内のブランチは切り替わりません。'
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments $runArguments -Description 'Issue #17 Codex E2E'
    $executionSucceeded = $true
}
catch {
    [Console]::Error.WriteLine(("Issue #17 検証を停止しました: {0}" -f $_.Exception.Message))
    if ($null -ne $executionRoot -and (Test-Path -LiteralPath $executionRoot)) {
        [Console]::Error.WriteLine(("一時クローンを保持しました。確認後に削除してください: {0}" -f $executionRoot))
    }
    exit 1
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $tempContractDirectory) {
        Remove-Item -LiteralPath $tempContractDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($executionSucceeded -and $null -ne $executionRoot -and (Test-Path -LiteralPath $executionRoot)) {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $executionPath = [System.IO.Path]::GetFullPath($executionRoot)
        $tempPrefix = $tempRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $executionPath.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            [Console]::Error.WriteLine(("安全確認のため一時クローンを削除しませんでした: {0}" -f $executionPath))
        }
        else {
            Remove-Item -LiteralPath $executionPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
