[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ManifestPath,
    [Parameter(Mandatory)] [string]$PublicKeyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolProject = Join-Path $PSScriptRoot 'ReleaseTool\UsageToolsReleaseTool.csproj'
& dotnet run --project $toolProject -c Release -- `
    verify `
    --manifest ([IO.Path]::GetFullPath($ManifestPath)) `
    --public-key ([IO.Path]::GetFullPath($PublicKeyPath))
if ($LASTEXITCODE -ne 0) {
    throw "Signed update verification failed with exit code $LASTEXITCODE"
}
