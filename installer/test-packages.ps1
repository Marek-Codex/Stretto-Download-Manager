[CmdletBinding()]
param([ValidateRange(60,900)][int]$TimeoutSeconds = 360)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $root 'Stretto.App\Stretto.App.csproj') -Raw
$version = [version][string]$project.Project.PropertyGroup.Version
$packageVersion = '{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, [Math]::Max(0,$version.Build), [Math]::Max(0,$version.Revision)
$artifacts = Join-Path $root "artifacts\$packageVersion"
$packages = Join-Path $artifacts 'test-signed'
foreach ($name in @("Stretto-$packageVersion-win-x64-setup.test-signed.exe", "Stretto-$packageVersion-win-x64.test-signed.msix", 'Stretto-Test-Signing.cer')) {
    if (!(Test-Path -LiteralPath (Join-Path $packages $name))) { throw "Missing test package: $name" }
}
$wsb = Get-Command wsb.exe -ErrorAction Stop
$existing = & $wsb.Source list
if ($LASTEXITCODE -ne 0) { throw 'Could not query Windows Sandbox.' }
if (($existing -join '') -match '[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}') { throw 'A Sandbox is already running. This test will not close an existing session.' }
$run = Join-Path $env:TEMP ('StrettoPreview-' + [guid]::NewGuid().ToString('N'))
$inputDir = Join-Path $run 'input'
$outputDir = Join-Path $run 'results'
New-Item -ItemType Directory -Path $inputDir,$outputDir -Force | Out-Null
Copy-Item (Join-Path $packages '*') $inputDir
Copy-Item (Join-Path $PSScriptRoot 'sandbox-smoke.ps1') $inputDir
& dotnet publish (Join-Path $root 'Stretto.Check\Stretto.Check.csproj') -c Release -r win-x64 --self-contained true --disable-build-servers -o (Join-Path $inputDir 'engine-check') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not publish the isolated engine harness.' }
$escapedInput = [Security.SecurityElement]::Escape($inputDir)
$escapedOutput = [Security.SecurityElement]::Escape($outputDir)
$config = "<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MappedFolders><MappedFolder><HostFolder>$escapedInput</HostFolder><SandboxFolder>C:\StrettoInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder><MappedFolder><HostFolder>$escapedOutput</HostFolder><SandboxFolder>C:\StrettoResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder></MappedFolders><LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\StrettoInput\sandbox-smoke.ps1</Command></LogonCommand></Configuration>"
$started = (& $wsb.Source start --config $config --raw) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not start Windows Sandbox.' }
$id = ($started | ConvertFrom-Json).Id
if ($id -notmatch '^[0-9a-fA-F-]{36}$') { throw 'Sandbox did not return an ID.' }
Set-Content -LiteralPath (Join-Path $artifacts 'sandbox-run-path.txt') -Value $run
Write-Host "Disposable test session: $id; results: $outputDir"
try {
    & $wsb.Source connect --id $id
    if ($LASTEXITCODE -ne 0) { throw 'Could not connect the guest user session.' }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $report = Join-Path $outputDir 'result.json'
    do {
        Start-Sleep -Milliseconds 750
        $result = $null
        if (Test-Path -LiteralPath $report) {
            try { $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json } catch { }
        }
        if ($result -and $result.status -ne 'running') { break }
    } until ((Get-Date) -gt $deadline)
    if (!$result -or $result.status -eq 'running') { throw 'Sandbox smoke test timed out; retained results are in the printed directory.' }
    Copy-Item -LiteralPath $report -Destination (Join-Path $artifacts 'package-smoke-result.json') -Force
    $result | ConvertTo-Json -Depth 5 | Write-Host
    if ($result.status -ne 'passed') { throw "Package smoke failed: $($result.error)" }
} finally {
    # Only this invocation's disposable guest is stopped. No existing user VM is touched.
    & $wsb.Source stop --id $id
}
