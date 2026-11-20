$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $repositoryRoot 'scripts\cli-installer\CliRelease.psm1'
Import-Module $modulePath -Force
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ateliers-cli-release-test-' + [Guid]::NewGuid().ToString('N'))

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$ExpectedText)
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Assert-True ($message.Contains($ExpectedText)) "Expected error containing '$ExpectedText', got '$message'."
}

try {
    $beta = Get-CliReleaseTagInfo -Tag 'v0.0.4-beta.7'
    Assert-True $beta.IsPrerelease 'Beta tag was not identified as a prerelease.'
    Assert-True ($beta.Version -ceq '0.0.4-beta.7') 'Beta version normalization failed.'
    Assert-True ($beta.ZipName -ceq 'Ateliers.Ai.Orchestrator-win-x64-v0.0.4-beta.7.zip') 'Beta ZIP name mismatch.'

    $stable = Get-CliReleaseTagInfo -Tag 'v1.2.3'
    Assert-True (-not $stable.IsPrerelease) 'Stable tag was identified as a prerelease.'
    Assert-True ($stable.ZipName -ceq 'Ateliers.Ai.Orchestrator-win-x64-v1.2.3.zip') 'Stable ZIP name mismatch.'
    $stableArguments = @(Get-CliReleaseCreateArguments -Tag 'v1.2.3' -ArtifactDirectory 'artifacts')
    Assert-True ($stableArguments -notcontains '--prerelease') 'Stable release arguments contain the prerelease flag.'
    $betaArguments = @(Get-CliReleaseCreateArguments -Tag 'v0.0.4-beta.7' -ArtifactDirectory 'artifacts')
    Assert-True ($betaArguments -contains '--prerelease') 'Beta release arguments do not contain the prerelease flag.'
    Assert-Throws { Get-CliReleaseTagInfo -Tag 'v1.2' } 'Invalid CLI release tag'
    Assert-Throws { Get-CliReleaseTagInfo -Tag 'v1.2.3-beta..7' } 'Invalid CLI release tag'
    Assert-Throws { Get-CliReleaseTagInfo -Tag 'v1.2.3-01' } 'Invalid CLI release tag'

    $publishDirectory = Join-Path $tempRoot 'publish'
    $artifactDirectory = Join-Path $tempRoot 'artifacts'
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $publishDirectory 'release-manifest.json'), '{"version":"0.0.4-beta.7","runtimeIdentifier":"win-x64"}')
    [System.IO.File]::WriteAllText((Join-Path $publishDirectory 'cli.exe'), 'fixture-binary')
    $assets = New-CliReleaseAssets -Tag 'v0.0.4-beta.7' -PublishDirectory $publishDirectory -ArtifactDirectory $artifactDirectory
    Assert-True (Test-Path -LiteralPath $assets.ZipPath -PathType Leaf) 'Release ZIP was not created.'
    Assert-True (Test-Path -LiteralPath $assets.ChecksumPath -PathType Leaf) 'Release checksum was not created.'
    $checksum = (Get-Content -LiteralPath $assets.ChecksumPath -Raw).Trim()
    Assert-True ($checksum -ceq "$($assets.Sha256)  $($assets.ZipName)") 'Checksum file format mismatch.'
    $actualHash = (Get-FileHash -LiteralPath $assets.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($actualHash -ceq $assets.Sha256) 'Checksum does not match the generated ZIP.'

    $release = [pscustomobject]@{
        tag_name = 'v0.0.4-beta.7'
        prerelease = $true
        draft = $false
        assets = @(
            [pscustomobject]@{ name = $beta.ZipName },
            [pscustomobject]@{ name = $beta.ChecksumName }
        )
    }
    Assert-CliReleaseMetadata -Release $release -Tag 'v0.0.4-beta.7' | Out-Null
    $release.prerelease = $false
    Assert-Throws { Assert-CliReleaseMetadata -Release $release -Tag 'v0.0.4-beta.7' } 'prerelease status does not match'
    $release.prerelease = $true
    $release.assets = @([pscustomobject]@{ name = $beta.ZipName })
    Assert-Throws { Assert-CliReleaseMetadata -Release $release -Tag 'v0.0.4-beta.7' } "missing required asset '$($beta.ChecksumName)'"

    $workflow = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot '.github\workflows\release-cli.yml'))
    Assert-True (-not $workflow.Contains("if: `${{ !contains(github.ref_name, '-') }}")) 'Release workflow still skips prerelease tags.'
    Assert-True ($workflow.Contains('Get-CliReleaseCreateArguments -Tag $env:GITHUB_REF_NAME')) 'Release workflow does not use the tested Release argument builder.'
    $installer = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'scripts\cli-installer\Install-AteliersAiOrchestrator.ps1'))
    Assert-True ($installer.Contains('api.github.com/repos/$repository/releases/tags/$tag')) 'Installer does not check whether the tag has a published Release.'
    Assert-True ($installer.Contains('Assert-CliReleaseMetadata -Release $release -Tag $tag')) 'Installer does not validate Release status and required assets.'

    Write-Output 'CLI release tests passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
    Remove-Module CliRelease -ErrorAction SilentlyContinue
}
