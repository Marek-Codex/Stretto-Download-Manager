[CmdletBinding()]
param(
    [ValidateSet('Both', 'Exe', 'Msix')][string]$Format = 'Both',
    [string]$Publisher = 'CN=MarekCodex',
    [string]$PublisherDisplayName = 'MarekCodex',
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$IdentityName = 'MarekCodex.Stretto',
    [string]$CertificateThumbprint,
    [switch]$KeepStaging,
    [uri]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $projectRoot 'Stretto.App\Stretto.App.csproj') -Raw
$version = [version][string]$project.Project.PropertyGroup.Version
$packageVersion = '{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, [Math]::Max(0, $version.Build), [Math]::Max(0, $version.Revision)
$artifactRoot = Join-Path $projectRoot "artifacts\$packageVersion"
# Fresh staging on every run; no recursive cleanup of a caller-supplied path.
$stagingRoot = Join-Path $artifactRoot ('staging-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stagingRoot 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with code $LASTEXITCODE" }
}

function Find-SdkTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $candidates = @(@(
        Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\$Name") -ErrorAction SilentlyContinue
        Get-ChildItem (Join-Path $nugetRoot "microsoft.windows.sdk.buildtools\*\bin\*\x64\$Name") -ErrorAction SilentlyContinue
    ) | Sort-Object FullName -Descending)
    if ($candidates.Count -eq 0) { throw "$Name not found. Restore/build the app to obtain Microsoft.Windows.SDK.BuildTools, or install Windows SDK tools." }
    return $candidates[0].FullName
}

Invoke-Checked 'dotnet' @('publish', (Join-Path $projectRoot 'Stretto.App\Stretto.App.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
    '-p:DebugType=None', '-p:DebugSymbols=false',
    '--disable-build-servers', '-o', $payload)

foreach ($required in @('Stretto.App.exe', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'Assets\stretto.ico',
    'App.xbf', 'MainWindow.xbf', 'Stretto.App.pri', 'Assets\Icons.xbf', 'Assets\Density.xbf', 'Dialogs\NewDownloadDialog.xbf')) {
    if (!(Test-Path (Join-Path $payload $required))) { throw "Incomplete self-contained payload: $required is absent." }
}

# Assemble extensions explicitly so publish does not depend on build-output copy behavior.
$extensionRoot = Join-Path $payload 'extensions'
$chromium = Join-Path $extensionRoot 'chromium'
$firefox = Join-Path $extensionRoot 'firefox'
New-Item -ItemType Directory -Path $chromium, $firefox -Force | Out-Null
Copy-Item (Join-Path $projectRoot 'extensions\chromium\*') $chromium -Recurse -Force
foreach ($pattern in @('common.js', 'media.js', 'popup.*', 'options.*', 'icon*.png')) {
    Copy-Item (Join-Path $chromium $pattern) $firefox -Force
}
Copy-Item (Join-Path $projectRoot 'extensions\firefox\*') $firefox -Force
$xpi = Join-Path $extensionRoot 'stretto-firefox.xpi'
if (Test-Path $xpi) { Remove-Item -LiteralPath $xpi }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($firefox, $xpi)
Copy-Item (Join-Path $projectRoot 'LICENSE') $payload
Copy-Item (Join-Path $projectRoot 'PRIVACY.md') $payload
New-Item -ItemType Directory -Path (Join-Path $payload 'docs') -Force | Out-Null
Copy-Item (Join-Path $projectRoot 'docs\USER-GUIDE.md') (Join-Path $payload 'docs')
& (Join-Path $PSScriptRoot 'write-engine-manifest.ps1') -PayloadPath $payload

$signTool = $null
if ($CertificateThumbprint) {
    $CertificateThumbprint = $CertificateThumbprint.Replace(' ', '')
    $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
    if ($certificate.Subject -cne $Publisher) { throw 'Certificate subject must exactly match Publisher.' }
    if (!$certificate.HasPrivateKey) { throw 'Signing certificate has no private key.' }
    $signTool = Find-SdkTool 'signtool.exe'
}
function Sign-Artifact([string]$Path) {
    if ($signTool) {
        Invoke-Checked $signTool @('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl.AbsoluteUri, '/td', 'SHA256', $Path)
        Invoke-Checked $signTool @('verify', '/pa', $Path)
    }
}

$suffix = if ($signTool) { '' } else { '.unsigned' }
$outputs = [System.Collections.Generic.List[string]]::new()
if ($Format -in @('Both', 'Exe')) {
    $nsis = Get-Command makensis.exe -ErrorAction SilentlyContinue
    $nsisPath = if ($nsis) { $nsis.Source } else { Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe' }
    if (!(Test-Path $nsisPath)) { throw 'NSIS was not found. Install NSIS or put makensis.exe on PATH.' }
    $exePath = Join-Path $artifactRoot "Stretto-$packageVersion-win-x64-setup$suffix.exe"
    Invoke-Checked $nsisPath @('/V2', "/DSTRETTO_VERSION=$packageVersion", "/DSTRETTO_PAYLOAD=$payload",
        "/DSTRETTO_OUTPUT=$exePath", "/DSTRETTO_PUBLISHER=$PublisherDisplayName", (Join-Path $PSScriptRoot 'stretto.nsi'))
    Sign-Artifact $exePath
    $outputs.Add($exePath)
}

if ($Format -in @('Both', 'Msix')) {
    $makeAppx = Find-SdkTool 'makeappx.exe'
    $msixStage = Join-Path $stagingRoot 'msix'
    Copy-Item -LiteralPath $payload -Destination $msixStage -Recurse
    $assets = Join-Path $msixStage 'PackageAssets'
    New-Item -ItemType Directory -Path $assets | Out-Null
    Add-Type -AssemblyName System.Drawing
    $source = [System.Drawing.Image]::FromFile((Join-Path $payload 'Assets\stretto-icon.png'))
    try {
        foreach ($asset in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
            $bitmap = [System.Drawing.Bitmap]::new([int]$asset[1], [int]$asset[1])
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($source, 0, 0, [int]$asset[1], [int]$asset[1])
                $bitmap.Save((Join-Path $assets ($asset[0] + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
        }
    } finally { $source.Dispose() }
    $manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
    $manifest = $manifest.Replace('@IDENTITY@', [System.Security.SecurityElement]::Escape($IdentityName)).
        Replace('@PUBLISHER@', [System.Security.SecurityElement]::Escape($Publisher)).
        Replace('@PUBLISHER_DISPLAY@', [System.Security.SecurityElement]::Escape($PublisherDisplayName)).
        Replace('@VERSION@', $packageVersion)
    Set-Content -LiteralPath (Join-Path $msixStage 'AppxManifest.xml') -Value $manifest -Encoding utf8
    $msixPath = Join-Path $artifactRoot "Stretto-$packageVersion-win-x64$suffix.msix"
    Invoke-Checked $makeAppx @('pack', '/d', $msixStage, '/p', $msixPath, '/o')
    Sign-Artifact $msixPath
    $outputs.Add($msixPath)
}

$checksums = foreach ($path in $outputs) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path $path -Leaf)
}
$checksums | Set-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding ascii
if (!$KeepStaging) {
    $resolvedArtifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    $resolvedStagingRoot = (Resolve-Path -LiteralPath $stagingRoot).Path
    if ([IO.Path]::GetDirectoryName($resolvedStagingRoot) -ne $resolvedArtifactRoot -or
        [IO.Path]::GetFileName($resolvedStagingRoot) -notmatch '^staging-[0-9a-f]{32}$') {
        throw 'Refusing to clean a staging path outside this artifact directory.'
    }
    Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force
}
$outputs | ForEach-Object { Write-Host "Built: $_" }
if (!$signTool) { Write-Warning 'Unsigned build artifacts only. MSIX must be signed by a trusted publisher before normal installation.' }
