# Verifies the archives produced by scripts/package-release.ps1.
# Lists every problem and exits with a non-zero code if the layout is wrong.
#
#   pwsh scripts/test-release-package.ps1 -Version 0.0.42 [-PackageDirectory package]

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) { $PackageDirectory = 'package' }
if (-not [System.IO.Path]::IsPathRooted($PackageDirectory)) { $PackageDirectory = Join-Path $root $PackageDirectory }

$failures = New-Object System.Collections.Generic.List[string]
$bundle = 'AppData/Roaming/Autodesk/ApplicationPlugins/AcadHttpServer.bundle'
$contents = "$bundle/Contents/AutoCAD2020-2024"
$manualName = "AcadHttpServer-manual-install-v$Version.zip"

# Returns name -> bytes for every file entry of the archive (empty if the archive is missing).
function Read-Archive([string]$Name) {
    $result = [ordered]@{}
    $path = Join-Path $PackageDirectory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Archive is missing: $path")
        return $result
    }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.Contains('\')) { $failures.Add("${Name}: backslash in entry name '$($entry.FullName)'") }
            $stream = $entry.Open()
            $memory = New-Object System.IO.MemoryStream
            try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
            $result[$entry.FullName] = $memory.ToArray()
        }
    }
    finally { $zip.Dispose() }
    Write-Host "${Name}: $(@($result.Keys) -join ', ')"
    return $result
}

function Assert-ExactEntries([string]$Name, $Archive, [string[]]$Expected) {
    if ($Archive.Count -eq 0) { return }
    $actual = @($Archive.Keys)
    foreach ($entry in $Expected) {
        if ($actual -notcontains $entry) { $failures.Add("${Name}: missing '$entry'") }
    }
    foreach ($entry in $actual) {
        if ($Expected -notcontains $entry) { $failures.Add("${Name}: unexpected entry '$entry' (AutoCAD DLLs, .pdb and other files must not be packaged)") }
    }
}

function Test-SameBytes([byte[]]$A, [byte[]]$B) {
    if ($null -eq $A -or $null -eq $B -or $A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}

$plain = Read-Archive 'AutoCADHttp.zip'
Assert-ExactEntries 'AutoCADHttp.zip' $plain @('AutoCADHttp.dll', 'AutoCADHttp.settings.json')

$manual = Read-Archive $manualName
Assert-ExactEntries $manualName $manual @(
    'install.cmd',
    'uninstall.cmd',
    'INSTALL.md',
    "$bundle/PackageContents.xml",
    "$contents/AutoCADHttp.dll",
    "$contents/AutoCADHttp.settings.json"
)

if ($manual.Count -gt 0 -and $plain.Count -gt 0) {
    foreach ($file in @('AutoCADHttp.dll', 'AutoCADHttp.settings.json')) {
        if (-not (Test-SameBytes $plain[$file] $manual["$contents/$file"])) {
            $failures.Add("$file differs between AutoCADHttp.zip and $manualName")
        }
    }
}

if ($manual.Contains("$contents/AutoCADHttp.settings.json")) {
    try { [System.Text.Encoding]::UTF8.GetString($manual["$contents/AutoCADHttp.settings.json"]) | ConvertFrom-Json | Out-Null }
    catch { $failures.Add("AutoCADHttp.settings.json is not valid JSON: $($_.Exception.Message)") }
}

# PackageContents.xml: AppVersion, AutoCAD 2020-2024 x64, every module is packaged.
if ($manual.Contains("$bundle/PackageContents.xml")) {
    $xml = New-Object System.Xml.XmlDocument
    try {
        $xml.LoadXml([System.Text.Encoding]::UTF8.GetString($manual["$bundle/PackageContents.xml"]).TrimStart([char]0xFEFF))
        $package = $xml.DocumentElement
        # get_LocalName(): the XML adapter would return the Name attribute for .Name
        if ($package.get_LocalName() -ne 'ApplicationPackage') { $failures.Add("PackageContents.xml: root element is '$($package.get_LocalName())', expected ApplicationPackage") }
        if ($package.GetAttribute('AppVersion') -ne $Version) { $failures.Add("PackageContents.xml: AppVersion is '$($package.GetAttribute('AppVersion'))', expected '$Version'") }
        if ($package.GetAttribute('ProductCode') -notmatch '^\{[0-9a-fA-F-]{36}\}$') { $failures.Add('PackageContents.xml: ProductCode must be a GUID in braces') }
        $requirements = @($xml.SelectNodes('//RuntimeRequirements'))
        if ($requirements.Count -eq 0) { $failures.Add('PackageContents.xml: RuntimeRequirements are missing') }
        foreach ($r in $requirements) {
            if ($r.GetAttribute('OS') -ne 'Win64') { $failures.Add("PackageContents.xml: OS must be Win64 (x64 DLL), got '$($r.GetAttribute('OS'))'") }
            if ($r.GetAttribute('SeriesMin') -ne 'R23.1') { $failures.Add("PackageContents.xml: SeriesMin must be R23.1 (AutoCAD 2020), got '$($r.GetAttribute('SeriesMin'))'") }
            if ($r.GetAttribute('SeriesMax') -ne 'R24.3') { $failures.Add("PackageContents.xml: SeriesMax must be R24.3 (AutoCAD 2024), got '$($r.GetAttribute('SeriesMax'))'") }
        }
        $components = @($xml.SelectNodes('//ComponentEntry'))
        if ($components.Count -eq 0) { $failures.Add('PackageContents.xml: no ComponentEntry') }
        foreach ($component in $components) {
            $module = "$bundle/" + $component.GetAttribute('ModuleName').TrimStart('.', '/')
            if (-not $manual.Contains($module)) { $failures.Add("PackageContents.xml references '$($component.GetAttribute('ModuleName'))', but '$module' is not packaged") }
            if ($component.GetAttribute('AppType') -ne '.Net') { $failures.Add("PackageContents.xml: AppType must be .Net") }
            if ($component.GetAttribute('LoadOnAutoCADStartup') -ne 'True') { $failures.Add('PackageContents.xml: LoadOnAutoCADStartup must be True') }
            $commands = @($component.SelectNodes('Commands/Command') | ForEach-Object { $_.GetAttribute('Global') })
            foreach ($command in @('HTTPSTART', 'HTTPSTOP', 'HTTPSTATUS')) {
                if ($commands -notcontains $command) { $failures.Add("PackageContents.xml: command $command is not declared") }
            }
        }
    }
    catch { $failures.Add("PackageContents.xml is not valid XML: $($_.Exception.Message)") }
}

# release-manifest.txt lists the SHA256 of both archives.
$manifestPath = Join-Path $PackageDirectory 'release-manifest.txt'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    $failures.Add("release-manifest.txt is missing")
}
else {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw
    foreach ($name in @('AutoCADHttp.zip', $manualName)) {
        $path = Join-Path $PackageDirectory $name
        if (Test-Path -LiteralPath $path) {
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $manifest.Contains("$hash  $name")) { $failures.Add("release-manifest.txt: wrong or missing SHA256 for $name") }
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    throw "$($failures.Count) release package check(s) failed."
}

Write-Host "Release package layout is valid."
