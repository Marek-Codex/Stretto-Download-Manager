# Runs only inside a disposable Windows Sandbox. The host maps a package-only
# input folder read-only and a separate result folder; no personal app data is used.
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'This smoke test is restricted to Windows Sandbox.' }
$results = [ordered]@{ startedUtc = [DateTime]::UtcNow.ToString('o'); checks = @(); status = 'running' }
function Record([string]$Check) {
    $results.checks += $Check
    $results | ConvertTo-Json -Depth 5 | Set-Content C:\StrettoResults\result.json
}
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Start-App([string]$Path) {
    $process = Start-Process -FilePath $Path -PassThru
    $deadline = (Get-Date).AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        Assert (!$process.HasExited) 'The app exited before presenting a window.'
    } until ($process.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
    Assert ($process.MainWindowHandle -ne 0) 'The app did not present a window within 25 seconds.'
    return $process
}
function Check-InstalledEngine([string]$InstallPath, [string]$Label) {
    $harness = Join-Path $env:TEMP ('StrettoEngine-' + [guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath C:\StrettoInput\engine-check -Destination $harness -Recurse
    Copy-Item -LiteralPath (Join-Path $InstallPath 'Stretto.Core.dll') -Destination $harness -Force
    $server = Start-Job {
        $body = New-Object byte[] 1048576
        for ($i = 0; $i -lt $body.Length; $i++) { $body[$i] = $i % 251 }
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add('http://127.0.0.1:8127/')
        $listener.Start()
        try {
            while ($listener.IsListening) {
                $context = $listener.GetContext()
                $response = $context.Response
                $response.ContentType = 'application/octet-stream'
                $response.Headers['Accept-Ranges'] = 'bytes'
                $response.Headers['ETag'] = '"sandbox-fixture-v1"'
                $start = 0; $end = $body.Length - 1
                if ($context.Request.Headers['Range'] -match '^bytes=(\d+)-(\d*)$') {
                    $start = [int]$Matches[1]
                    if ($Matches[2]) { $end = [int]$Matches[2] }
                    $response.StatusCode = 206
                    $response.Headers['Content-Range'] = "bytes $start-$end/$($body.Length)"
                }
                $response.ContentLength64 = $end - $start + 1
                if ($context.Request.HttpMethod -ne 'HEAD') { $response.OutputStream.Write($body, $start, $end - $start + 1) }
                $response.Close()
            }
        } finally { $listener.Close() }
    }
    try {
        Start-Sleep -Seconds 3
        $output = Join-Path $harness 'downloads'
        $check = Start-Process -FilePath (Join-Path $harness 'Stretto.Check.exe') -ArgumentList @('http://127.0.0.1:8127/fixture.bin', '-o', $output, '-n', '4') -PassThru -RedirectStandardOutput (Join-Path $harness 'stdout.txt') -RedirectStandardError (Join-Path $harness 'stderr.txt')
        if (!$check.WaitForExit(45000)) { $check.Kill(); throw 'Installed engine download timed out.' }
        Assert ($check.ExitCode -eq 0) "Installed $Label engine download failed."
        $file = @(Get-ChildItem $output -Filter '*.bin' -File)
        Assert ($file.Count -eq 1) 'Expected one downloaded fixture.'
        $bytes = [IO.File]::ReadAllBytes($file[0].FullName)
        Assert ($bytes.Length -eq 1048576) 'Downloaded fixture length differs.'
        for ($i = 0; $i -lt $bytes.Length; $i++) { if ($bytes[$i] -ne ($i % 251)) { throw "Wrong downloaded byte at $i" } }
        Record "$Label installed engine downloaded and verified every byte of a 1 MiB local fixture using four connections"
    } finally {
        Stop-Job $server -ErrorAction SilentlyContinue
        Remove-Job $server -Force -ErrorAction SilentlyContinue
    }
}
try {
    Record 'Windows Sandbox identity guard passed'
    $inputRoot = 'C:\StrettoInput'
    $exe = @(Get-ChildItem $inputRoot -Filter '*.test-signed.exe')
    $msix = @(Get-ChildItem $inputRoot -Filter '*.test-signed.msix')
    Assert ($exe.Count -eq 1 -and $msix.Count -eq 1) 'Expected one EXE and one MSIX.'
    $install = Join-Path $env:LOCALAPPDATA 'Programs\Stretto'
    Assert (!(Test-Path $install)) 'Expected a clean EXE installation directory.'
    $setup = Start-Process -FilePath $exe[0].FullName -ArgumentList '/S' -Wait -PassThru
    Assert ($setup.ExitCode -eq 0) 'EXE installation failed.'
    foreach ($file in @('Stretto.App.exe', 'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'extensions\chromium\manifest.json', 'extensions\stretto-firefox.xpi')) {
        Assert (Test-Path (Join-Path $install $file)) "Missing installed file: $file"
    }
    Record 'EXE installed with licensing files and both extension distributions'
    Check-InstalledEngine $install 'EXE'
    $app = Start-App (Join-Path $install 'Stretto.App.exe')
    Record 'EXE app presented a window on clean Windows without developer runtimes'
    Stop-Process -Id $app.Id -Force
    Start-Sleep -Milliseconds 500
    $startup = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Stretto -ErrorAction SilentlyContinue
    Assert ($null -eq $startup) 'Startup was enabled without opting in.'
    Record 'EXE startup remains opt-in'
    $setup = Start-Process -FilePath $exe[0].FullName -ArgumentList '/S' -Wait -PassThru
    Assert ($setup.ExitCode -eq 0) 'Same-version EXE reinstall failed.'
    Record 'Same-version EXE reinstall passed (not a cross-version upgrade test)'
    Start-Process -FilePath (Join-Path $install 'Uninstall.exe') -ArgumentList '/S' -Wait | Out-Null
    $deadline = (Get-Date).AddSeconds(25)
    while ((Test-Path (Join-Path $install 'Stretto.App.exe')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Assert (!(Test-Path (Join-Path $install 'Stretto.App.exe'))) 'EXE uninstall left the app executable.'
    Record 'EXE uninstall removed the executable'

    # Trust is confined to this disposable guest, never the user's host computer.
    Import-Certificate -FilePath (Join-Path $inputRoot 'Stretto-Test-Signing.cer') -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Add-AppxPackage -Path $msix[0].FullName
    $package = Get-AppxPackage -Name MarekCodex.Stretto
    Assert ($null -ne $package) 'MSIX registration was not found.'
    Record 'Test-signed MSIX installed with certificate trusted only in the disposable guest'
    Check-InstalledEngine $package.InstallLocation 'MSIX'
    Start-Process ('shell:AppsFolder\' + $package.PackageFamilyName + '!Stretto')
    Start-Sleep -Seconds 15
    $apps = @(Get-Process -Name Stretto.App -ErrorAction SilentlyContinue)
    Assert ($apps.Count -gt 0) 'MSIX app did not remain running.'
    Assert (@($apps | Where-Object { $_.MainWindowHandle -ne 0 }).Count -gt 0) 'MSIX app did not present a window.'
    Record 'MSIX activation presented a window'
    $apps | Stop-Process -Force
    Remove-AppxPackage -Package $package.PackageFullName
    Assert ($null -eq (Get-AppxPackage -Name MarekCodex.Stretto)) 'MSIX remained registered after uninstall.'
    Record 'MSIX uninstall removed package registration'
    $results.status = 'passed'
} catch {
    $results.status = 'failed'
    $results.error = $_.Exception.Message
} finally {
    $results.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $results | ConvertTo-Json -Depth 5 | Set-Content C:\StrettoResults\result.json
}
