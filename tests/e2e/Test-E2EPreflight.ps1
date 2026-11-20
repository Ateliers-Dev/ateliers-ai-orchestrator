$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$scriptPath = Join-Path $repositoryRoot 'scripts\e2e\run-codex-e2e.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "E2Eスクリプトの構文エラー: $($parseErrors[0].Message)" }

foreach ($functionName in @('Resolve-CodexExecutable', 'Get-StructuredRunResult', 'Resolve-E2ESlackSettings')) {
    $functionAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName }, $true)
    if ($null -eq $functionAst) { throw "必要な関数がありません: $functionName" }
    Invoke-Expression $functionAst.Extent.Text
}

$source = Get-Content -LiteralPath $scriptPath -Raw
if ($source -match '\bRead-Host\b') { throw '非対話E2EスクリプトにRead-Hostが残っています。' }
if ($source -notmatch "ValidateSet\('test'\)") { throw 'Slack通知はtestChannelId固定で必須にしてください。' }
if ($source.IndexOf('$slackConfiguration = Resolve-E2ESlackSettings', [StringComparison]::Ordinal) -gt
    $source.IndexOf('$runStarted = $true', [StringComparison]::Ordinal)) { throw 'Slack事前確認より先に外部実行が開始されます。' }
if ($source.IndexOf("'verify-slack'", [StringComparison]::Ordinal) -gt $source.IndexOf('$runStarted = $true', [StringComparison]::Ordinal) -or
    $source.IndexOf("'GitHub push権限の事前確認'", [StringComparison]::Ordinal) -gt $source.IndexOf('$runStarted = $true', [StringComparison]::Ordinal)) {
    throw 'SlackまたはGitHub push権限を確認する前にCodexを起動します。'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AteliersE2EPreflightTest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$originalPath = $env:PATH
$originalCodexPath = $env:ATELIERS_CODEX_CLI_PATH
try {
    $exePath = Join-Path $tempRoot 'codex.exe'
    $ps1Path = Join-Path $tempRoot 'codex.ps1'
    $cmdPath = Join-Path $tempRoot 'codex.cmd'
    foreach ($path in @($exePath, $ps1Path, $cmdPath)) { [System.IO.File]::WriteAllText($path, '') }

    if ((Resolve-CodexExecutable -ExplicitPath $ps1Path) -cne $ps1Path) { throw '明示したcodex.ps1の優先解決に失敗しました。' }
    $env:ATELIERS_CODEX_CLI_PATH = $cmdPath
    if ((Resolve-CodexExecutable -ExplicitPath '') -cne $cmdPath) { throw '環境変数codex.cmdの解決に失敗しました。' }
    Remove-Item Env:\ATELIERS_CODEX_CLI_PATH -ErrorAction SilentlyContinue

    $env:PATH = $tempRoot + [System.IO.Path]::PathSeparator + $originalPath
    $resolvedFromPath = Resolve-CodexExecutable -ExplicitPath ''
    if ([System.IO.Path]::GetFileName($resolvedFromPath) -cne 'codex.exe') { throw 'PATH上のCodex CLI解決に失敗しました。' }

    $configPath = Join-Path $tempRoot 'slack.json'
    $secretsPath = Join-Path $tempRoot 'slack.secrets.json'
    $settings = @{
        testChannelId = 'C_TEST'
        bots = @(
            @{ id = 'orchestrator'; enabled = $true; channelId = 'C_ORCH' }
            @{ id = 'implementer'; enabled = $true; channelId = 'C_IMPL' }
            @{ id = 'reviewer'; enabled = $true; channelId = 'C_REVIEW' }
        )
    }
    $secrets = @{ tokens = @{ orchestrator = 'test'; implementer = 'test'; reviewer = 'test' } }
    [System.IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json -Depth 8))
    [System.IO.File]::WriteAllText($secretsPath, ($secrets | ConvertTo-Json -Depth 8))
    $slack = Resolve-E2ESlackSettings -ConfigurationPath $configPath -SecretsPath $secretsPath -RepositoryRoot $tempRoot
    if ($slack.TestChannelId -cne 'C_TEST' -or $slack.PSObject.Properties.Name -contains 'Token') { throw 'Slack設定からtestChannelId固定の宛先を解決できません。' }
    $settings.testChannelId = 'C_REPLACE_ME'
    [System.IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json -Depth 8))
    $missingTestChannelRejected = $false
    try { $null = Resolve-E2ESlackSettings -ConfigurationPath $configPath -SecretsPath $secretsPath -RepositoryRoot $tempRoot }
    catch { $missingTestChannelRejected = $true }
    if (-not $missingTestChannelRejected) { throw '未設定のSlack testChannelIdを拒否しませんでした。' }
    $settings.testChannelId = 'C_TEST'
    [System.IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json -Depth 8))
    $secrets.tokens.reviewer = ''
    [System.IO.File]::WriteAllText($secretsPath, ($secrets | ConvertTo-Json -Depth 8))
    $missingBotAuthRejected = $false
    try { $null = Resolve-E2ESlackSettings -ConfigurationPath $configPath -SecretsPath $secretsPath -RepositoryRoot $tempRoot }
    catch { $missingBotAuthRejected = $true }
    if (-not $missingBotAuthRejected) { throw 'Slack Bot認証不足を拒否しませんでした。' }
    $missingFilesRejected = $false
    try { $null = Resolve-E2ESlackSettings -ConfigurationPath (Join-Path $tempRoot 'missing.json') -SecretsPath $secretsPath -RepositoryRoot $tempRoot }
    catch { $missingFilesRejected = $true }
    if (-not $missingFilesRejected) { throw 'Slack設定不足を拒否しませんでした。' }

    $parsed = Get-StructuredRunResult -Output @('diagnostic line', '{"protocolVersion":1,"ok":true,"exitCode":0,"runId":"run-1","state":"NeedsHumanInput","slack":{"sent":3,"failed":0}}')
    if ($null -eq $parsed -or $parsed.runId -cne 'run-1' -or [int]$parsed.slack.sent -ne 3) { throw '機械可読なrun結果の解析に失敗しました。' }
    if ($null -ne (Get-StructuredRunResult -Output @('not-json'))) { throw '不正なrun結果を有効として扱いました。' }

    Write-Host 'E2E preflight deterministic tests passed.'
}
finally {
    $env:PATH = $originalPath
    if ($null -eq $originalCodexPath) { Remove-Item Env:\ATELIERS_CODEX_CLI_PATH -ErrorAction SilentlyContinue }
    else { $env:ATELIERS_CODEX_CLI_PATH = $originalCodexPath }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
