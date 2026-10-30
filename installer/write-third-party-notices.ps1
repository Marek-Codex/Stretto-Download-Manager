[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
$payload = (Resolve-Path -LiteralPath $PayloadPath).Path
$assetsPath = Join-Path $projectRoot 'Stretto.App\obj\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
$packageRoots = @($assets.packageFolders.Keys)
$noticeRoot = Join-Path $payload 'third-party-licenses'
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
$inventory = [System.Collections.Generic.List[string]]::new()
$inventory.Add('# Bundled dependency licenses and notices')
$inventory.Add('')
$inventory.Add('Generated from this build''s restored packages and self-contained runtime. Original texts are retained below. SDK/build dependency notices are included conservatively; not every SDK binary is redistributed. Optional ffmpeg and yt-dlp are not bundled.')
$inventory.Add('')

function Copy-Notices([string]$Id, [string]$PackageRoot, [string[]]$Files) {
    $destination = Join-Path $noticeRoot $Id
    foreach ($relative in $Files) {
        $source = Join-Path $PackageRoot $relative
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing upstream notice: $Id/$relative" }
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
        $link = 'third-party-licenses/' + $Id + '/' + $relative.Replace('\', '/')
        $inventory.Add('- [' + $Id + '/' + $relative + '](' + $link + ') — SHA-256 `' + (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() + '`')
    }
}

foreach ($key in ($assets.libraries.Keys | Sort-Object)) {
    $library = $assets.libraries[$key]
    if ($library.type -ne 'package') { continue }
    $root = $null
    foreach ($candidate in $packageRoots) {
        $directory = Join-Path $candidate $library.path
        if (Test-Path -LiteralPath $directory -PathType Container) { $root = $directory; break }
    }
    if (!$root) { throw "Restored dependency is unavailable: $key" }
    $files = @($library.files | Where-Object { $_ -match '(?i)(^|/)(license[^/]*|notice[^/]*|third-party-notices[^/]*)$' })
    # Preserve the package's declared license/copyright as well as its notice texts.
    $files += @(Get-ChildItem -LiteralPath $root -Filter '*.nuspec' -File | Select-Object -ExpandProperty Name)
    if ($files.Count -eq 0) { throw "No license metadata found for $key" }
    Copy-Notices ($key.Replace('/', '-')) $root $files
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'Stretto.App.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
if (!$runtimeConfig.runtimeOptions.ContainsKey('includedFrameworks')) { throw 'Expected a self-contained runtime inventory.' }
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $package = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $root = $null
    foreach ($candidate in $packageRoots) {
        $directory = Join-Path $candidate ($package + '/' + $framework.version)
        if (Test-Path -LiteralPath $directory -PathType Container) { $root = $directory; break }
    }
    if (!$root) { throw "Runtime package not found: $package/$($framework.version)" }
    Copy-Notices ($package + '-' + $framework.version) $root @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')
}
$inventory.Add('')
$deps = Get-Content -LiteralPath (Join-Path $payload 'Stretto.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($key in $deps.libraries.Keys) {
    if (!$key.StartsWith('runtimepack.Microsoft.Windows.SDK.NET.Ref/')) { continue }
    $version = $key.Split('/')[1]
    $root = $null
    foreach ($candidate in $packageRoots) {
        $directory = Join-Path $candidate ('microsoft.windows.sdk.net.ref/' + $version)
        if (Test-Path -LiteralPath $directory) { $root = $directory; break }
    }
    if (!$root) { throw "Windows SDK .NET runtime package not found: $key" }
    Copy-Notices ('Microsoft.Windows.SDK.NET.Ref-' + $version) $root @('microsoft.windows.sdk.net.ref.nuspec')
    Copy-Notices ('Microsoft.Windows.SDK.NET.Ref-' + $version) (Join-Path $PSScriptRoot 'upstream-licenses') @('windows-sdk-license.rtf')
}
$inventory.Add('System.Security.Cryptography.ProtectedData declares MIT in its included NuGet metadata. The .NET runtime LICENSE.TXT supplies the accompanying Microsoft/.NET MIT license text. Windows App SDK components use their own supplied Microsoft terms; Stretto does not relicense them.')
$inventory | Set-Content -LiteralPath (Join-Path $payload 'THIRD-PARTY-NOTICES.md') -Encoding utf8
Write-Host ('Included dependency license inventory: ' + $noticeRoot)
