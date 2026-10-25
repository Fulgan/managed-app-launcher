#Requires -Version 7
<#
.SYNOPSIS
  Builds dist\com.fulgan.managedapplauncher.sdPlugin and the installable .streamDeckPlugin.

.PARAMETER Install
  Also copies the plugin into the Stream Deck plugins folder, restarting the Stream Deck application.
#>
param(
    [switch]$Install,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$pluginId = "com.fulgan.managedapplauncher"
$dist = Join-Path $root "dist"
$folder = Join-Path $dist "$pluginId.sdPlugin"
$package = Join-Path $dist "$pluginId.streamDeckPlugin"

if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
New-Item -ItemType Directory -Force $folder | Out-Null

# The plugin, CLI and manager share one self-contained runtime in the plugin folder.
foreach ($project in "src/ManagedLauncher.Plugin", "src/ManagedLauncher.Cli", "src/ManagedLauncher.Manager") {
    dotnet publish (Join-Path $root $project) -c $Configuration -r win-x64 --self-contained -o $folder -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

Copy-Item (Join-Path $root "plugin/manifest.json") $folder
Copy-Item (Join-Path $root "plugin/imgs") $folder -Recurse
Copy-Item (Join-Path $root "plugin/pi") $folder -Recurse

# Launcher profiles bundled through the manifest's Profiles array; Stream Deck offers them on install.
$profiles = Join-Path $folder "profiles"
New-Item -ItemType Directory -Force $profiles | Out-Null
$cli = Join-Path $folder "malaunch.exe"
foreach ($p in @(
        @{ Name = "Managed App Launcher"; Size = "5x3"; Model = "20GBA9901" },
        @{ Name = "Managed App Launcher XL"; Size = "8x4"; Model = "20GAT9902" },
        @{ Name = "Managed App Launcher Mini"; Size = "3x2"; Model = "20GAI9902" })) {
    & $cli page --out (Join-Path $profiles "$($p.Name).streamDeckProfile") --name "Managed App Launcher" --size $p.Size --device $p.Model
    if ($LASTEXITCODE -ne 0) { throw "profile generation failed: $($p.Name)" }
}

if (Test-Path $package) { Remove-Item $package -Force }
Compress-Archive -Path $folder -DestinationPath "$package.zip"
Move-Item "$package.zip" $package
Write-Host "built $package"

if ($Install) {
    $plugins = Join-Path $env:APPDATA "Elgato\StreamDeck\Plugins"
    $exe = "C:\Program Files\Elgato\StreamDeck\StreamDeck.exe"
    Get-Process StreamDeck -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process malaunch-plugin, applauncher-plugin -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    # Removes an install under the former id com.fulgan.applauncher, which Stream Deck would load beside this one.
    $legacy = Join-Path $plugins "com.fulgan.applauncher.sdPlugin"
    if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force }
    $target = Join-Path $plugins "$pluginId.sdPlugin"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $folder $plugins -Recurse
    Start-Process $exe
    Write-Host "installed to $target"
}
