[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string]$PrivateKeyPath,
    [Parameter(Mandatory)] [string]$PublicKeyPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$privateFullPath = [IO.Path]::GetFullPath($PrivateKeyPath)
$publicFullPath = [IO.Path]::GetFullPath($PublicKeyPath)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPrefix = $projectRoot.TrimEnd('\') + '\'
if ($privateFullPath.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The private signing key must be stored outside the project and every release package.'
}

if (-not $PSCmdlet.ShouldProcess($privateFullPath, 'Generate a 4096-bit RSA update signing key')) {
    return
}

$toolProject = Join-Path $PSScriptRoot 'ReleaseTool\UsageToolsReleaseTool.csproj'
$arguments = @(
    'run', '--project', $toolProject, '-c', 'Release', '--',
    'keygen', '--private', $privateFullPath, '--public', $publicFullPath
)
if ($Force) {
    $arguments += '--force'
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Update key generation failed with exit code $LASTEXITCODE"
}
