[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -or -not [System.IO.Path]::IsPathRooted($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA が設定されていないか、絶対パスではありません。Windowsのユーザー環境で実行してください。'
    }

    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $projectPath = Join-Path $repositoryRoot 'src\Ateliers.Ai.Orchestrator.Mcp\Ateliers.Ai.Orchestrator.Mcp.csproj'
    $installDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Ateliers-Dev\Ai-Orchestrator\Mcp'))
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
            $installItem = Get-Item -LiteralPath $installDirectory -Force
            if (-not $installItem.PSIsContainer) {
                throw "MCP配置先がディレクトリではありません。既存ファイルは削除していません: $installDirectory"
            }

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
