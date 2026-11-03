# Enables the version-controlled hooks in .githooks for this clone.
# Run from the repo root (SqlAccountSync) in PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\scripts\install-git-hooks.ps1

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

if (-not (Test-Path ".git")) {
    Write-Error "Not a git repository root: $root"
}

if (-not (Test-Path ".githooks\pre-commit")) {
    Write-Error "Missing .githooks\pre-commit"
}

git config core.hooksPath .githooks
$hooksPath = (git config --get core.hooksPath)
Write-Host "core.hooksPath=$hooksPath"
Write-Host "Pre-commit secret filename gate is enabled for this clone."
Write-Host "Note: hooks do not run with git commit --no-verify, and do not protect other clones until installed there."
