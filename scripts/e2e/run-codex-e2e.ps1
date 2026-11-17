[CmdletBinding()]
param(
    [string]$ScenarioPath,
    [switch]$RunCodex,
    [string]$CodexExecutable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-CheckedNative {
    param([string]$FilePath, [string[]]$Arguments, [string]$Description)
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $nativeOutput = @(& $FilePath @Arguments 2>&1)
        $nativeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    foreach ($line in $nativeOutput) { Write-Host $line }
    if ($nativeExitCode -ne 0) {
        $diagnostics = ($nativeOutput -join ' ')
        if ($diagnostics.Length -gt 4000) { $diagnostics = $diagnostics.Substring(0, 4000) }
        throw ("{0} に失敗しました (exit code {1}): {2}" -f $Description, $nativeExitCode, $diagnostics)
    }
}

function Get-CheckedNativeOutput {
    param([string]$FilePath, [string[]]$Arguments, [string]$Description)
    $standardErrorPath = [System.IO.Path]::GetTempFileName()
    try {
        $savedErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $nativeOutput = @(& $FilePath @Arguments 2> $standardErrorPath)
            $nativeExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $savedErrorActionPreference
        }

        $standardOutputText = (($nativeOutput | ForEach-Object { "$($_)" }) -join [Environment]::NewLine).Trim()
        $standardErrorContent = Get-Content -LiteralPath $standardErrorPath -Raw -ErrorAction SilentlyContinue
        $standardErrorText = if ($null -eq $standardErrorContent) { '' } else { $standardErrorContent.Trim() }
        if (-not [string]::IsNullOrWhiteSpace($standardErrorText)) {
            [Console]::Error.WriteLine($standardErrorText)
        }
        if ($nativeExitCode -ne 0) {
            $diagnosticParts = @()
            if (-not [string]::IsNullOrWhiteSpace($standardOutputText)) { $diagnosticParts += $standardOutputText }
            if (-not [string]::IsNullOrWhiteSpace($standardErrorText)) { $diagnosticParts += $standardErrorText }
            $diagnostics = $diagnosticParts -join ' '
            if ($diagnostics.Length -gt 4000) { $diagnostics = $diagnostics.Substring(0, 4000) }
            throw ("{0} に失敗しました (exit code {1}): {2}" -f $Description, $nativeExitCode, $diagnostics)
        }
        return $standardOutputText
    }
    finally {
        Remove-Item -LiteralPath $standardErrorPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-ScenarioRepositoryIdentity {
    param($Contract)
    if ($null -eq $Contract.inputs -or @($Contract.inputs).Count -eq 0) {
        throw 'E2Eシナリオにinputs.referenceがありません。'
    }

    $repositoryIdentities = New-Object 'System.Collections.Generic.List[string]'
    foreach ($item in $Contract.inputs) {
        $referenceText = [string]$item.reference
        $referenceUri = $null
        if ([string]::IsNullOrWhiteSpace($referenceText) -or
            -not [System.Uri]::TryCreate($referenceText, [System.UriKind]::Absolute, [ref]$referenceUri) -or
            $referenceUri.Scheme -cne 'https' -or $referenceUri.Host -cne 'github.com' -or
            -not [string]::IsNullOrEmpty($referenceUri.UserInfo) -or
            $referenceText.IndexOfAny([char[]]@('?', '#')) -ge 0) {
            throw 'E2Eシナリオの参照URLはUserInfo・query・fragmentを含まないHTTPS GitHubリポジトリURLで指定してください。'
        }
        $referenceSegments = @($referenceUri.AbsolutePath.Trim('/').Split('/'))
        if ($referenceSegments.Count -lt 2 -or
            $referenceSegments[0] -notmatch '^[A-Za-z0-9][A-Za-z0-9-]*$' -or
            $referenceSegments[1] -notmatch '^[A-Za-z0-9_.-]+$') {
            throw 'E2Eシナリオの参照URLからGitHubリポジトリを特定できません。URL形式を確認してください。'
        }
        $repositoryIdentity = ('{0}/{1}' -f $referenceSegments[0], $referenceSegments[1]).ToLowerInvariant()
        if ($repositoryIdentity.EndsWith('.git', [System.StringComparison]::OrdinalIgnoreCase)) {
            $repositoryIdentity = $repositoryIdentity.Substring(0, $repositoryIdentity.Length - 4)
        }
        if (-not $repositoryIdentities.Contains($repositoryIdentity)) { $repositoryIdentities.Add($repositoryIdentity) }
    }

    if ($repositoryIdentities.Count -ne 1) {
        throw ("E2Eシナリオのinputs.referenceは単一のGitHubリポジトリを指す必要があります: {0}" -f ($repositoryIdentities -join ', '))
    }
    return $repositoryIdentities[0]
}

function Invoke-FakeUnitTests {
    param([string]$DotnetPath, [string]$ProjectPath)
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $DotnetPath run --project $ProjectPath --configuration Release --no-build --no-restore -- --unit 2>&1)
        $testExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    $outputLines = @($output | ForEach-Object { "$($_)".TrimEnd([char]13) })
    $outputLines | ForEach-Object { Write-Host $_ }
    $expectedSkips = @(
        '未検証: PR不要のCodex実行では実行bit変更を許容する: Unixの実行属性を確認するテストはWindowsでは実行できません。',
        '未検証: PR必須Codex snapshotは新規実行ファイルのmodeを保持する: Unix mode付きファイルのsnapshot検証はUnix runnerで実行します。'
    )
    $summaryLines = @($outputLines | Where-Object { $_.StartsWith('結果:') })
    $skipLines = @($outputLines | Where-Object { $_.StartsWith('未検証:') })
    $missingExpectedSkips = @($expectedSkips | Where-Object { $skipLines -cnotcontains $_ })
    $summaryMatch = $null
    if ($summaryLines.Count -eq 1) {
        $summaryMatch = [regex]::Match($summaryLines[0], '^結果: (\d+)成功, (\d+)未検証, (\d+)失敗 / (\d+)項目$')
    }
    $hasValidSummary = $summaryMatch -and $summaryMatch.Success -and
        [int]$summaryMatch.Groups[2].Value -eq $expectedSkips.Count -and
        [int]$summaryMatch.Groups[3].Value -eq 0 -and
        [int]$summaryMatch.Groups[1].Value + [int]$summaryMatch.Groups[2].Value -eq [int]$summaryMatch.Groups[4].Value
    if ($testExitCode -eq 2 -and $hasValidSummary -and $skipLines.Count -eq $expectedSkips.Count -and $missingExpectedSkips.Count -eq 0) {
        Write-Host 'Windows固有のUnix modeテスト2件のみ未検証として扱います。'
        return
    }
    if ($testExitCode -ne 0 -or -not $hasValidSummary -or $skipLines.Count -ne 0) {
        throw ("Fake-only Unit testsがCIで許容するWindows結果と一致しません (exit code {0})。" -f $testExitCode)
    }
}

function Write-TaskContract {
    param($Contract, [string]$WorkingDirectory, [string]$RepositorySha, [string]$Path)
    $Contract.workingDirectory = $WorkingDirectory
    foreach ($item in $Contract.inputs) { $item.revision = "GitHub default branch $RepositorySha" }
    $json = $Contract | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$defaultScenarioPath = Join-Path $PSScriptRoot 'scenarios\testbed-review-cycle.json'
if ([string]::IsNullOrWhiteSpace($ScenarioPath)) { $ScenarioPath = $defaultScenarioPath }
if ([System.IO.Path]::IsPathRooted($ScenarioPath)) {
    $scenarioPath = [System.IO.Path]::GetFullPath($ScenarioPath)
}
else {
    $scenarioPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ScenarioPath))
}
$cliExecutable = Join-Path $repositoryRoot 'src\Ateliers.Ai.Orchestrator.Cli\bin\Release\net10.0\Ateliers.Ai.Orchestrator.Cli.exe'
$executionRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AteliersAiOrchestrator\e2e\checkout-' + [Guid]::NewGuid().ToString('N'))
$checkoutPath = Join-Path $executionRoot 'repository'
$tempContractPath = Join-Path $executionRoot 'task-contract.json'
$runStarted = $false
$keepExecutionRoot = $false

Push-Location $repositoryRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git'))) { throw ("Gitリポジトリが見つかりません: {0}" -f $repositoryRoot) }
    if (-not (Test-Path -LiteralPath $scenarioPath -PathType Leaf)) { throw ("E2Eシナリオが見つかりません: {0}" -f $scenarioPath) }
    $contract = Get-Content -LiteralPath $scenarioPath -Raw | ConvertFrom-Json
    $repositoryIdentity = Get-ScenarioRepositoryIdentity -Contract $contract
    $repositoryUrl = 'https://github.com/' + $repositoryIdentity + '.git'

    $gitCommand = Get-Command 'git.exe' -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) { $gitCommand = Get-Command 'git' -ErrorAction SilentlyContinue }
    if ($null -eq $gitCommand) { throw 'Git が PATH にありません。Git for Windows をインストールしてください。' }
    $dotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) { $dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue }
    if ($null -eq $dotnetCommand) { throw '.NET SDK 10 の dotnet が PATH にありません。' }
    $ghCommand = Get-Command 'gh.exe' -ErrorAction SilentlyContinue
    if ($null -eq $ghCommand) { $ghCommand = Get-Command 'gh' -ErrorAction SilentlyContinue }
    if ($null -eq $ghCommand) { throw 'GitHub CLI (gh) が PATH にありません。' }

    Write-Host '本体リポジトリをRelease buildし、Fake-only Unit testsを実行します。'
    Invoke-CheckedNative -FilePath $dotnetCommand.Source -Arguments @('restore', (Join-Path $repositoryRoot 'Ateliers.Ai.Orchestrator.sln')) -Description 'ソリューションのrestore'
    Invoke-CheckedNative -FilePath $dotnetCommand.Source -Arguments @(
        'build', (Join-Path $repositoryRoot 'Ateliers.Ai.Orchestrator.sln'), '--configuration', 'Release', '--no-restore', '--nologo'
    ) -Description 'Release build'
    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf)) { throw ("Release CLI が見つかりません: {0}" -f $cliExecutable) }
    Invoke-FakeUnitTests -DotnetPath $dotnetCommand.Source -ProjectPath (Join-Path $repositoryRoot 'tests\Ateliers.Ai.Orchestrator.Tests\Ateliers.Ai.Orchestrator.Tests.csproj')

    Write-Host ("GitHub認証と{0}の既定ブランチSHAを確認します。" -f $repositoryIdentity)
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $authOutput = @(& $ghCommand.Source auth status --hostname github.com 2>&1)
        $authExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($authExitCode -ne 0) { throw 'GitHub CLI が github.com に認証されていません。認証状態を確認してください。' }
    $defaultBranch = Get-CheckedNativeOutput -FilePath $ghCommand.Source -Arguments @(
        'repo', 'view', $repositoryIdentity, '--json', 'defaultBranchRef', '--template', '{{.defaultBranchRef.name}}'
    ) -Description 'GitHub既定ブランチの確認'
    if ([string]::IsNullOrWhiteSpace($defaultBranch)) { throw 'GitHubリポジトリの既定ブランチを特定できません。' }
    $branchApiPath = 'repos/{0}/branches/{1}' -f $repositoryIdentity, [System.Uri]::EscapeDataString($defaultBranch)
    $remoteHead = Get-CheckedNativeOutput -FilePath $ghCommand.Source -Arguments @('api', $branchApiPath, '--jq', '.commit.sha') -Description 'GitHub既定ブランチSHAの確認'

    Write-Host 'シナリオ対象リポジトリを一時ディレクトリへcloneし、clean状態を確認します。'
    New-Item -ItemType Directory -Path $executionRoot -Force | Out-Null
    Invoke-CheckedNative -FilePath $gitCommand.Source -Arguments @(
        'clone', '--single-branch', '--branch', $defaultBranch, $repositoryUrl, $checkoutPath
    ) -Description 'シナリオ対象リポジトリの一時clone作成'
    $localHead = Get-CheckedNativeOutput -FilePath $gitCommand.Source -Arguments @('-C', $checkoutPath, 'rev-parse', 'HEAD') -Description '一時cloneのSHA確認'
    if (-not $localHead.Equals($remoteHead, [System.StringComparison]::OrdinalIgnoreCase)) { throw '一時cloneがGitHub既定ブランチの確認済み最新SHAと一致しません。' }
    $gitStatus = Get-CheckedNativeOutput -FilePath $gitCommand.Source -Arguments @(
        '-C', $checkoutPath, 'status', '--porcelain=v1', '--untracked-files=all'
    ) -Description '一時cloneの状態確認'
    if (-not [string]::IsNullOrWhiteSpace($gitStatus)) {
        throw 'シナリオ対象リポジトリの一時cloneがcleanではありません。Codexは起動しません。'
    }

    Write-TaskContract -Contract $contract -WorkingDirectory $checkoutPath -RepositorySha $remoteHead -Path $tempContractPath
    Write-Host '現行Release CLIでシナリオ契約を検証します。'
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments @('validate', $tempContractPath) -Description 'E2Eシナリオ契約の検証'
    Write-Host ("準備完了: {0} {1} {2}。実Codexはまだ起動していません。" -f $repositoryIdentity, $defaultBranch, $remoteHead)

    if (-not $RunCodex) {
        Write-Host '準備確認だけで終了します。実行するには -RunCodex を指定し、開始時に確認文を入力してください。'
        return
    }

    if ([string]::IsNullOrWhiteSpace($CodexExecutable)) {
        $codexCommand = Get-Command 'codex.exe' -ErrorAction SilentlyContinue
        if ($null -eq $codexCommand) { throw '-CodexExecutable を省略する場合、Codex CLI (codex.exe) が PATH に必要です。' }
        $CodexExecutable = $codexCommand.Source
    }
    if (-not [System.IO.Path]::IsPathRooted($CodexExecutable) -or
        -not (Test-Path -LiteralPath $CodexExecutable -PathType Leaf) -or
        [System.IO.Path]::GetExtension($CodexExecutable) -ne '.exe') {
        throw '-CodexExecutable には存在する codex.exe の絶対パスを指定してください。'
    }

    Write-Host ''
    Write-Host ("対象GitHubリポジトリ: {0}" -f $repositoryIdentity)
    Write-Host 'この公開リポジトリで実Codexを実行します。Codex利用枠・費用が発生する場合があり、成功時は一意なブランチへのpush、公開PR、Reviewer結果コメントの投稿が行われます。自動マージはしません。失敗後にpush済みブランチやPRが残った場合は保持し、人間が確認して扱います。'
    $confirmationText = 'RUN CODEX E2E FOR {0} WITH PUSH AND PR' -f $repositoryIdentity.ToUpperInvariant()
    $confirmation = Read-Host ("続行するには {0} と入力してください" -f $confirmationText)
    if ($confirmation -cne $confirmationText) { throw '確認文字列が一致しないため、実Codex E2Eを開始しませんでした。' }

    $runRoot = Join-Path $executionRoot 'runs'
    $lifecycleLog = Join-Path $executionRoot 'lifecycle.jsonl'
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $runArguments = @('run', $tempContractPath, '--agent', 'codex', '--codex-exe', $CodexExecutable,
        '--runs', $runRoot, '--lifecycle-log', $lifecycleLog, '--slack-notifications', 'disabled')
    $runStarted = $true
    $keepExecutionRoot = $true
    Write-Host 'Codex E2Eを開始します。cleanな一時cloneを対象にします。'
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $runOutput = @(& $cliExecutable @runArguments 2>&1)
        $runExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    foreach ($line in $runOutput) { Write-Host $line }
    if ($runExitCode -ne 0) { throw ("Codex E2Eが失敗しました (exit code {0})。一時cloneと証跡、GitHub上のブランチ・PRを確認してください。" -f $runExitCode) }

    $runDirectories = @(Get-ChildItem -LiteralPath $runRoot -Directory)
    if ($runDirectories.Count -ne 1) { throw '実行記録ディレクトリを一意に特定できません。作業領域と証跡を保持します。' }
    $runDirectory = $runDirectories[0].FullName
    foreach ($evidenceName in @('github-pull-request.json', 'github-review-comment.json', 'events.jsonl', 'report.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $runDirectory $evidenceName) -PathType Leaf)) {
            throw ("必須証跡がありません: {0}。作業領域と証跡を保持します。" -f $evidenceName)
        }
    }
    $pullRequestEvidence = Get-Content -LiteralPath (Join-Path $runDirectory 'github-pull-request.json') -Raw | ConvertFrom-Json
    $commentEvidence = Get-Content -LiteralPath (Join-Path $runDirectory 'github-review-comment.json') -Raw | ConvertFrom-Json
    if (-not $pullRequestEvidence.verified -or $pullRequestEvidence.merged -or -not $commentEvidence.verified -or
        $pullRequestEvidence.headSha -ne $commentEvidence.headSha -or $pullRequestEvidence.baseSha -ne $commentEvidence.baseSha) {
        throw 'PRまたはReviewer結果コメントの証跡が不一致です。作業領域と証跡を保持します。'
    }

    Write-Host ''
    Write-Host 'PR作成とReviewer結果コメントの照合を確認しました。人間がPRを確認し、修正PRはTestbed mainへマージせず、証跡確認後にクローズしてください。'
    Write-Host ("PR: {0}" -f $pullRequestEvidence.url)
    Write-Host ("実行証跡: {0}" -f $runDirectory)
    Write-Host ("一時clone: {0}" -f $executionRoot)
    Write-Host '一時cloneとrun記録は自動削除しません。'
}
catch {
    [Console]::Error.WriteLine(("E2E準備または実行を停止しました: {0}" -f $_.Exception.Message))
    if ($runStarted -or $keepExecutionRoot) {
        [Console]::Error.WriteLine(("証跡を保持しています。一時cloneとrun記録: {0}" -f $executionRoot))
        [Console]::Error.WriteLine('GitHub上ですでにpushまたはPR作成が行われている場合、ブランチやPRを削除・クローズ・マージせず、人間が証跡と状態を確認してください。')
    }
    exit 1
}
finally {
    Pop-Location
    if (-not $keepExecutionRoot -and (Test-Path -LiteralPath $executionRoot)) {
        Remove-Item -LiteralPath $executionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
