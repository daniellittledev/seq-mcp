#!/usr/bin/env pwsh
# Builds the Claude Desktop extension (.mcpb) for seq-mcp.
#
# Publishes a self-contained, single-file Windows executable (no .NET runtime
# required on the host), stages it next to the manifest, validates, and packs
# the bundle to dist/seq-mcp.mcpb.
#
# Pass -Version to stamp the manifest and executable (CI derives this from the
# release tag); omit it to build with the version already in mcpb/manifest.json.

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/SeqMcp/SeqMcp.fsproj'
$serverDir = Join-Path $root 'mcpb/server'
$manifest = Join-Path $root 'mcpb/manifest.json'
$distDir = Join-Path $root 'dist'
$output = Join-Path $distDir 'seq-mcp.mcpb'

# 0. Stamp the manifest version when a version is supplied (e.g. from a tag).
if ($Version) {
    Write-Host "Stamping manifest version $Version..." -ForegroundColor Cyan
    $text = Get-Content $manifest -Raw
    $text = [regex]::Replace($text, '(?m)^(\s*"version":\s*")[^"]*(")', "`${1}$Version`${2}")
    Set-Content $manifest -Value $text -NoNewline
}

# 1. Clean and publish a self-contained single-file executable into mcpb/server.
if (Test-Path $serverDir) { Remove-Item $serverDir -Recurse -Force }
New-Item -ItemType Directory -Path $serverDir -Force | Out-Null

Write-Host "Publishing $Runtime self-contained build..." -ForegroundColor Cyan
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $serverDir
)
if ($Version) { $publishArgs += "-p:Version=$Version" }
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $serverDir 'SeqMcp.exe'
if (-not (Test-Path $exe)) { throw "Expected executable not found: $exe" }

# 2. Validate the manifest, then pack the bundle.
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Host "Validating manifest..." -ForegroundColor Cyan
npx --yes '@anthropic-ai/mcpb@2.1.2' validate $manifest
if ($LASTEXITCODE -ne 0) { throw "manifest validation failed ($LASTEXITCODE)" }

Write-Host "Packing bundle..." -ForegroundColor Cyan
npx --yes '@anthropic-ai/mcpb@2.1.2' pack (Join-Path $root 'mcpb') $output
if ($LASTEXITCODE -ne 0) { throw "mcpb pack failed ($LASTEXITCODE)" }

Write-Host "`nBuilt $output" -ForegroundColor Green
