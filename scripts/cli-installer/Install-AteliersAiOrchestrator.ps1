[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')]
    [ValidatePattern('^v?\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = 'Ateliers-Dev/ateliers-ai-orchestrator'
$assetNamePrefix = 'Ateliers.Ai.Orchestrator-win-x64'
$installRootName = 'Ateliers-Dev\Ai-Orchestrator'

function Get-InstalledVersion {
    param([Parameter(Mandatory = $true)][string]$BinaryDirectory)

    $executable = Join-Path $BinaryDirectory 'Ateliers.Ai.Orchestrator.Cli.exe'
    $releaseManifest = Join-Path $BinaryDirectory 'release-manifest.json'
    $installManifest = Join-Path $BinaryDirectory 'install.json'
    foreach ($path in @($executable, $releaseManifest, $installManifest)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "インストールが不完全です。必要なファイルがありません: $path"
        }
    }

    try {
        $release = Get-Content -LiteralPath $releaseManifest -Raw | ConvertFrom-Json
        $installed = Get-Content -LiteralPath $installManifest -Raw | ConvertFrom-Json
    }
    catch {
        throw "インストール情報を読み取れません: $($_.Exception.Message)"
    }

    if ($release.version -ne $installed.version) {
        throw "インストール情報のバージョンが一致しません (release=$($release.version), install=$($installed.version))。"
    }

    $reportedVersion = (& $executable --version 2>&1 | Out-String).Trim()
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "CLIのバージョン確認に失敗しました (exit code $exitCode): $reportedVersion"
    }
    if ($reportedVersion -ne $release.version) {
        throw "配置されたCLIとRelease情報のバージョンが一致しません (CLI=$reportedVersion, release=$($release.version))。"
    }
    return $reportedVersion
}

try {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or -not [System.IO.Path]::IsPathRooted($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA が設定されていないか、絶対パスではありません。Windowsのユーザー環境で実行してください。'
    }

    $installRoot = Join-Path $env:LOCALAPPDATA $installRootName
    $binaryDirectory = Join-Path $installRoot 'bin'
    $executable = Join-Path $binaryDirectory 'Ateliers.Ai.Orchestrator.Cli.exe'

    if ($Verify) {
        $installedVersion = Get-InstalledVersion -BinaryDirectory $binaryDirectory
        Write-Output "配置先: $executable"
        Write-Output "バージョン: $installedVersion"
        exit 0
    }

    $normalizedVersion = $Version.TrimStart('v')
    $tag = "v$normalizedVersion"
    $assetName = "$assetNamePrefix-v$normalizedVersion.zip"
    $releaseUrl = "https://github.com/$repository/releases/download/$tag"
    $workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ateliers-orchestrator-install-" + [Guid]::NewGuid().ToString('N'))
    $stageDirectory = Join-Path $installRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
    $backupDirectory = Join-Path $installRoot ('.backup-' + [Guid]::NewGuid().ToString('N'))
    $hadPreviousInstall = Test-Path -LiteralPath $binaryDirectory -PathType Container
    $switched = $false

    try {
        New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
        New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
        $archivePath = Join-Path $workDirectory $assetName
        $checksumPath = "$archivePath.sha256"
        Invoke-WebRequest -Uri "$releaseUrl/$assetName" -OutFile $archivePath -UseBasicParsing
        Invoke-WebRequest -Uri "$releaseUrl/$assetName.sha256" -OutFile $checksumPath -UseBasicParsing

        $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
        if ($checksumText -notmatch '^([0-9a-fA-F]{64})(?:\s+\*?.+)?$') {
            throw 'ReleaseのSHA-256ファイル形式が不正です。'
        }
        $expectedHash = $Matches[1].ToUpperInvariant()
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "Release ZIPのSHA-256が一致しません (expected=$expectedHash, actual=$actualHash)。"
        }

        New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $stageDirectory -Force
        $releaseManifestPath = Join-Path $stageDirectory 'release-manifest.json'
        if (-not (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf)) {
            throw 'Release ZIPに release-manifest.json がありません。'
        }
        $releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
        if ($releaseManifest.version -ne $normalizedVersion -or $releaseManifest.runtimeIdentifier -ne 'win-x64') {
            throw "Release ZIPのバージョンまたは対象OSが一致しません (version=$($releaseManifest.version), rid=$($releaseManifest.runtimeIdentifier))。"
        }
        $packageExecutable = Join-Path $stageDirectory 'Ateliers.Ai.Orchestrator.Cli.exe'
        if (-not (Test-Path -LiteralPath $packageExecutable -PathType Leaf)) {
            throw "Release ZIPにCLI実行ファイルがありません: $packageExecutable"
        }
        $installManifest = [ordered]@{
            version = $normalizedVersion
            runtimeIdentifier = 'win-x64'
            releaseTag = $tag
            releaseUrl = $releaseUrl
            installedAtUtc = [DateTime]::UtcNow.ToString('o')
        }
        $installManifestJson = $installManifest | ConvertTo-Json
        $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText(
            (Join-Path $stageDirectory 'install.json'),
            $installManifestJson + [Environment]::NewLine,
            $utf8WithoutBom)
        $packageVersion = Get-InstalledVersion -BinaryDirectory $stageDirectory
        if ($packageVersion -ne $normalizedVersion) {
            throw "Release ZIPのCLIバージョンが指定と一致しません (requested=$normalizedVersion, actual=$packageVersion)。"
        }

        if ($hadPreviousInstall) {
            Move-Item -LiteralPath $binaryDirectory -Destination $backupDirectory
        }
        try {
            Move-Item -LiteralPath $stageDirectory -Destination $binaryDirectory
            $switched = $true
            $installedVersion = Get-InstalledVersion -BinaryDirectory $binaryDirectory
            if ($installedVersion -ne $normalizedVersion) {
                throw "インストール後の確認でバージョンが一致しません (requested=$normalizedVersion, actual=$installedVersion)。"
            }
        }
        catch {
            $failedDirectory = $null
            if ($switched -and (Test-Path -LiteralPath $binaryDirectory)) {
                $failedDirectory = Join-Path $installRoot ('.failed-' + [Guid]::NewGuid().ToString('N'))
                Move-Item -LiteralPath $binaryDirectory -Destination $failedDirectory
            }
            if ($hadPreviousInstall -and (Test-Path -LiteralPath $backupDirectory)) {
                Move-Item -LiteralPath $backupDirectory -Destination $binaryDirectory
            }
            if ($failedDirectory -and (Test-Path -LiteralPath $failedDirectory)) {
                Remove-Item -LiteralPath $failedDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
            throw
        }

        if ($hadPreviousInstall) {
            Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        }
        Write-Output "配置先: $executable"
        Write-Output "バージョン: $installedVersion"

    $configInstaller = Join-Path $PSScriptRoot 'Install-AteliersAiOrchestratorConfig.ps1'
    if (-not (Test-Path -LiteralPath $configInstaller -PathType Leaf)) {
        throw 'Slack設定インストーラーが見つかりません。CLIは配置されましたが設定配置を確認できません。'
    }
    & $configInstaller -PreserveExisting
    }
    finally {
        if (Test-Path -LiteralPath $stageDirectory) {
            Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $workDirectory) {
            Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
catch {
    [Console]::Error.WriteLine("インストール確認に失敗しました: $($_.Exception.Message)")
    exit 1
}
