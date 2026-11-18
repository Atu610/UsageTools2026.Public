[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ReleaseDirectory,
    [Parameter(Mandatory)] [string]$PrivateKeyPath,
    [Parameter(Mandatory)] [string]$BaseUrl,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [string]$ReleaseNotesPath = '',
    [string]$MinimumVersion = '',
    [switch]$Mandatory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolProject = Join-Path $PSScriptRoot 'ReleaseTool\UsageToolsReleaseTool.csproj'
$arguments = @(
    'run', '--project', $toolProject, '-c', 'Release', '--',
    'publish',
    '--release-dir', [IO.Path]::GetFullPath($ReleaseDirectory),
    '--private-key', [IO.Path]::GetFullPath($PrivateKeyPath),
    '--base-url', $BaseUrl,
    '--version', $Version,
    '--output-dir', [IO.Path]::GetFullPath($OutputDirectory)
)
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $arguments += @('--notes', [IO.Path]::GetFullPath($ReleaseNotesPath))
}
if (-not [string]::IsNullOrWhiteSpace($MinimumVersion)) {
    $arguments += @('--minimum', $MinimumVersion)
}
if ($Mandatory) {
    $arguments += '--mandatory'
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Signed update publishing failed with exit code $LASTEXITCODE"
}
