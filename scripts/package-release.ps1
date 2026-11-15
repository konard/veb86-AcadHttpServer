# Builds the release archives from an already compiled AutoCADHttp.dll:
#
#   AutoCADHttp.zip                                  AutoCADHttp.dll + AutoCADHttp.settings.json at the root (NETLOAD)
#   AcadHttpServer-manual-install-v<Version>.zip     per-user installation without an installer (AutoCAD 2020-2024):
#       AppData/Roaming/Autodesk/ApplicationPlugins/AcadHttpServer.bundle/
#           PackageContents.xml
#           Contents/AutoCAD2020-2024/AutoCADHttp.dll
#           Contents/AutoCAD2020-2024/AutoCADHttp.settings.json
#       install.cmd, uninstall.cmd, INSTALL.md
#   release-manifest.txt                             SHA256 of both archives
#
# Usage (from the repository root, after `dotnet build AutoCADHttp.sln -c Release`):
#   pwsh scripts/package-release.ps1 -Version 0.0.42
#   pwsh scripts/test-release-package.ps1 -Version 0.0.42
#
# Runs on Windows PowerShell 5.1 and PowerShell 7 (Windows / Linux). The output directory is replaced.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$BuildOutput,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ($Version -notmatch '^\d+\.\d+(?:\.\d+){0,2}$') {
    throw "Version must look like 0.0.42, 1.2 or 1.2.3.4 (got '$Version')."
}

$root = Split-Path -Parent $PSScriptRoot
function Resolve-RepoPath([string]$Path, [string]$Default) {
    if ([string]::IsNullOrWhiteSpace($Path)) { $Path = $Default }
    if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $root $Path }
    return [System.IO.Path]::GetFullPath($Path)
}
$BuildOutput = Resolve-RepoPath $BuildOutput 'src/AutoCADHttp/bin/Release'
$OutputDirectory = Resolve-RepoPath $OutputDirectory 'package'

$bundleName = 'AcadHttpServer.bundle'
$bundleEntry = "AppData/Roaming/Autodesk/ApplicationPlugins/$bundleName"
$contentsEntry = 'Contents/AutoCAD2020-2024'
$pluginFiles = @('AutoCADHttp.dll', 'AutoCADHttp.settings.json')
$manualName = "AcadHttpServer-manual-install-v$Version.zip"

foreach ($file in $pluginFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $BuildOutput $file) -PathType Leaf)) {
        throw "Build output is incomplete: '$file' was not found in '$BuildOutput'. Run 'dotnet build AutoCADHttp.sln -c Release' first."
    }
}

# PackageContents.xml: the template's AppVersion is replaced with the release version.
$manifestPath = [System.IO.Path]::Combine($root, 'installer_acad', 'PackageContents.xml')
$packageContents = New-Object System.Xml.XmlDocument
$packageContents.PreserveWhitespace = $true
$packageContents.Load($manifestPath)
$packageContents.DocumentElement.SetAttribute('AppVersion', $Version)
$xmlSettings = New-Object System.Xml.XmlWriterSettings
$xmlSettings.Encoding = New-Object System.Text.UTF8Encoding($false)
$xmlStream = New-Object System.IO.MemoryStream
$xmlWriter = [System.Xml.XmlWriter]::Create($xmlStream, $xmlSettings)
$packageContents.Save($xmlWriter)
$xmlWriter.Dispose()
$packageContentsBytes = $xmlStream.ToArray()

# Entries are written with forward slashes (ZIP specification), so the archives are identical on Windows and Linux.
function New-ZipArchive([string]$Path, [object[]]$Entries) {
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($item in $Entries) {
                $entry = $zip.CreateEntry($item.Name, [System.IO.Compression.CompressionLevel]::Optimal)
                $entryStream = $entry.Open()
                try {
                    if ($null -ne $item.Bytes) { $bytes = $item.Bytes } else { $bytes = [System.IO.File]::ReadAllBytes($item.Source) }
                    $entryStream.Write($bytes, 0, $bytes.Length)
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

# 1. AutoCADHttp.zip - unchanged layout for NETLOAD from any folder.
$plainEntries = @($pluginFiles | ForEach-Object { @{ Name = $_; Source = (Join-Path $BuildOutput $_) } })
New-ZipArchive (Join-Path $OutputDirectory 'AutoCADHttp.zip') $plainEntries

# 2. Manual installation: the archive mirrors %USERPROFILE%, everything goes to the current user's %APPDATA%.
$manualEntries = @(
    @{ Name = "$bundleEntry/PackageContents.xml"; Bytes = $packageContentsBytes }
)
foreach ($file in $pluginFiles) {
    $manualEntries += @{ Name = "$bundleEntry/$contentsEntry/$file"; Source = (Join-Path $BuildOutput $file) }
}
foreach ($file in @('install.cmd', 'uninstall.cmd', 'INSTALL.md')) {
    $manualEntries += @{ Name = $file; Source = [System.IO.Path]::Combine($root, 'installer_manual', $file) }
}
New-ZipArchive (Join-Path $OutputDirectory $manualName) $manualEntries

# 3. Checksums.
$lines = @("AcadHttpServer release v$Version", '', 'Archives (SHA256):')
Get-ChildItem -LiteralPath $OutputDirectory -File -Filter '*.zip' | Sort-Object Name | ForEach-Object {
    $lines += "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
}
[System.IO.File]::WriteAllLines((Join-Path $OutputDirectory 'release-manifest.txt'), [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))

Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | ForEach-Object { Write-Host "$($_.Name)  ($($_.Length) bytes)" }
Write-Host "Release package created: $OutputDirectory"
