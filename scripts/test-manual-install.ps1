# Windows only: runs install.cmd / uninstall.cmd from the manual-install archive against a temporary %APPDATA%
# (the real user profile is not touched) and checks the installed AcadHttpServer.bundle.
#
#   powershell -File scripts/test-manual-install.ps1 -Version 0.0.42 [-PackageDirectory package]

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) { $PackageDirectory = 'package' }
if (-not [System.IO.Path]::IsPathRooted($PackageDirectory)) { $PackageDirectory = Join-Path $root $PackageDirectory }
$archive = Join-Path $PackageDirectory "AcadHttpServer-manual-install-v$Version.zip"
if (-not (Test-Path -LiteralPath $archive)) { throw "Archive not found: $archive" }

# A space and parentheses in the profile path check the quoting in the .cmd scripts.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("acadhttp manual (" + [Guid]::NewGuid().ToString('N') + ")")
$extracted = Join-Path $work 'archive'
$appData = Join-Path $work 'Roaming'
New-Item -ItemType Directory -Force -Path $extracted, $appData | Out-Null

$bundle = Join-Path $appData 'Autodesk\ApplicationPlugins\AcadHttpServer.bundle'
$contents = Join-Path $bundle 'Contents\AutoCAD2020-2024'
$settings = Join-Path $contents 'AutoCADHttp.settings.json'

function Invoke-Script([string]$Name) {
    $savedAppData = $env:APPDATA
    $savedPreference = $ErrorActionPreference
    # Run from the extracted folder: the script name needs no quoting, %~dp0 still contains spaces and ( ).
    Push-Location -LiteralPath $extracted
    try {
        $ErrorActionPreference = 'Continue'
        $env:APPDATA = $appData
        $output = & cmd.exe /d /c $Name /quiet 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $env:APPDATA = $savedAppData
        $ErrorActionPreference = $savedPreference
        Pop-Location
    }
    $output | ForEach-Object { Write-Host "  $Name> $_" }
    if ($code -ne 0) { throw "$Name exited with code $code" }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "OK: $Message"
}

try {
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted

    # 1. Fresh installation.
    Invoke-Script 'install.cmd'
    foreach ($file in @((Join-Path $bundle 'PackageContents.xml'), (Join-Path $contents 'AutoCADHttp.dll'), $settings)) {
        Assert-True (Test-Path -LiteralPath $file -PathType Leaf) "installed $file"
    }
    $installedDll = (Get-FileHash (Join-Path $contents 'AutoCADHttp.dll')).Hash
    $packagedDll = (Get-FileHash (Join-Path $extracted 'AppData\Roaming\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\Contents\AutoCAD2020-2024\AutoCADHttp.dll')).Hash
    Assert-True ($installedDll -eq $packagedDll) 'installed AutoCADHttp.dll matches the archive'
    $extra = @(Get-ChildItem -LiteralPath $appData -Recurse -File | Where-Object { -not $_.FullName.StartsWith($bundle) })
    Assert-True ($extra.Count -eq 0) 'nothing is installed outside AcadHttpServer.bundle'

    # 2. Update: user settings are preserved, plugin files are replaced.
    $custom = '{ "address": "127.0.0.1", "port": 5123, "widgetsDirectory": "C:/widgets" }'
    [System.IO.File]::WriteAllText($settings, $custom)
    [System.IO.File]::WriteAllText((Join-Path $contents 'AutoCADHttp.dll'), 'old version')
    Invoke-Script 'install.cmd'
    Assert-True ([System.IO.File]::ReadAllText($settings) -eq $custom) 'reinstall keeps the user AutoCADHttp.settings.json'
    Assert-True ((Get-FileHash (Join-Path $contents 'AutoCADHttp.dll')).Hash -eq $packagedDll) 'reinstall replaces AutoCADHttp.dll'

    # 3. Uninstall, twice (the second run must succeed when nothing is installed).
    Invoke-Script 'uninstall.cmd'
    Assert-True (-not (Test-Path -LiteralPath $bundle)) 'uninstall removes AcadHttpServer.bundle'
    Invoke-Script 'uninstall.cmd'
    Assert-True (-not (Test-Path -LiteralPath $bundle)) 'uninstall without an installation succeeds'

    Write-Host 'Manual installation scripts work.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
