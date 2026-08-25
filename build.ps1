# ACE build/test/publish pipeline (SRS §14: win-x64 self-contained distribution).
#
#   pwsh ./build.ps1             # build + test + publish win-x64
#   pwsh ./build.ps1 -SkipTests  # build + publish only
#
# Outputs:
#   dist\win-x64\Ace.Mcp.Server.exe  (self-contained single-file MCP server)
#   dist\win-x64\ace.exe             (self-contained single-file CLI)
#   plugin\qoder\dist\win-x64\*      (in-package copy of the MCP server so the
#                                     Qoder plugin folder is self-contained)

param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    Write-Host '==> dotnet build (Release)' -ForegroundColor Cyan
    dotnet build Ace.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }

    if (-not $SkipTests) {
        Write-Host '==> dotnet test (Release)' -ForegroundColor Cyan
        dotnet test Ace.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (exit code $LASTEXITCODE)." }
    }

    Write-Host '==> dotnet publish Ace.Mcp.Server (win-x64, self-contained, single file)' -ForegroundColor Cyan
    dotnet publish src\Ace.Mcp.Server\Ace.Mcp.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\win-x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish Ace.Mcp.Server failed (exit code $LASTEXITCODE)." }

    # Stage the published MCP server (plus any required sidecars) inside the Qoder
    # plugin package so plugin\qoder alone can spawn the server (.mcp.json points at
    # the in-package path dist/win-x64/Ace.Mcp.Server.exe).
    Write-Host '==> staging MCP server into plugin\qoder\dist\win-x64 (self-contained plugin)' -ForegroundColor Cyan
    $pluginDist = Join-Path (Join-Path (Join-Path 'plugin' 'qoder') 'dist') 'win-x64'
    if (Test-Path $pluginDist) {
        Remove-Item $pluginDist -Recurse -Force
    }
    New-Item -ItemType Directory -Path $pluginDist -Force | Out-Null
    Get-ChildItem (Join-Path 'dist' 'win-x64') -File |
        Where-Object { $_.Name -notin 'ace.exe', 'ace.pdb' -and $_.Name -notlike 'Ace.Cli*' -and $_.Extension -ne '.pdb' } |
        Copy-Item -Destination $pluginDist
    $pluginServer = Join-Path $pluginDist 'Ace.Mcp.Server.exe'
    if (-not (Test-Path $pluginServer)) { throw "Plugin staging failed: $pluginServer was not created." }

    Write-Host '==> creating plugin zip (plugin\ace-qoder.zip)' -ForegroundColor Cyan
    $zipPath = Join-Path 'plugin' 'ace-qoder.zip'
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    # Zip the CONTENTS of plugin\qoder (not the folder itself) so the archive root
    # contains .qoder-plugin/plugin.json directly. A wrapping top-level "qoder\"
    # folder makes Qoder's installer fail to locate the plugin manifest.
    Get-ChildItem (Join-Path 'plugin' 'qoder') -Force |
        Compress-Archive -DestinationPath $zipPath
    Write-Host "    -> $zipPath ($('{0:N0}' -f (Get-Item $zipPath).Length) bytes)" -ForegroundColor Green

    Write-Host '==> dotnet publish Ace.Cli as ace.exe (win-x64, self-contained, single file)' -ForegroundColor Cyan
    dotnet publish src\Ace.Cli\Ace.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\win-x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish Ace.Cli failed (exit code $LASTEXITCODE)." }

    Write-Host '==> Publish complete:' -ForegroundColor Green
    Get-ChildItem dist\win-x64 | Format-Table Name, Length, LastWriteTime
    Write-Host '==> Plugin package contents (plugin\qoder\dist\win-x64):' -ForegroundColor Green
    Get-ChildItem $pluginDist | Format-Table Name, Length, LastWriteTime
}
finally {
    Pop-Location
}
