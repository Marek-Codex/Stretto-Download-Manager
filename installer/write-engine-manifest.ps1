[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$corePath = Join-Path $PayloadPath 'Stretto.Core.dll'
if (!(Test-Path -LiteralPath $corePath -PathType Leaf)) { throw 'The published engine assembly is missing.' }

# Fingerprint the bytes we ship, with a separate source fingerprint for maintainers.
# No installation identifier, private signing material or machine-specific paths belong here.
$sourceRoot = Join-Path $projectRoot 'Stretto.Core'
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
    $_.Extension -in @('.cs', '.csproj') -and
    [IO.Path]::GetRelativePath($sourceRoot, $_.FullName).Replace('\', '/') -notmatch '(^|/)(bin|obj)/'
} | ForEach-Object {
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($projectRoot, $_.FullName).Replace('\', '/'); Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
} | Sort-Object Path)
$canonical = ($sources | ForEach-Object { $_.Path + [char]0 + $_.Hash }) -join "`n"
$sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
$git = Get-Command git.exe -ErrorAction SilentlyContinue
$gitPath = if ($git) { $git.Source } else { Join-Path $env:ProgramFiles 'Git\cmd\git.exe' }
$commit = $null
$modified = $null
if (Test-Path -LiteralPath $gitPath) {
    $revision = & $gitPath -C $projectRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0) {
        $commit = $revision.Trim()
        $changes = & $gitPath -C $projectRoot status --porcelain -- Stretto.Core
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect engine source status.' }
        $modified = [bool]$changes
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    name = 'Stretto Transfer Engine'
    assembly = 'Stretto.Core.dll'
    assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($corePath).Version.ToString()
    sha256 = (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceCommit = $commit
    sourceModified = $modified
    sourceSha256 = $sourceHash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PayloadPath 'ENGINE-BUILD.json') -Encoding utf8
