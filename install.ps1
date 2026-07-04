#!/usr/bin/env pwsh
# Install (or reinstall) cs4ai as a global dotnet tool from a freshly built local pack.
# Idempotent: uninstalls any prior global install first, then installs from bin\<config>.
#
# Usage:
#   .\install.ps1                    # Release (default)
#   .\install.ps1 -Configuration Debug

[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo      = $PSScriptRoot
$project   = Join-Path $repo 'src\erikphilips.cs4ai\ErikPhilips.cs4ai.csproj'
$packageId = 'ErikPhilips.Cs4Ai'
$packDir   = Join-Path $repo "src\erikphilips.cs4ai\bin\$Configuration"

Write-Host "==> Building $packageId ($Configuration)" -ForegroundColor Cyan
dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "==> Packing $packageId ($Configuration)" -ForegroundColor Cyan
dotnet pack $project -c $Configuration --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

Write-Host "==> Stopping any running cs4ai daemons" -ForegroundColor Cyan
# Running daemons hold the tool-store files and block uninstall. Staged sessions (if any)
# die with the daemon — same loss semantics as idle expiry; nothing on disk is touched.
Get-Process cs4ai -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host "==> Removing previous global install (if any)" -ForegroundColor Cyan
$installed = (dotnet tool list -g) -match "^\s*$([regex]::Escape($packageId.ToLower()))\s"
if ($installed) {
  dotnet tool uninstall -g $packageId | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "uninstall failed" }
} else {
  Write-Host "    (not installed)"
}

Write-Host "==> Installing from $packDir" -ForegroundColor Cyan
dotnet tool install -g $packageId --add-source $packDir
if ($LASTEXITCODE -ne 0) { throw "install failed" }

Write-Host ""
Write-Host "==> Installed:" -ForegroundColor Green
cs4ai --version

# Refresh the Claude Code skill so the SKILL.md stays in sync with the binary.
# User-level lives under ~/.claude/skills; the project-level copy stays in the repo
# for in-repo sessions. Both are pure re-emits from the embedded const string — safe to
# run every time.
$userSkills = Join-Path $HOME '.claude\skills'
$repoSkills = Join-Path $repo '.claude\skills'
Write-Host "==> Refreshing skills" -ForegroundColor Cyan
cs4ai --create-skill $userSkills | Out-Host
cs4ai --create-skill $repoSkills | Out-Host
