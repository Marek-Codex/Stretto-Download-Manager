# Windows packages

Build from PowerShell on Windows:

```powershell
.\installer\build-packages.ps1
```

The default builds both x64 formats under `artifacts/<version>/`, plus SHA-256 checksums. `-Format Exe` and `-Format Msix` build either format alone. The version comes from `Stretto.App.csproj`; the package adds the fourth numeric component.

Successful builds remove their temporary staging directory. Use `-KeepStaging` to keep the self-contained payload for debugging. Failed staging directories are retained for diagnosis and can be removed once inspected.

Requirements: .NET 10 SDK, NSIS for EXE, and Windows SDK MakeAppx for MSIX. The script finds NSIS in its usual install directory and MakeAppx/SignTool in either the installed SDK or restored `Microsoft.Windows.SDK.BuildTools` NuGet package; no global SDK PATH entries are necessary.

Both packages include .NET and Windows App SDK. They require x64 Windows 10 1809 or later; users do not need an SDK or a separately installed .NET runtime. ffmpeg and yt-dlp remain optional components installed through the app. Browser extensions are bundled; the Firefox XPI remains unsigned unless separately signed through Mozilla.

## EXE

NSIS installs to `%LOCALAPPDATA%\Programs\Stretto`, creates a Start menu shortcut, registers `stretto:`, and adds an Installed apps entry. It needs no administrator rights, installs no service, and does not enable startup. Close Stretto from its tray menu before an upgrade or uninstall. Uninstall preserves downloaded files and `%LOCALAPPDATA%\Stretto` settings/tools.

The installer supports `/S` for silent installation; the uninstaller supports `/S` too. No custom installation directory is offered. This keeps per-user ownership and uninstall boundaries predictable.

## MSIX

The manifest declares a full-trust desktop app, the `stretto:` protocol, and an initially disabled startup task. Stretto uses Windows StartupTask APIs when packaged and the per-user Run key when unpackaged. Windows can veto startup if it has been disabled in Startup apps; the app records the actual enabled state.

Packaged settings/tools are under `ApplicationData.Current.LocalFolder/Stretto`. Bundled extensions are copied there at launch so unpacked-browser-extension paths survive MSIX updates. Uninstalling MSIX normally removes its app data; exported downloads outside the package data folder remain. EXE and MSIX have separate settings. Switching formats is not an automatic migration; install one distribution at a time, re-pair the browser and reload its extension from the new setup path.

Building MSIX does not configure automatic updates. The Store or a separately hosted `.appinstaller` feed can handle updates once the distribution channel is chosen. Stretto's current update check opens its GitHub release page and does not install updates.

## Identity and signing

Without a certificate the files are explicitly named `.unsigned.exe` and `.unsigned.msix`. They are build/review artifacts. An unsigned MSIX cannot be installed through normal App Installer; no test certificate is generated or trusted by this script.

Before distribution, choose the permanent publisher/identity values. Defaults (`CN=MarekCodex`, `MarekCodex.Stretto`) are development values, not a reserved Store identity. A Store build must use the exact identity/publisher assigned in Partner Center. Direct distribution needs a code-signing certificate trusted by the target machine.

To sign with a certificate already in the current user's Personal certificate store:

```powershell
.\installer\build-packages.ps1 `
  -Publisher 'CN=Your exact certificate subject' `
  -PublisherDisplayName 'Your publisher name' `
  -IdentityName 'Your.ReservedIdentity' `
  -CertificateThumbprint 'YOUR_CERTIFICATE_THUMBPRINT'
```

The script validates the subject/private key, signs both artifacts with SHA-256 and an RFC 3161 timestamp, and verifies signatures. It never exports private keys. Keep certificates and secrets outside the repository. Use the same package identity/publisher for future upgrades.

For private testing, a self-signed certificate can identify `CN=MarekCodex, O=Codex Plexus` without an account signup. This is a self-declared test identity, not a verified organization. Testers must explicitly trust the public `.cer` certificate in Local Machine / Trusted People before normal MSIX installation; never distribute the private key/PFX. The signer also needs that trust to pass this script's chain-verification step. No certificate is created or trusted automatically.

For public distribution, Microsoft Store registration currently has a free onboarding flow at [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com/); Microsoft signs MSIX submissions for Store distribution. That does not provide an EXE-signing certificate. Azure Artifact Signing provides direct-download signing after identity verification (Basic currently USD 9.99/month). Individuals currently need to be in the US or Canada and their legal identity appears on the public certificate. The certificate-store parameter above is for locally held certificates; Azure signing uses its own SignTool integration and will need separate configuration if selected. See [Store registration](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account), [Artifact Signing setup](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) and [pricing](https://azure.microsoft.com/en-us/products/artifact-signing).

## Acceptance before release

### Optional self-signed test downloads

After building both unsigned formats, `./installer/sign-test-packages.ps1 -CreateCertificate`
creates a one-year test-only certificate in Current User / Personal with a
non-exportable private key. It signs copies into `artifacts/<version>/test-signed/`,
exports only a public `.cer`, and produces tester instructions and checksums.
Later runs reuse the testing certificate; use `-CertificateThumbprint` if selecting
between more than one. It never installs a certificate into a trust store.

This path is intentionally separate from trusted release signing. It accepts the
specific Windows untrusted-root result for the expected signer, but rejects other
signature verification failures. Deliberately modified EXE/MSIX copies were rejected
in the local verification check. Clean-machine installation is still untested.
There is no trusted timestamp, so certificate expiry matters. Publish the `.cer`
fingerprint with the release; never upload a PFX or private key. Testers must choose
whether to trust the certificate for MSIX installation. The EXE does not require
certificate import and can still show Windows reputation warnings.

These packages use the development identity `MarekCodex.Stretto` / `CN=MarekCodex`.
They are not signed Store builds and migration to the final Store identity is not
guaranteed. The generated `TESTING.md` explains certificate trust and later removal.

### Distribution acceptance

Every installer payload includes `ENGINE-BUILD.json`, generated from the published
`Stretto.Core.dll` and current Core sources. It records the assembly SHA-256,
assembly version, Git commit/source-modification status and Core source hash.
This identifies a build; it does not establish source provenance or prevent copying.
See [the engine engineering report](../docs/ENGINE-ENGINEERING-2026-10-28.md) for
the measured benchmark, scope and limits.

Current results and the uninstall-test approval block are recorded in [finishing work](../docs/FINISHING-WORK-2026-10-28.md).

Build success and MakeAppx validation are not installation tests. On a clean Windows account/VM without SDKs, verify both signed artifacts independently: launch, download, tray exit, opt-in startup and Windows disabling it, browser launch/pairing, optional tool installation, restart/resume, upgrade from a previous signed package, and uninstall. Check that browser extension paths remain valid after an MSIX upgrade. Test EXE installation/uninstallation in both interactive and silent modes. Do not label these packages release-ready until those checks and the engine/browser findings in the release review are resolved.

References: [self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps), [desktop manifest extensions](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions), [MSIX signing](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview).
