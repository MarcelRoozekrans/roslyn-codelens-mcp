$ErrorActionPreference = "Stop"
$ToolName = "roslyn-codegraph-mcp"

$installed = dotnet tool list -g | Select-String $ToolName
if (-not $installed) {
    Write-Host "[roslyn-codegraph] Installing $ToolName dotnet global tool..." -ForegroundColor Yellow
    dotnet tool install -g $ToolName
}

& $ToolName @args
