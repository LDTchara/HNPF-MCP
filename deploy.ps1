# HNPF-MCP one-click deploy: build bridge + connector, copy DLLs into the game.
# Usage:  powershell -ExecutionPolicy Bypass -File deploy.ps1
# After running, restart Hacknet for the new bridge to take effect.

$ErrorActionPreference = "Stop"

$GameDir   = "D:\Game\Hacknet+DLC+Pathfinder"
$BridgeSrc = "$PSScriptRoot\bridge"
$ConnSrc   = "$PSScriptRoot\connector"
$BridgeDst = "$GameDir\BepInEx\plugins"
$ConnDst   = "$GameDir\Extensions\KernelExtensionTEST123123\Plugins"

Write-Host "== HNPF-MCP deploy ==" -ForegroundColor Cyan

# 1. Build
Write-Host "[1/3] Building bridge..." -ForegroundColor Yellow
& dotnet build "$BridgeSrc\HnpfMcpBridge.csproj" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "bridge build failed" }

Write-Host "[2/3] Building connector..." -ForegroundColor Yellow
& dotnet build "$ConnSrc\HnpfMcpConnector.csproj" -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "connector build failed" }

# 2. Copy
Write-Host "[3/3] Copying DLLs..." -ForegroundColor Yellow
Copy-Item "$BridgeSrc\bin\Release\net472\HnpfMcpBridge.dll" "$BridgeDst\" -Force
Copy-Item "$ConnSrc\bin\Release\net472\HnpfMcpConnector.dll" "$ConnDst\" -Force

Write-Host "  bridge    -> $BridgeDst\HnpfMcpBridge.dll" -ForegroundColor Green
Write-Host "  connector -> $ConnDst\HnpfMcpConnector.dll" -ForegroundColor Green
Write-Host "Deploy done. Restart Hacknet (and enter the KE extension) to load the new bridge." -ForegroundColor Cyan
