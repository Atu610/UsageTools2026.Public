[CmdletBinding()]
param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$CertificateThumbprint = '',
    [string]$SignToolPath = '',
    [string]$BuildRoot = '',
    [string]$LogPath = '',
    [switch]$InPlace,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($LogPath) { Start-Transcript -Path $LogPath -Force | Out-Null }

function Remove-OwnedDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$OwnerRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $OwnerRoot).Path
    if (-not $resolvedPath.StartsWith($resolvedRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside project root: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Add-ObfuscarVariable {
    param(
        [Parameter(Mandatory)] [System.Xml.XmlDocument]$Document,
        [Parameter(Mandatory)] [System.Xml.XmlElement]$Root,
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Value
    )

    $element = $Document.CreateElement('Var')
    $element.SetAttribute('name', $Name)
    $element.SetAttribute('value', $Value)
    [void]$Root.AppendChild($element)
}

function Resolve-SignTool {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "SignTool was not found: $ExplicitPath"
        }

        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kitsRoot)) {
        throw 'Windows SDK SignTool was not found. Install the Windows SDK or pass -SignToolPath.'
    }

    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'signtool.exe' |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw 'Windows SDK x64 SignTool was not found.'
    }

    return $candidate.FullName
}

$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$projectFile = Join-Path $ProjectRoot 'UsageTools2026.csproj'
$updaterProject = Join-Path $ProjectRoot 'Updater\UsageToolsUpdater.csproj'
$toolManifest = Join-Path $ProjectRoot 'dotnet-tools.json'
$binRoot = Join-Path $ProjectRoot 'bin'
if (-not [string]::IsNullOrWhiteSpace($BuildRoot)) {
    $binRoot = [IO.Path]::GetFullPath($BuildRoot)
    if (-not $binRoot.StartsWith($ProjectRoot.TrimEnd('\') + '\bin\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'BuildRoot must be an isolated subdirectory of the project bin directory.'
    }
}
$stagePath = Join-Path $binRoot 'ProtectedStage'
$updaterStagePath = Join-Path $binRoot 'ProtectedUpdaterStage'
$obfuscatedPath = Join-Path $binRoot 'ProtectedObfuscated'
$outputPath = Join-Path $binRoot 'ProtectedRelease'
$packagePath = Join-Path $binRoot 'ProtectedPackages'
$privateMapPath = Join-Path $binRoot 'ProtectionMaps'
$configPath = Join-Path $binRoot 'obfuscar.generated.xml'
$obfuscarLog = Join-Path $privateMapPath 'obfuscar-log.xml'

if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "Project file was not found: $projectFile"
}

if (-not (Test-Path -LiteralPath $toolManifest)) {
    throw "Local tool manifest was not found: $toolManifest"
}

foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='UsageTools2026.exe'")) {
    if (!$process.ExecutablePath -or $process.ExecutablePath -eq (Join-Path $outputPath 'UsageTools2026.exe')) {
        throw 'Close the running output application before building; output files have not been touched.'
    }
}

Remove-OwnedDirectory -Path $stagePath -OwnerRoot $ProjectRoot
Remove-OwnedDirectory -Path $updaterStagePath -OwnerRoot $ProjectRoot
Remove-OwnedDirectory -Path $obfuscatedPath -OwnerRoot $ProjectRoot
if (-not $InPlace) { Remove-OwnedDirectory -Path $outputPath -OwnerRoot $ProjectRoot }
New-Item -ItemType Directory -Path $stagePath, $updaterStagePath, $obfuscatedPath, $outputPath, $packagePath, $privateMapPath -Force | Out-Null

Push-Location $ProjectRoot
try {
    if (-not $SkipRestore) {
    & dotnet tool restore --tool-manifest $toolManifest
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed with exit code $LASTEXITCODE"
    }

    & dotnet restore $projectFile -r $RuntimeIdentifier --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "locked NuGet restore failed with exit code $LASTEXITCODE"
    }

    & dotnet restore $updaterProject -r $RuntimeIdentifier
    if ($LASTEXITCODE -ne 0) {
        throw "updater restore failed with exit code $LASTEXITCODE"
    }
    }

    & dotnet publish $projectFile `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        --no-restore `
        --nologo `
        -o $stagePath `
        '-p:PublishSingleFile=false' `
        '-p:PublishReadyToRun=false' `
        '-p:PublishTrimmed=false' `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    & dotnet publish $updaterProject `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        --no-restore `
        --nologo `
        -o $updaterStagePath `
        '-p:PublishSingleFile=true' `
        '-p:PublishReadyToRun=false' `
        '-p:PublishTrimmed=true' `
        '-p:TrimMode=full' `
        '-p:EnableCompressionInSingleFile=true' `
        '-p:InvariantGlobalization=true' `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) {
        throw "updater publish failed with exit code $LASTEXITCODE"
    }

    $updaterExecutable = Join-Path $updaterStagePath 'UsageToolsUpdater.exe'
    if (-not (Test-Path -LiteralPath $updaterExecutable)) {
        throw "Published updater was not found: $updaterExecutable"
    }
    Copy-Item -LiteralPath $updaterExecutable -Destination (Join-Path $stagePath 'UsageToolsUpdater.exe') -Force

    $inputAssembly = Join-Path $stagePath 'UsageTools2026.dll'
    if (-not (Test-Path -LiteralPath $inputAssembly)) {
        throw "Published application assembly was not found: $inputAssembly"
    }

    $document = [System.Xml.XmlDocument]::new()
    $declaration = $document.CreateXmlDeclaration('1.0', 'utf-8', $null)
    [void]$document.AppendChild($declaration)
    $root = $document.CreateElement('Obfuscator')
    [void]$document.AppendChild($root)

    Add-ObfuscarVariable -Document $document -Root $root -Name 'InPath' -Value $stagePath
    Add-ObfuscarVariable -Document $document -Root $root -Name 'OutPath' -Value $obfuscatedPath
    Add-ObfuscarVariable -Document $document -Root $root -Name 'LogFile' -Value $obfuscarLog
    Add-ObfuscarVariable -Document $document -Root $root -Name 'KeepPublicApi' -Value 'false'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'HidePrivateApi' -Value 'true'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'HideStrings' -Value 'true'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'RenameProperties' -Value 'true'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'RenameEvents' -Value 'true'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'RenameFields' -Value 'true'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'UseUnicodeNames' -Value 'true'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'SkipGenerated' -Value 'true'
    Add-ObfuscarVariable -Document $document -Root $root -Name 'SuppressIldasm' -Value 'true'

    $module = $document.CreateElement('Module')
    $module.SetAttribute('file', $inputAssembly)
    [void]$root.AppendChild($module)

    $preservedTypes = @(
        'UsageTools2026.App',
        'UsageTools2026.MainWindow',
        'UsageTools2026.MainWindow/ProjectRow',
        'UsageTools2026.MiniStatusWindow',
        'UsageTools2026.AboutWindow',
        'UsageTools2026.AiChangelogWindow',
        'UsageTools2026.UpdateWindow',
        'UsageTools2026.Models.AiProvider',
        'UsageTools2026.Models.AiReleaseInfo',
        'UsageTools2026.Models.AiChangelogSnapshot',
        'UsageTools2026.Models.ReportContext',
        'UsageTools2026.Models.CodexCreditBalance',
        'UsageTools2026.Models.ClaudeOfficialDetails',
        'UsageTools2026.Models.ClaudeLimitPool',
        'UsageTools2026.Models.ClaudeResetGrantSummary',
        'UsageTools2026.Models.ClaudeResetGrant',
        'UsageTools2026.Models.ClaudeModelOption',
        'UsageTools2026.Models.ClaudeExtraUsage',
        'UsageTools2026.Services.AppLanguage',
        'UsageTools2026.Services.AppTheme',
        'UsageTools2026.Services.AppSettings',
        'UsageTools2026.Services.AppSettingsService',
        'UsageTools2026.Services.BarColorMode',
        'UsageTools2026.Services.ThemeDefinition',
        'UsageTools2026.Services.ThemeCatalog',
        'UsageTools2026.Services.ClaudeOfficialUsageCache',
        'UsageTools2026.Services.AiChangelogStateDocument',
        'UsageTools2026.Services.MiniUsageAnnouncementState',
        'UsageTools2026.Services.MiniUsageAnnouncement',
        'UsageTools2026.Services.MiniUsageAnnouncementKind',
        'UsageTools2026.Services.PersonalMobileCredentials',
        'UsageTools2026.Services.MobileQuotaWindow',
        'UsageTools2026.Services.MobileQuotaProvider',
        'UsageTools2026.Services.MobileQuotaPayload',
        'UsageTools2026.Services.ResetNewsDeliveryService/Delivery',
        'UsageTools2026.Services.UsageDataState',
        'UsageTools2026.Services.MiniUsageWindowState',
        'UsageTools2026.Services.CodexOfficialUsageReader/CodexOfficialUsage',
        'UsageTools2026.Services.UpdateService/SignedUpdateEnvelope',
        'UsageTools2026.Services.UpdateService/UpdatePayload',
        'UsageTools2026.Services.UpdateInstallerService/UpdateInstallResult'
    )
    foreach ($typeName in $preservedTypes) {
        $skip = $document.CreateElement('SkipType')
        $skip.SetAttribute('name', $typeName)
        $skip.SetAttribute('skipMethods', 'true')
        $skip.SetAttribute('skipFields', 'true')
        $skip.SetAttribute('skipProperties', 'true')
        $skip.SetAttribute('skipEvents', 'true')
        [void]$module.AppendChild($skip)
    }

    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.Xml.XmlWriter]::Create($configPath, $settings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }

    & dotnet tool run obfuscar.console $configPath
    if ($LASTEXITCODE -ne 0) {
        throw "Obfuscar failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$obfuscatedAssembly = Join-Path $obfuscatedPath 'UsageTools2026.dll'
if (-not (Test-Path -LiteralPath $obfuscatedAssembly)) {
    throw "Obfuscated assembly was not produced: $obfuscatedAssembly"
}

foreach ($stageItem in Get-ChildItem -LiteralPath $stagePath -Recurse -File -Force) {
    if ($stageItem.Extension -in @('.pdb', '.xml') -or $stageItem.Name -eq 'UsageTools2026.dll') { continue }
    $relative = [IO.Path]::GetRelativePath($stagePath, $stageItem.FullName)
    $destination = Join-Path $outputPath $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    if ((Test-Path -LiteralPath $destination) -and
        (Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $stageItem.FullName).Hash) { continue }
    Copy-Item -LiteralPath $stageItem.FullName -Destination $destination -Force
}
Copy-Item -LiteralPath $obfuscatedAssembly -Destination (Join-Path $outputPath 'UsageTools2026.dll') -Force
Get-ChildItem -LiteralPath $outputPath -Recurse -File |
    Where-Object { $_.Extension -in @('.pdb', '.xml') } |
    Remove-Item -Force

$requiredOutputs = @(
    (Join-Path $outputPath 'UsageTools2026.exe'),
    (Join-Path $outputPath 'UsageTools2026.dll'),
    (Join-Path $outputPath 'UsageTools2026.runtimeconfig.json'),
    (Join-Path $outputPath 'UsageToolsUpdater.exe')
)
foreach ($requiredOutput in $requiredOutputs) {
    if (-not (Test-Path -LiteralPath $requiredOutput)) {
        throw "Protected release is incomplete; required file is missing: $requiredOutput"
    }
}

$teamReadmeSource = Join-Path $PSScriptRoot 'TEAM_README_zh-TW.txt'
if (-not (Test-Path -LiteralPath $teamReadmeSource)) {
    throw "Team readme was not found: $teamReadmeSource"
}
Copy-Item -LiteralPath $teamReadmeSource -Destination (Join-Path $outputPath 'TEAM_README_zh-TW.txt') -Force

$signed = $false
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $signTool = Resolve-SignTool -ExplicitPath $SignToolPath
    $signTargets = @(
        (Join-Path $outputPath 'UsageTools2026.exe'),
        (Join-Path $outputPath 'UsageTools2026.dll'),
        (Join-Path $outputPath 'UsageToolsUpdater.exe')
    )
    foreach ($target in $signTargets) {
        & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $target
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed: $target"
        }

        & $signTool verify /pa /v $target
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode verification failed: $target"
        }
    }

    $signed = $true
}

$reportPath = Join-Path $outputPath 'PROTECTION_REPORT.txt'
$report = @(
    'UsageTools2026 protected release',
    "Built UTC: $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))",
    'Obfuscator: Obfuscar.GlobalTool 2.2.50',
    'Public implementation API renamed: yes (except WPF/BAML and JSON contracts)',
    'Private implementation renamed: yes',
    'String hiding: yes',
    'PDB files shipped: no',
    "Authenticode signed: $signed",
    "Runtime: self-contained $RuntimeIdentifier",
    'External updater: self-contained single-file with health-check rollback',
    'PublishTrimmed: false (required for WPF/reflection safety)',
    'PublishReadyToRun: false (obfuscation runs on managed IL)',
    'Private Obfuscar mapping/log files are not included in this folder.'
)
[IO.File]::WriteAllLines($reportPath, $report, [System.Text.UTF8Encoding]::new($false))

$hashPath = Join-Path $outputPath 'SHA256SUMS.txt'
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $stagePath -Recurse -File) {
    [void]$allowed.Add([IO.Path]::GetRelativePath($stagePath, $file.FullName))
}
foreach ($name in @('TEAM_README_zh-TW.txt','PROTECTION_REPORT.txt','SHA256SUMS.txt')) { [void]$allowed.Add($name) }
foreach ($file in Get-ChildItem -LiteralPath $outputPath -Recurse -File) {
    if (!$allowed.Contains([IO.Path]::GetRelativePath($outputPath, $file.FullName))) {
        throw "Unrecognized output file, refusing to package: $($file.Name)"
    }
}
$hashLines = Get-ChildItem -LiteralPath $outputPath -Recurse -File |
    Where-Object { $_.FullName -ne $hashPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($outputPath.Length).TrimStart('\').Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
[IO.File]::WriteAllLines($hashPath, $hashLines, [System.Text.UTF8Encoding]::new($false))

$zipPath = Join-Path $packagePath "UsageTools2026-Protected-$RuntimeIdentifier.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $zipPath -CompressionLevel Optimal

[pscustomobject]@{
    OutputDirectory = $outputPath
    Package = $zipPath
    Signed = $signed
    PackageSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
}
