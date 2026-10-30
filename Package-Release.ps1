# ==============================================================================
# Caveman's Discord Bridge - release packaging
# Builds the plugin and the preloader, then writes the Hexium/Thunderstore zip:
#   CavemansDiscordBridge.dll, manifest.json, README.md, CHANGELOG.md, icon.png, patchers/CavemansDiscordBridge.Preloader.dll
# ==============================================================================

param(
    [string]$OutDir = (Join-Path $PSScriptRoot "release")
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

# Version comes from the plugin itself, so the zip can never disagree with the DLL
$pluginCs = [IO.File]::ReadAllText((Join-Path $root "Plugin.cs"), $utf8NoBom)
if ($pluginCs -notmatch 'ModVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"') { throw "ModVersion not found in Plugin.cs" }
$version = $Matches[1]
Write-Host "Caveman's Discord Bridge $version" -ForegroundColor Cyan

foreach ($proj in @("CavemansDiscordBridge.csproj", "Preloader\CavemansDiscordBridge.Preloader.csproj")) {
    dotnet build (Join-Path $root $proj) -c Release -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
}

$stage = Join-Path $OutDir "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "patchers") | Out-Null

Copy-Item (Join-Path $root "bin\Release\netstandard2.1\CavemansDiscordBridge.dll") $stage
Copy-Item (Join-Path $root "Preloader\bin\Release\netstandard2.1\CavemansDiscordBridge.Preloader.dll") (Join-Path $stage "patchers")
foreach ($f in @("README.md", "CHANGELOG.md", "icon.png")) { Copy-Item (Join-Path $root $f) $stage }

$manifest = [ordered]@{
    name           = "Cavemans_Discord_Bridge"
    version_number = $version
    website_url    = "https://github.com/TheCavemanOfficial/CavemansDiscordBridge"
    description    = "Two-way Discord chat for your Valheim server, status posts, death feed, scheduled restarts, a live admin panel with buttons, admin commands, and reports on why players can't join. Server-side; players don't need it."
    dependencies   = @("denikson-BepInExPack_Valheim-5.4.2333")
}
[IO.File]::WriteAllText((Join-Path $stage "manifest.json"), ($manifest | ConvertTo-Json), $utf8NoBom)

# Never ship settings: the server file holds the bot token, webhooks and Steam key
$leaked = @(Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in ".cfg", ".yml", ".json" -and $_.Name -ne "manifest.json" })
if ($leaked.Count -gt 0) { throw "Refusing to package settings files: $($leaked.Name -join ', ')" }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $OutDir "TheCaveman-Cavemans_Discord_Bridge-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
foreach ($f in Get-ChildItem $stage -Recurse -File) {
    # Forward slashes, so every mod manager puts the preloader in patchers/
    $entry = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
$archive.Dispose()
Remove-Item $stage -Recurse -Force

Write-Host ("-> {0} ({1:N0} KB)" -f $zip, ((Get-Item $zip).Length / 1KB)) -ForegroundColor Green
