# Installing and using Stretto

Stretto is a Windows download manager. Plain HTTP/HTTPS and FTP downloads do not require optional video components or a paid account.

## Installation

Use one installation format at a time:

- **Microsoft Store / MSIX:** Windows manages installation and removal. Use the Store listing when available. A direct MSIX download needs a trusted signature; an `.unsigned.msix` is a build artifact, not a normal installable release.
- **EXE installer:** installs for your Windows account, without administrator rights, under your local Programs folder. The .NET and Windows App SDK runtimes are included. Exit Stretto from its notification-area menu before installing an upgrade.

These formats have separate settings. When switching, reload/reinstall the browser extension using the new copy's setup guide and pair it with that app. MSIX and EXE installation do not automatically migrate settings or download history between formats.

## First download

Choose **Add download**, paste an HTTP, HTTPS or FTP link, and review the filename and destination. Browser captures show known size/type information; pasted links show their host. Stretto checks server metadata when the download begins rather than making an extra request while you edit the form.

**Download** starts now. **Add to queue** schedules or parks it. **Options** contains per-download login and proxy overrides. The expected-folder hint follows the filename and file-type rules; a server-supplied filename/type can change that classification. Press Escape to dismiss the New download window.

Pause and resume from a row or its context menu. **Progress window** shows one transfer separately; closing it leaves the transfer running. Failed transfers show their error, and **Retry** attempts the transfer again. If a link expired, use the app's link-refresh action or start again from its website.

Finished files appear in **Library**. Open the file, show it in its folder, or use Details to check a checksum. Opening a downloaded file runs it with the application Windows associates with that file type.

## Browser integration

Choose **Browser → Set up the extension**. In step one, choose your browser, open it, copy the single setup-page address, and prepare its extensions page. Step two loads the bundled extension; step three confirms the local connection. Startup is optional in Settings; it is not silently enabled by the installer.

Chromium capture leaves POST and unrecognized requests with the browser. When Stretto declines or stops answering, the original browser request continues rather than being restarted by URL. The original is cancelled only after Stretto accepts. If your browser asks where to save every download, it can show its Save dialog while Stretto's confirmation is open.

Some one-use links, form responses and browser-bound authentication cannot be replayed in another application. Use **Do not capture downloads from this site**, a site exclusion, or temporarily switch monitoring off for those downloads. Ordinary unsigned Firefox installation is temporary until browser restart; permanent ordinary-Firefox distribution requires Mozilla signing. See [browser support](../extensions/README.md).

## Video and optional components

Install yt-dlp for supported video sites and ffmpeg for HLS/DASH streams or conversion through **Settings → Components & about**. These are optional programs invoked when needed, not services or tray applications. Site support depends on the current extractor and the site's access requirements; Stretto does not bypass DRM or subscription requirements.

## Exit, uninstall and support

Closing the main window may hide it in the notification area, depending on Settings. Use **Exit** to stop the app. Uninstall through Windows Installed apps. The EXE preserves app settings/tools; Windows normally removes the MSIX app-data folder. Your downloads outside app data remain.

When reporting a problem, include the Stretto version, browser/version, and what happened. Review diagnostics before sharing: local logs and download URLs can contain private information. See the [privacy notice](../PRIVACY.md).
