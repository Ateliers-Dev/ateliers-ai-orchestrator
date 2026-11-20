Set-StrictMode -Version Latest

function Get-CliReleaseTagInfo {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Tag)

    if ($Tag -notmatch '^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?$') {
        throw "Invalid CLI release tag: $Tag"
    }

    $version = $Tag.TrimStart('v')
    $assetBaseName = "Ateliers.Ai.Orchestrator-win-x64-v$version.zip"
    [pscustomobject]@{
        Tag = "v$version"
        Version = $version
        IsPrerelease = $version.Contains('-')
        ZipName = $assetBaseName
        ChecksumName = "$assetBaseName.sha256"
    }
}

function New-CliReleaseAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory
    )

    $releaseInfo = Get-CliReleaseTagInfo -Tag $Tag
    if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
        throw "Publish directory was not found: $PublishDirectory"
    }
    $publishFiles = @(Get-ChildItem -LiteralPath $PublishDirectory -File)
    if ($publishFiles.Count -eq 0) {
        throw "Publish directory is empty: $PublishDirectory"
    }

    New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
    $zipPath = Join-Path $ArtifactDirectory $releaseInfo.ZipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $checksumPath = Join-Path $ArtifactDirectory $releaseInfo.ChecksumName
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($checksumPath, "$hash  $($releaseInfo.ZipName)$([Environment]::NewLine)", [System.Text.Encoding]::ASCII)

    [pscustomobject]@{
        Tag = $releaseInfo.Tag
        Version = $releaseInfo.Version
        IsPrerelease = $releaseInfo.IsPrerelease
        ZipPath = $zipPath
        ChecksumPath = $checksumPath
        ZipName = $releaseInfo.ZipName
        ChecksumName = $releaseInfo.ChecksumName
        Sha256 = $hash
    }
}

function Assert-CliReleaseMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Release,
        [Parameter(Mandatory = $true)][string]$Tag
    )

    $releaseInfo = Get-CliReleaseTagInfo -Tag $Tag
    if ($Release.draft) { throw "CLI release $($releaseInfo.Tag) is still a draft." }
    if ($Release.tag_name -cne $releaseInfo.Tag) {
        throw "GitHub returned release tag '$($Release.tag_name)' for requested tag '$($releaseInfo.Tag)'."
    }
    if ([bool]$Release.prerelease -ne $releaseInfo.IsPrerelease) {
        throw "GitHub release prerelease status does not match tag '$($releaseInfo.Tag)'."
    }

    $assetNames = @($Release.assets | ForEach-Object { [string]$_.name })
    foreach ($requiredAsset in @($releaseInfo.ZipName, $releaseInfo.ChecksumName)) {
        if ($assetNames -cnotcontains $requiredAsset) {
            throw "GitHub release '$($releaseInfo.Tag)' is missing required asset '$requiredAsset'."
        }
    }
    return $releaseInfo
}

function Get-CliReleaseCreateArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory
    )

    $releaseInfo = Get-CliReleaseTagInfo -Tag $Tag
    $arguments = @(
        'release', 'create', $releaseInfo.Tag,
        (Join-Path $ArtifactDirectory $releaseInfo.ZipName),
        (Join-Path $ArtifactDirectory $releaseInfo.ChecksumName),
        '--title', $releaseInfo.Tag, '--generate-notes'
    )
    if ($releaseInfo.IsPrerelease) { $arguments += '--prerelease' }
    return $arguments
}

Export-ModuleMember -Function Get-CliReleaseTagInfo, New-CliReleaseAssets, Assert-CliReleaseMetadata, Get-CliReleaseCreateArguments
