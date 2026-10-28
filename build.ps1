#Requires -Version 7
<#
.SYNOPSIS
  Builds dist\com.fulgan.managedapplauncher.sdPlugin and the installable .streamDeckPlugin.

.PARAMETER Install
  Also copies the plugin into the Stream Deck plugins folder, restarting the Stream Deck application.

.PARAMETER Uninstall
  Builds nothing: removes the installed plugin and its Explorer verb, restarting the Stream Deck application.
  Settings in %APPDATA%\ManagedAppLauncher are kept.
#>
param(
    [switch]$Install,
    [switch]$Uninstall,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$pluginId = "com.fulgan.managedapplauncher"
$dist = Join-Path $root "dist"
$folder = Join-Path $dist "$pluginId.sdPlugin"
$package = Join-Path $dist "$pluginId.streamDeckPlugin"
$plugins = Join-Path $env:APPDATA "Elgato\StreamDeck\Plugins"
$target = Join-Path $plugins "$pluginId.sdPlugin"
$streamDeck = "C:\Program Files\Elgato\StreamDeck\StreamDeck.exe"

function Stop-StreamDeck {
    # The manager saves each edit as it is made, so a close request loses nothing; killed if it does not exit.
    foreach ($manager in Get-Process ManagedLauncher.Manager -ErrorAction SilentlyContinue) {
        $manager.CloseMainWindow() | Out-Null
        if (-not $manager.WaitForExit(5000)) { $manager.Kill() }
    }
    Get-Process StreamDeck -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process malaunch-plugin, applauncher-plugin -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

if ($Install -and $Uninstall) { throw "-Install and -Uninstall exclude each other" }

if ($Uninstall) {
    # The verb points at malaunch.exe inside the plugin folder, so it goes while that exe still exists.
    $cli = Join-Path $target "malaunch.exe"
    if (Test-Path $cli) { & $cli shell uninstall }
    Stop-StreamDeck
    try {
        foreach ($path in $target, (Join-Path $plugins "com.fulgan.applauncher.sdPlugin")) {
            if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Host "removed $path" }
        }
    }
    finally {
        Start-Process $streamDeck
    }
    Write-Host "uninstalled; settings kept in $env:APPDATA\ManagedAppLauncher"
    return
}

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
& $cli profiles --out $profiles --manifest (Join-Path $folder "manifest.json")
if ($LASTEXITCODE -ne 0) { throw "profile generation failed" }

if (Test-Path $package) { Remove-Item $package -Force }
Compress-Archive -Path $folder -DestinationPath "$package.zip"
Move-Item "$package.zip" $package
Write-Host "built $package"

if ($Install) {
    Stop-StreamDeck
    # Removes an install under the former id com.fulgan.applauncher, which Stream Deck would load beside this one.
    $legacy = Join-Path $plugins "com.fulgan.applauncher.sdPlugin"
    if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force }
    # Emptied, not deleted: an app the plugin started may hold the folder as its current directory.
    if (Test-Path $target) { Get-ChildItem $target -Force | Remove-Item -Recurse -Force }
    else { New-Item -ItemType Directory $target | Out-Null }
    Copy-Item (Join-Path $folder '*') $target -Recurse
    Start-Process $streamDeck
    Write-Host "installed to $target"
}
