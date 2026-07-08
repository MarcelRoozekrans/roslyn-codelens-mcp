#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unlists ("delists") every prerelease of RoslynCodeLens.Mcp from NuGet.org.

.DESCRIPTION
    NuGet.org does NOT support hard deletes. `dotnet nuget delete` against
    nuget.org UNLISTS the version: it is hidden from search and the version
    list, but remains restorable by exact version. This is effectively
    permanent and is a public-feed change — review the printed list first.

    Fetches the live version index, selects every prerelease (any version
    containing '-'), prints them, then unlists each one.

.PREREQUISITES
    - .NET SDK on PATH (`dotnet`)
    - $env:NUGET_API_KEY set to an API key with the "Unlist package" scope
      for RoslynCodeLens.Mcp. The key is read from the environment and never
      printed.

.EXAMPLE
    $env:NUGET_API_KEY = "oy2..."          # do not commit this
    ./scripts/delist-prereleases.ps1        # dry run: lists what would be delisted
    ./scripts/delist-prereleases.ps1 -Execute
#>
[CmdletBinding()]
param(
    [string]$PackageId = "RoslynCodeLens.Mcp",
    [string]$Source    = "https://api.nuget.org/v3/index.json",
    # Safety: nothing is delisted unless -Execute is passed.
    [switch]$Execute
)

$ErrorActionPreference = "Stop"

if (-not $env:NUGET_API_KEY) {
    Write-Error "NUGET_API_KEY environment variable is not set. Set it to a key with the 'Unlist package' scope."
    exit 1
}

$lower = $PackageId.ToLowerInvariant()
$indexUrl = "https://api.nuget.org/v3-flatcontainer/$lower/index.json"
Write-Host "Fetching versions from $indexUrl ..."
$all = (Invoke-RestMethod -Uri $indexUrl).versions

# A prerelease is any SemVer with a pre-release label (contains '-').
$prereleases = @($all | Where-Object { $_ -like "*-*" } | Sort-Object)

if ($prereleases.Count -eq 0) {
    Write-Host "No prereleases found. Nothing to do."
    exit 0
}

Write-Host ""
Write-Host "$($prereleases.Count) prerelease version(s) of $PackageId to delist:" -ForegroundColor Yellow
$prereleases | ForEach-Object { Write-Host "  $_" }
Write-Host ""

if (-not $Execute) {
    Write-Host "DRY RUN. Re-run with -Execute to actually unlist these versions." -ForegroundColor Cyan
    exit 0
}

$failed = @()
foreach ($v in $prereleases) {
    Write-Host "Delisting $PackageId $v ..." -NoNewline
    # --non-interactive: unlist on nuget.org without the confirm prompt.
    dotnet nuget delete $PackageId $v `
        --source $Source `
        --api-key $env:NUGET_API_KEY `
        --non-interactive
    if ($LASTEXITCODE -eq 0) {
        Write-Host " done" -ForegroundColor Green
    } else {
        Write-Host " FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        $failed += $v
    }
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "All $($prereleases.Count) prerelease(s) delisted." -ForegroundColor Green
} else {
    Write-Host "$($failed.Count) failed:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  $_" }
    exit 1
}
