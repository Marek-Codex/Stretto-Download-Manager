[CmdletBinding()]
param(
    [string]$CertificateThumbprint,
    [switch]$CreateCertificate
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $projectRoot 'Stretto.App\Stretto.App.csproj') -Raw
$version = [version][string]$project.Project.PropertyGroup.Version
$packageVersion = '{0}.{1}.{2}.{3}' -f $version.Major, $version.Minor, [Math]::Max(0, $version.Build), [Math]::Max(0, $version.Revision)
$artifactRoot = Join-Path $projectRoot "artifacts\$packageVersion"
$subject = 'CN=MarekCodex'
$friendlyName = 'Stretto testing only - self-signed publisher'

# PowerShell can flatten CERT_E_UNTRUSTEDROOT into UnknownError. Read the actual
# Windows trust result instead of accepting every error with an optimistic shrug.
if (!('StrettoTestSignature' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StrettoTestSignature {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileInfo {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle, Subject;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct TrustData {
        public uint Size;
        public IntPtr Policy, Sip;
        public uint UI, Revocation, Union;
        public IntPtr File;
        public uint StateAction;
        public IntPtr State, Url;
        public uint Flags, Context;
        public IntPtr Settings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    public static uint Verify(string path) {
        var file = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = path };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        Marshal.StructureToPtr(file, memory, false);
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var data = new TrustData {
            Size = (uint)Marshal.SizeOf<TrustData>(), UI = 2, Union = 1,
            File = memory, StateAction = 1, Flags = 0x1000
        };
        try { return WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
        finally {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<FileInfo>(memory);
            Marshal.FreeHGlobal(memory);
        }
    }
}
'@
}

# Test signing is deliberately separate from the trusted-release build path.
# Creating a certificate is not permission to add it to anyone's trust stores.
$inputs = @(
    "Stretto-$packageVersion-win-x64-setup.unsigned.exe",
    "Stretto-$packageVersion-win-x64.unsigned.msix"
)
foreach ($file in $inputs) {
    if (!(Test-Path -LiteralPath (Join-Path $artifactRoot $file))) {
        throw "Build the unsigned packages first: missing $file"
    }
}
$command = Get-Command signtool.exe -ErrorAction SilentlyContinue
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$signTool = if ($command) { $command.Source } else {
    $candidates = @(
        Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue
        Get-ChildItem (Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools\*\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue
    ) | Sort-Object FullName -Descending
    if (!$candidates) { throw 'SignTool was not found. Restore/build the app or install Windows SDK tools.' }
    $candidates[0].FullName
}

if ($CertificateThumbprint) {
    $certificate = Get-Item "Cert:\CurrentUser\My\$($CertificateThumbprint.Replace(' ', ''))"
} else {
    $existing = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -ceq $subject -and $_.FriendlyName -eq $friendlyName -and
        $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7)
    })
    if ($existing.Count -eq 1) { $certificate = $existing[0] }
    elseif ($existing.Count -gt 1) { throw 'Multiple test certificates exist. Select one explicitly with -CertificateThumbprint.' }
    elseif (!$CreateCertificate) { throw 'No test certificate exists. Use -CreateCertificate to create a local, non-exportable testing key.' }
    else {
        $certificate = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName $friendlyName `
            -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 `
            -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -NotAfter (Get-Date).AddYears(1)
    }
}
if ($certificate.Subject -cne $subject -or $certificate.Issuer -cne $subject -or !$certificate.HasPrivateKey) {
    throw 'Expected a self-signed CN=MarekCodex certificate with a local private key.'
}
if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'The certificate is outside its validity period.' }

$output = Join-Path $artifactRoot 'test-signed'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$publicCertificate = Join-Path $output 'Stretto-Test-Signing.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate -Type CERT -Force | Out-Null
$public = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($publicCertificate)
if ($public.HasPrivateKey) { throw 'The public certificate export unexpectedly contains a private key.' }
$certificateSha256 = (Get-FileHash -LiteralPath $publicCertificate -Algorithm SHA256).Hash.ToLowerInvariant()

$signedFiles = foreach ($file in $inputs) {
    $destination = Join-Path $output ($file.Replace('.unsigned.', '.test-signed.'))
    Copy-Item -LiteralPath (Join-Path $artifactRoot $file) -Destination $destination -Force
    # No trusted timestamp on this test certificate. Expiry remains visible and documented.
    & $signTool sign /sha1 $certificate.Thumbprint /s My /fd SHA256 $destination | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $destination" }
    $signature = Get-AuthenticodeSignature -LiteralPath $destination
    if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "Unexpected signing certificate: $destination"
    }
    $trustResult = [StrettoTestSignature]::Verify($destination)
    if ($signature.Status -eq 'HashMismatch' -or $trustResult -notin @([uint32]0, [uint32]0x800B0109L)) {
        throw ('Signature check failed (0x{0:X8}): {1}' -f $trustResult, $signature.StatusMessage)
    }
    Write-Host ('Windows verification for {0}: 0x{1:X8} (0x800B0109 means the self-signed root is not trusted)' -f (Split-Path $destination -Leaf), $trustResult)
    $destination
}

$instructions = @"
# Stretto self-signed testing downloads

These are alpha testing packages. The publisher certificate is self-signed, not
independently verified or trusted by Windows by default. Installing the public
certificate is an explicit trust decision, not a routine prerequisite for all users.
The private signing key is not included.

- Subject: $($certificate.Subject)
- Certificate thumbprint (SHA-1 identifier): $($certificate.Thumbprint)
- Public certificate SHA-256: $certificateSha256
- Certificate expires (UTC): $($certificate.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))
- No trusted timestamp is applied. These are not the final Store packages.

## Choose one installation format

The EXE is the per-user installer. It does not require importing this certificate;
Windows can still show publisher/reputation warnings for this self-signed preview.

The MSIX requires trust in this exact certificate. Check the certificate fingerprint
against the release notes before importing it. If you choose to trust it, open the
public Stretto-Test-Signing.cer, choose Install Certificate, select Local Machine,
and place it in Trusted People. This requires administrator approval and affects
all users on that computer. Do not put it in Trusted Root Certification Authorities.
Then open the test-signed MSIX with App Installer. Do not disable Windows protections.
If Windows still rejects the package, report the error rather than bypassing it.

This uses a development package identity. Migration to the final Microsoft Store
package is not guaranteed; exported settings/history and a separate install may be
needed. EXE and MSIX use separate settings. Install one distribution at a time.

## When testing is over

Uninstall the test app normally. If you imported the certificate, open Manage computer
certificates, find this exact thumbprint under Trusted People / Certificates, and
remove it when no installed test app needs it. Keep other certificates untouched.
Downloaded files outside app data remain yours; MSIX removal normally removes its
Windows-managed app data. Export anything you want to keep before uninstalling.

See Microsoft's certificate guidance:
https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing

Final clean-machine install/upgrade/uninstall and browser acceptance remain pending.
Checksum files establish file integrity against a published value, not publisher trust.
"@
Set-Content -LiteralPath (Join-Path $output 'TESTING.md') -Value $instructions -Encoding utf8
$filesToHash = @($signedFiles) + @($publicCertificate, (Join-Path $output 'TESTING.md'))
$checksums = foreach ($file in $filesToHash) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
$checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Test package set: $output"
Write-Host "Public certificate SHA-256: $certificateSha256"
Write-Warning 'Self-signed testing only. No trust stores were changed and no private key was exported.'
