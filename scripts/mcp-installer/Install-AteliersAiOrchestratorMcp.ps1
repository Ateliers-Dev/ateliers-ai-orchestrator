[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparsePointsInPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $currentPath = [System.IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "安全のため、reparse pointを含むパスは使用できません: $currentPath"
            }
        }

        $parentPath = [System.IO.Path]::GetDirectoryName($currentPath)
        if ([string]::IsNullOrWhiteSpace($parentPath) -or [string]::Equals($parentPath, $currentPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $currentPath = $parentPath
    }
}

function Assert-NoReparsePointsInTree {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $directories = [System.Collections.Generic.Stack[string]]::new()
    $directories.Push($Directory)
    while ($directories.Count -gt 0) {
        $currentDirectory = $directories.Pop()
        foreach ($entryPath in [System.IO.Directory]::EnumerateFileSystemEntries($currentDirectory)) {
            $attributes = [System.IO.File]::GetAttributes($entryPath)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "安全のため、MCP配置先にreparse pointが含まれる場合は削除しません: $entryPath"
            }
            if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $directories.Push($entryPath)
            }
        }
    }
}

try {
    $localAppDataValue = $env:LOCALAPPDATA
    $isFullyQualifiedLocalAppData = $localAppDataValue -match '^[A-Za-z]:\\' -or $localAppDataValue -match '^\\\\[^\\]+\\[^\\]+(?:\\|$)'
    if ([string]::IsNullOrWhiteSpace($localAppDataValue) -or -not $isFullyQualifiedLocalAppData) {
        throw 'LOCALAPPDATA は完全修飾されたドライブパスまたはUNCパスで指定してください。'
    }

    $localAppDataDirectory = [System.IO.Path]::GetFullPath($localAppDataValue)
    $localAppDataRoot = [System.IO.Path]::GetPathRoot($localAppDataDirectory)
    if ([string]::Equals($localAppDataDirectory.TrimEnd('\'), $localAppDataRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "LOCALAPPDATA にドライブまたは共有のルートは指定できません: $localAppDataDirectory"
    }
    if (-not $localAppDataDirectory.TrimEnd('\').EndsWith('\AppData\Local', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "LOCALAPPDATA はユーザーの AppData\Local ディレクトリを指定してください: $localAppDataDirectory"
    }
    if (-not (Test-Path -LiteralPath $localAppDataDirectory -PathType Container)) {
        throw "LOCALAPPDATA ディレクトリが存在しません: $localAppDataDirectory"
    }
    Assert-NoReparsePointsInPath -Path $localAppDataDirectory

    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $projectPath = Join-Path $repositoryRoot 'src\Ateliers.Ai.Orchestrator.Mcp\Ateliers.Ai.Orchestrator.Mcp.csproj'
    $installDirectory = [System.IO.Path]::GetFullPath((Join-Path $localAppDataDirectory 'Ateliers-Dev\Ai-Orchestrator\Mcp'))
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "MCPプロジェクトがありません。リポジトリ内のスクリプトから実行してください: $projectPath"
    }

    $publishDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ateliers-ai-orchestrator-mcp-publish-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $publishDirectory | Out-Null

    try {
        Write-Output "MCPをpublishしています: $projectPath"
        & dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish が失敗しました (exit code $LASTEXITCODE)。既存のMCP配置先は変更していません。"
        }

        $publishedExecutable = Join-Path $publishDirectory 'Ateliers.Ai.Orchestrator.Mcp.exe'
        if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
            throw "publish 成果物にMCP実行ファイルがありません。既存のMCP配置先は変更していません: $publishedExecutable"
        }

        if (Test-Path -LiteralPath $installDirectory) {
            Assert-NoReparsePointsInPath -Path $installDirectory
            $installItem = Get-Item -LiteralPath $installDirectory -Force
            if (-not $installItem.PSIsContainer) {
                throw "MCP配置先がディレクトリではありません。既存ファイルは削除していません: $installDirectory"
            }
            Assert-NoReparsePointsInTree -Directory $installDirectory

            try {
                Remove-Item -LiteralPath $installDirectory -Recurse -Force -ErrorAction Stop
            }
            catch {
                throw "既存のMCP配置先を削除できませんでした。MCP等の実行中プロセスを終了してから再実行してください。配置先: $installDirectory。詳細: $($_.Exception.Message)"
            }

            if (Test-Path -LiteralPath $installDirectory) {
                throw "既存のMCP配置先を削除できませんでした。成功扱いにせず中止します: $installDirectory"
            }
        }

        New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $installDirectory -Recurse -Force

        $installedExecutable = Join-Path $installDirectory 'Ateliers.Ai.Orchestrator.Mcp.exe'
        if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
            throw "配置後の確認に失敗しました。MCP実行ファイルがありません: $installedExecutable"
        }

        Write-Output "MCPを配置しました: $installedExecutable"
    }
    finally {
        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
catch {
    [Console]::Error.WriteLine("MCPインストールに失敗しました: $($_.Exception.Message)")
    exit 1
}
