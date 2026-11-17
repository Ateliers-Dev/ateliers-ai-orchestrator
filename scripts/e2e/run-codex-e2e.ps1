[CmdletBinding()]
param(
    [string]$ScenarioPath,
    [switch]$RunCodex,
    [string]$CodexExecutable,
    [string]$SlackConfigurationPath,
    [string]$SlackSecretsPath
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

function Invoke-CapturedNative {
    param([string]$FilePath, [string[]]$Arguments)
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $nativeOutput = @(& $FilePath @Arguments 2>&1)
        $nativeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    [pscustomobject]@{ ExitCode = $nativeExitCode; Output = @($nativeOutput | ForEach-Object { "$_" }) }
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

    Write-Host '本体リポジトリをRelease buildします。E2E実行の前提に単体テストは使いません。'
    Invoke-CheckedNative -FilePath $dotnetCommand.Source -Arguments @('restore', (Join-Path $repositoryRoot 'Ateliers.Ai.Orchestrator.sln')) -Description 'ソリューションのrestore'
    Invoke-CheckedNative -FilePath $dotnetCommand.Source -Arguments @(
        'build', (Join-Path $repositoryRoot 'Ateliers.Ai.Orchestrator.sln'), '--configuration', 'Release', '--no-restore', '--nologo'
    ) -Description 'Release build'
    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf)) { throw ("Release CLI が見つかりません: {0}" -f $cliExecutable) }

    if ([string]::IsNullOrWhiteSpace($SlackConfigurationPath)) {
        $SlackConfigurationPath = Join-Path $repositoryRoot 'config\slack.json'
    }
    if ([string]::IsNullOrWhiteSpace($SlackSecretsPath)) {
        $SlackSecretsPath = Join-Path $repositoryRoot 'config\slack.secrets.json'
    }
    if (-not (Test-Path -LiteralPath $SlackConfigurationPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $SlackSecretsPath -PathType Leaf)) {
        throw '本番Slack通知設定が不足しています。無効化・test宛てへの切替はせず、SlackConfigurationPath と SlackSecretsPath を明示してください。'
    }
    $slackSettings = Get-Content -LiteralPath $SlackConfigurationPath -Raw | ConvertFrom-Json
    $slackSecrets = Get-Content -LiteralPath $SlackSecretsPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$slackSettings.testChannelId) -or
        $null -eq $slackSettings.bots -or $null -eq $slackSecrets.tokens) {
        throw 'Slack設定の必須項目がありません。秘密値は表示せず、通知先とBot設定を確認してください。'
    }
    $productionChannels = @()
    foreach ($botId in @('orchestrator', 'implementer', 'reviewer')) {
        $bot = @($slackSettings.bots | Where-Object { $_.id -ceq $botId -and $_.enabled }) | Select-Object -First 1
        if ($null -eq $bot -or [string]::IsNullOrWhiteSpace([string]$bot.channelId) -or
            [string]$bot.channelId -match '^C_REPLACE_' -or
            [string]$bot.channelId -ceq [string]$slackSettings.testChannelId -or
            [string]::IsNullOrWhiteSpace([string]$slackSecrets.tokens.$botId)) {
            throw ("本番Slack通知先またはBot認証が設定されていません: {0}。test設定へ切り替えず停止します。" -f $botId)
        }
        if ($productionChannels -cnotcontains [string]$bot.channelId) { $productionChannels += [string]$bot.channelId }
    }
    Write-Host ("本番Slack通知先: {0}。Bot token値は表示しません。" -f ($productionChannels -join ', '))
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments @(
        'verify-slack', [System.IO.Path]::GetFullPath($SlackConfigurationPath), [System.IO.Path]::GetFullPath($SlackSecretsPath)
    ) -Description '本番Slack Bot認証の事前確認'

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
    if ($defaultBranch -cne 'main') { throw ("Testbedの既定ブランチがmainではありません: {0}" -f $defaultBranch) }
    $branchApiPath = 'repos/{0}/branches/{1}' -f $repositoryIdentity, [System.Uri]::EscapeDataString($defaultBranch)
    $remoteHead = Get-CheckedNativeOutput -FilePath $ghCommand.Source -Arguments @('api', $branchApiPath, '--jq', '.commit.sha') -Description 'GitHub既定ブランチSHAの確認'

    Write-Host 'シナリオ対象リポジトリを一時ディレクトリへcloneし、clean状態を確認します。'
    New-Item -ItemType Directory -Path $executionRoot -Force | Out-Null
    Invoke-CheckedNative -FilePath $gitCommand.Source -Arguments @(
        'clone', '--single-branch', '--branch', $defaultBranch, $repositoryUrl, $checkoutPath
    ) -Description 'シナリオ対象リポジトリの一時clone作成'
    $keepExecutionRoot = $true
    $localHead = Get-CheckedNativeOutput -FilePath $gitCommand.Source -Arguments @('-C', $checkoutPath, 'rev-parse', 'HEAD') -Description '一時cloneのSHA確認'
    if (-not $localHead.Equals($remoteHead, [System.StringComparison]::OrdinalIgnoreCase)) { throw '一時cloneがGitHub既定ブランチの確認済み最新SHAと一致しません。' }
    $gitStatus = Get-CheckedNativeOutput -FilePath $gitCommand.Source -Arguments @(
        '-C', $checkoutPath, 'status', '--porcelain=v1', '--untracked-files=all'
    ) -Description '一時cloneの状態確認'
    if (-not [string]::IsNullOrWhiteSpace($gitStatus)) {
        throw 'シナリオ対象リポジトリの一時cloneがcleanではありません。Codexは起動しません。'
    }

    $testbedProject = Join-Path $checkoutPath 'tests\WordCounter.Tests\WordCounter.Tests.csproj'
    $baselineBuild = Invoke-CapturedNative -FilePath $dotnetCommand.Source -Arguments @(
        'build', $testbedProject, '--configuration', 'Release', '--nologo'
    )
    $baselineBuild.Output | ForEach-Object { Write-Host $_ }
    @("exit code: $($baselineBuild.ExitCode)") + $baselineBuild.Output | Set-Content -LiteralPath (Join-Path $executionRoot 'testbed-baseline-build.log') -Encoding UTF8
    if ($baselineBuild.ExitCode -ne 0) { throw ("Testbed初期状態のRelease buildが失敗しました (exit code {0})。課題の基準状態を確認できません。" -f $baselineBuild.ExitCode) }
    $baselineChecks = Invoke-CapturedNative -FilePath $dotnetCommand.Source -Arguments @(
        'run', '--project', $testbedProject, '--configuration', 'Release', '--no-build', '--no-restore'
    )
    $baselineText = $baselineChecks.Output -join [Environment]::NewLine
    $baselineChecks.Output | ForEach-Object { Write-Host $_ }
    @("exit code: $($baselineChecks.ExitCode)") + $baselineChecks.Output | Set-Content -LiteralPath (Join-Path $executionRoot 'testbed-baseline-checks.log') -Encoding UTF8
    $expectedBaselineFailures = @('FAIL: tabs and newlines:', 'FAIL: whitespace only:')
    $hasExpectedBaseline = $baselineChecks.ExitCode -eq 1 -and
        $baselineText.Contains('2 check(s) failed.') -and
        @($expectedBaselineFailures | Where-Object { -not $baselineText.Contains($_) }).Count -eq 0 -and
        $baselineText -notmatch 'FAIL: (ordinary spaces|repeated spaces|empty input):'
    if (-not $hasExpectedBaseline) { throw 'Testbedの既知不具合を想定した基準失敗（tabs and newlines / whitespace only）と一致しません。原因を記録し、Codexは起動しません。' }
    Write-Host 'Testbed基準状態を確認しました。2件の既知チェックだけが失敗しています。'

    Write-TaskContract -Contract $contract -WorkingDirectory $checkoutPath -RepositorySha $remoteHead -Path $tempContractPath
    Write-Host '現行Release CLIでシナリオ契約を検証します。'
    Invoke-CheckedNative -FilePath $cliExecutable -Arguments @('validate', $tempContractPath) -Description 'E2Eシナリオ契約の検証'
    Write-Host ("準備完了: {0} {1} {2}。実Codexはまだ起動していません。" -f $repositoryIdentity, $defaultBranch, $remoteHead)

    if (-not $RunCodex) {
        Write-Host '準備確認だけで終了します。実行するには -RunCodex を指定し、開始時に確認文を入力してください。'
        Write-Host 'E2Eは未実施のため、リリース判定は未完了です。'
        $keepExecutionRoot = $false
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
    $effectiveCodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME')
    if ([string]::IsNullOrWhiteSpace($effectiveCodexHome)) {
        $effectiveCodexHome = Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)) '.codex'
    }
    else {
        $effectiveCodexHome = [System.IO.Path]::GetFullPath($effectiveCodexHome)
    }
    Write-Host ("Codex CLI認証・設定home: {0}（認証ファイルはコピーせず、親CODEX_HOME未設定でも既定homeを使用します）" -f $effectiveCodexHome)
    Write-Host 'この公開リポジトリで実Codexを実行します。Codex利用枠・費用が発生する場合があり、成功時は一意なブランチへのpush、公開PR、Reviewer結果コメントの投稿が行われます。自動マージはしません。失敗後にpush済みブランチやPRが残った場合は保持し、人間が確認して扱います。'
    $confirmationText = 'RUN CODEX E2E FOR {0} WITH PUSH AND PR AND SLACK TO {1}' -f $repositoryIdentity.ToUpperInvariant(), ($productionChannels -join ',')
    $confirmation = Read-Host ("続行するには {0} と入力してください" -f $confirmationText)
    if ($confirmation -cne $confirmationText) { throw '確認文字列が一致しないため、実Codex E2Eを開始しませんでした。' }

    $runRoot = Join-Path $executionRoot 'runs'
    $lifecycleLog = Join-Path $executionRoot 'lifecycle.jsonl'
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $runArguments = @('run', $tempContractPath, '--agent', 'codex', '--codex-exe', $CodexExecutable,
        '--runs', $runRoot, '--lifecycle-log', $lifecycleLog, '--slack-notifications', 'auto',
        '--slack-config', [System.IO.Path]::GetFullPath($SlackConfigurationPath),
        '--slack-secrets', [System.IO.Path]::GetFullPath($SlackSecretsPath))
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
    @("exit code: $runExitCode") + @($runOutput | ForEach-Object { "$_" }) |
        Set-Content -LiteralPath (Join-Path $executionRoot 'orchestrator-cli-output.log') -Encoding UTF8
    foreach ($line in $runOutput) { Write-Host $line }
    if ($runExitCode -ne 0) { throw ("Codex E2Eが失敗しました (exit code {0})。一時cloneと証跡、GitHub上のブランチ・PRを確認してください。" -f $runExitCode) }
    if (($runOutput -join [Environment]::NewLine) -notmatch '状態: NeedsHumanInput') {
        throw 'Codex E2EがNeedsHumanInputで停止したことを確認できません。成功扱いにせず、証跡を保持しました。'
    }
    $slackSentMatch = [regex]::Match(($runOutput -join [Environment]::NewLine), 'Slack通知送信成功: (\d+)件')
    if (-not $slackSentMatch.Success -or [int]$slackSentMatch.Groups[1].Value -lt 1 -or
        ($runOutput -join [Environment]::NewLine) -match 'Slack通知に\d+件失敗') {
        throw '本番Slackへの通知成功を確認できません。失敗・未設定を成功として扱わず、証跡を保持しました。'
    }

    $testbedTestSource = 'tests\WordCounter.Tests\Program.cs'
    $testExpectationsDiff = Invoke-CapturedNative -FilePath $gitCommand.Source -Arguments @(
        '-C', $checkoutPath, 'diff', '--quiet', $remoteHead, '--', $testbedTestSource
    )
    if ($testExpectationsDiff.ExitCode -ne 0) {
        throw 'Testbedのチェック実装または期待値がmainから変更されています。結果を合格扱いせず、証跡とPRを保持します。'
    }
    $finalBuild = Invoke-CapturedNative -FilePath $dotnetCommand.Source -Arguments @(
        'build', (Join-Path $checkoutPath 'tests\WordCounter.Tests\WordCounter.Tests.csproj'), '--configuration', 'Release', '--nologo'
    )
    $finalBuild.Output | ForEach-Object { Write-Host $_ }
    @("exit code: $($finalBuild.ExitCode)") + $finalBuild.Output | Set-Content -LiteralPath (Join-Path $executionRoot 'testbed-final-build.log') -Encoding UTF8
    if ($finalBuild.ExitCode -ne 0) { throw ("修正後のTestbed Release buildが失敗しました (exit code {0})。E2EはNGです。" -f $finalBuild.ExitCode) }
    $finalChecks = Invoke-CapturedNative -FilePath $dotnetCommand.Source -Arguments @(
        'run', '--project', (Join-Path $checkoutPath 'tests\WordCounter.Tests\WordCounter.Tests.csproj'), '--configuration', 'Release', '--no-build', '--no-restore'
    )
    $finalChecksText = $finalChecks.Output -join [Environment]::NewLine
    $finalChecks.Output | ForEach-Object { Write-Host $_ }
    @("exit code: $($finalChecks.ExitCode)") + $finalChecks.Output | Set-Content -LiteralPath (Join-Path $executionRoot 'testbed-final-checks.log') -Encoding UTF8
    if ($finalChecks.ExitCode -ne 0 -or
        -not $finalChecksText.Contains('All word-count checks passed.') -or
        $finalChecksText -match 'FAIL:') {
        throw '修正後のTestbed 5チェック全件成功を独立確認できません。E2EはNGとし、証跡とPRを保持します。'
    }
    Write-Host '修正後のTestbed Release buildと5チェック全件成功を独立確認しました。テスト期待値はmainから未変更です。'

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
    $reportText = Get-Content -LiteralPath (Join-Path $runDirectory 'report.md') -Raw
    $runEvents = @(Get-Content -LiteralPath (Join-Path $runDirectory 'events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    $implementerCompleted = @($runEvents | Where-Object { $_.eventType -ceq 'agent_call' -and $_.role -ceq 'Implementer' -and $_.result -ceq 'completed' }).Count -gt 0
    $reviewerCompleted = @($runEvents | Where-Object { $_.eventType -ceq 'agent_call' -and $_.role -ceq 'Reviewer' -and $_.result -ceq 'completed' }).Count -gt 0
    $reviewResponses = @(Get-ChildItem -LiteralPath (Join-Path $runDirectory 'exchanges') -Directory -Filter 'reviewer-*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'response.md' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if (-not $implementerCompleted -or -not $reviewerCompleted -or $reviewResponses.Count -lt 1 -or
        $reportText -notmatch '- 状態: `NeedsHumanInput`' -or $reportText -notmatch '- レビュー呼び出し: [1-9]\d*') {
        throw 'Implementer/Reviewerの完了記録、Reviewer応答、またはNeedsHumanInput報告が不足しています。成功扱いせず証跡を保持します。'
    }
    if (-not $pullRequestEvidence.verified -or $pullRequestEvidence.merged -or
        -not [string]::Equals([string]$pullRequestEvidence.repository, $repositoryIdentity, [System.StringComparison]::OrdinalIgnoreCase) -or
        $pullRequestEvidence.baseBranch -cne $defaultBranch -or
        $pullRequestEvidence.branch -eq $defaultBranch -or -not $commentEvidence.verified -or
        $pullRequestEvidence.headSha -ne $commentEvidence.headSha -or $pullRequestEvidence.baseSha -ne $commentEvidence.baseSha) {
        throw 'PRまたはReviewer結果コメントの証跡が不一致です。作業領域と証跡を保持します。'
    }
    $pullRequestState = Get-CheckedNativeOutput -FilePath $ghCommand.Source -Arguments @(
        'pr', 'view', [string]$pullRequestEvidence.url, '--json', 'state,baseRefName,headRefName',
        '--template', '{{.state}}|{{.baseRefName}}|{{.headRefName}}'
    ) -Description 'GitHub上のPR状態確認'
    $pullRequestStateParts = $pullRequestState.Split('|')
    if ($pullRequestStateParts.Count -ne 3 -or $pullRequestStateParts[0] -cne 'OPEN' -or
        $pullRequestStateParts[1] -cne $defaultBranch -or $pullRequestStateParts[2] -cne [string]$pullRequestEvidence.branch) {
        throw 'PRが未マージのOPEN状態でbase/mainを指していることを確認できません。証跡と作業領域を保持します。'
    }

    $deliveryConfirmationText = 'CONFIRM SLACK DELIVERY {0} TO {1}' -f $runDirectories[0].Name, ($productionChannels -join ',')
    $deliveryConfirmation = Read-Host ("Slackの本番通知先でこのrun IDの通知到達を確認後、{0} と入力してください" -f $deliveryConfirmationText)
    if ($deliveryConfirmation -cne $deliveryConfirmationText) {
        throw 'Slack通知の到達を確認できませんでした。E2Eは不合格とし、実行証跡を保持します。'
    }

    Write-Host ''
    Write-Host 'PR作成とReviewer結果コメントの照合を確認しました。PRはOPENのままにし、Testbed mainへマージしません。'
    Write-Host 'Implementer/Reviewer、PR、通知送信の各ゲートは通過しました。Slackの実通知先でrun IDの到達を目視確認しました。'
    Write-Host ("PR: {0}" -f $pullRequestEvidence.url)
    Write-Host ("実行証跡: {0}" -f $runDirectory)
    Write-Host 'リリース判定: E2E合格。ただしPRの人間レビューと明示承認は別途必要です。'
    Write-Host ("一時clone: {0}" -f $executionRoot)
    Write-Host '一時cloneとrun記録は自動削除しません。'
}
catch {
    [Console]::Error.WriteLine('リリース判定: NG（E2Eの全受入条件を確認できませんでした）')
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
