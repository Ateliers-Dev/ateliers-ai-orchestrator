[CmdletBinding()]
param(
    [switch]$PreserveExisting
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-FileContentMatches {
    param(
        [Parameter(Mandatory = $true)][string]$ReferencePath,
        [Parameter(Mandatory = $true)][string]$CandidatePath
    )

    $reference = Get-Item -LiteralPath $ReferencePath
    $candidate = Get-Item -LiteralPath $CandidatePath
    if ($reference.Length -ne $candidate.Length) {
        throw 'ファイルコピーのサイズ確認に失敗しました。'
    }
    $referenceHash = (Get-FileHash -LiteralPath $ReferencePath -Algorithm SHA256).Hash
    $candidateHash = (Get-FileHash -LiteralPath $CandidatePath -Algorithm SHA256).Hash
    if ($referenceHash -cne $candidateHash) {
        throw 'ファイルコピーの内容確認に失敗しました。'
    }
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or -not [System.IO.Path]::IsPathRooted($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA が設定されていないか、絶対パスではありません。Windowsのユーザー環境で実行してください。'
}

$sourceDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\config'))
$destinationDirectory = Join-Path $env:LOCALAPPDATA 'Ateliers-Dev\Ai-Orchestrator\config'
$fileNames = @('slack.json', 'slack.secrets.json')
$availableFiles = @($fileNames | Where-Object {
    Test-Path -LiteralPath (Join-Path $sourceDirectory $_) -PathType Leaf
})

if ($availableFiles.Count -eq 0) {
    Write-Output '配置元にSlack設定ファイルがないため、既存の設定を維持しました。'
    return
}

New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
$operationId = [Guid]::NewGuid().ToString('N')
$stagingDirectory = Join-Path $destinationDirectory ('.staging-' + $operationId)
$existingFiles = @($availableFiles | Where-Object {
    Test-Path -LiteralPath (Join-Path $destinationDirectory $_) -PathType Leaf
})
$backupDirectory = $null
$backupBase = $null
$completedBackupFiles = @{}
$temporaryFiles = @()
$replacementStarted = $false
$rollbackFailed = $false

if (-not $PreserveExisting -and $existingFiles.Count -gt 0) {
    $timestamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
    $backupBase = Join-Path (Join-Path $destinationDirectory 'backups') $timestamp
}

try {
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    foreach ($fileName in $availableFiles) {
        Copy-Item -LiteralPath (Join-Path $sourceDirectory $fileName) -Destination (Join-Path $stagingDirectory $fileName)
    }

    if ($backupBase) {
        $backupRoot = Split-Path -Parent $backupBase
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $suffix = 0
        while ($true) {
            $candidate = if ($suffix -eq 0) { $backupBase } else { '{0}-{1:D3}' -f $backupBase, $suffix }
            if (Test-Path -LiteralPath $candidate) { $suffix++; continue }
            try {
                New-Item -ItemType Directory -Path $candidate -ErrorAction Stop | Out-Null
                $backupDirectory = $candidate
                break
            }
            catch {
                if (Test-Path -LiteralPath $candidate) { $suffix++; continue }
                throw
            }
        }
        foreach ($fileName in $existingFiles) {
            $sourcePath = Join-Path $destinationDirectory $fileName
            $backupPath = Join-Path $backupDirectory $fileName
            $pendingBackupPath = Join-Path $backupDirectory ('.{0}.{1}.pending' -f $fileName, $operationId)
            $temporaryFiles += $pendingBackupPath
            Copy-Item -LiteralPath $sourcePath -Destination $pendingBackupPath
            Assert-FileContentMatches -ReferencePath $sourcePath -CandidatePath $pendingBackupPath
            Move-Item -LiteralPath $pendingBackupPath -Destination $backupPath
            $completedBackupFiles[$fileName] = $true
        }
    }

    $replacementStarted = $true
    foreach ($fileName in $availableFiles) {
        $destinationPath = Join-Path $destinationDirectory $fileName
        if ($PreserveExisting -and (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
            Write-Output ("既存設定を維持しました: {0}" -f $fileName)
            continue
        }
        Move-Item -LiteralPath (Join-Path $stagingDirectory $fileName) -Destination $destinationPath -Force
        Write-Output ("設定ファイルを配置しました: {0}" -f $fileName)
    }
}
catch {
    if ($replacementStarted) {
        foreach ($fileName in $availableFiles) {
            $destinationPath = Join-Path $destinationDirectory $fileName
            if ($completedBackupFiles.ContainsKey($fileName)) {
                $backupPath = Join-Path $backupDirectory $fileName
                $restorePath = Join-Path $destinationDirectory ('.{0}.{1}.restore' -f $fileName, $operationId)
                $temporaryFiles += $restorePath
                try {
                    Copy-Item -LiteralPath $backupPath -Destination $restorePath
                    Assert-FileContentMatches -ReferencePath $backupPath -CandidatePath $restorePath
                    Move-Item -LiteralPath $restorePath -Destination $destinationPath -Force
                }
                catch {
                    $rollbackFailed = $true
                }
            }
            elseif ($fileName -notin $existingFiles -and (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
                Remove-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
    $backupNote = if ($backupDirectory -and -not $replacementStarted) {
        " バックアップ未完了のため元設定は変更されていません。途中のフォルダーを保持しました: $backupDirectory"
    }
    elseif ($backupDirectory) {
        " 検証済みバックアップフォルダーを保持しました: $backupDirectory"
    }
    else { '' }
    $rollbackNote = if ($rollbackFailed) { ' 元設定の復旧に失敗しました。検証済みバックアップを確認してください。' } else { '' }
    throw ("Slack設定ファイルの更新に失敗しました。設定値は表示していません。{0}{1}" -f $backupNote, $rollbackNote)
}
finally {
    foreach ($temporaryPath in $temporaryFiles) {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($backupDirectory) {
    Write-Output ("既存設定のバックアップ: {0}" -f $backupDirectory)
}

