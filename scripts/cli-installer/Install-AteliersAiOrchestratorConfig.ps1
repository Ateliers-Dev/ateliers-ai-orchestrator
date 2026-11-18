[CmdletBinding()]
param(
    [switch]$PreserveExisting
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
            Copy-Item -LiteralPath (Join-Path $destinationDirectory $fileName) -Destination (Join-Path $backupDirectory $fileName)
        }
    }

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
    foreach ($fileName in $availableFiles) {
        $destinationPath = Join-Path $destinationDirectory $fileName
        $backupPath = if ($backupDirectory) { Join-Path $backupDirectory $fileName } else { $null }
        if ($backupPath -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            Copy-Item -LiteralPath $backupPath -Destination $destinationPath -Force -ErrorAction SilentlyContinue
        }
        elseif ($fileName -notin $existingFiles -and (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
            Remove-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue
        }
    }
    $backupNote = if ($backupDirectory) { " バックアップを保持しました: $backupDirectory" } else { '' }
    throw ("Slack設定ファイルの更新に失敗しました。設定値は表示していません。{0}" -f $backupNote)
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($backupDirectory) {
    Write-Output ("既存設定のバックアップ: {0}" -f $backupDirectory)
}
