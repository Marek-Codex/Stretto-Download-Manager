# Stretto vs XDM: feature parity and competitive gap audit

**Checked:** 26 September 2026  
**Scope:** Windows desktop only. Cross-platform support is explicitly out of scope. Stretto source and checked-in docs are compared with XDM's current official repository, release notes, wiki, and download page. This is a product gap audit, not a hands-on certification of every browser/site/server combination.

## Executive assessment

**On Windows, Stretto already matches or exceeds most of XDM's everyday download-manager feature set.** It has the core accelerator, browser handoff, clipboard intake, queues/scheduler, media grabber, conversion, FTP, proxy/authentication, retry/resume, categorization, import/export, antivirus hook, tray/startup, and batch tools. Several parity features were easy to miss in Stretto's README overview.

For this comparison, **extension distribution is set aside**: Stretto has a wizard-based install path, and the comparison is about the Windows desktop product. The meaningful remaining gaps are advanced proxy compatibility and the depth/turnkey feel of media conversion and extraction. There is no obvious missing everyday downloader feature.

I would first make the existing feature set installable, supported, and demonstrably reliable; then address the smaller set of meaningful product gaps below. Torrents, a giant converter preset catalog, or more protocol checkboxes are not the priority.

## Feature comparison

| Area | XDM capability from official sources | Stretto status from repository | Assessment |
|---|---|---|---|
| HTTP acceleration, pause/resume, recovery | Parallel segmented downloads; resume interrupted/broken downloads; HTTP/HTTPS | Segmented ranged downloads, resume sidecar, per-segment retry, overall retry, range fallback | **Parity.** Stretto has strong reliability design. Benchmark actual supported hosts; neither app controls server-side throttling. |
| Browser takeover | Integration modules for common Chromium/Firefox browsers; capture rules for file types | Wizard-based install path; Chromium handoff and Firefox implementation | **Set aside for this audit.** Compare actual capture behavior separately; store installation is not treated as a product-feature gap here. |
| Browser media detection | Media grabber reports media detected while browsing; video site support | Extension popup, media feed/grabber, direct files and HLS/DASH sightings, links/images batch capture | **Parity in shape.** Broad browser/site coverage remains unproven. Firefox cannot capture POST downloads according to its source comments; Chromium can. |
| Video/site downloads | XDM delegates site extraction to yt-dlp in newer versions; HLS, MPEG-DASH, and Adobe HDS listed in project claims | Page scraper, ffmpeg for HLS/DASH, optional yt-dlp with install/update paths, format selection, MP3/MP4/remux conversion | **Near parity.** HDS is not implemented/documented; site extraction depends on external tools being available, though Stretto helps install them. XDM also relies on yt-dlp, so the dependency itself is not unique. |
| Format conversion | Built-in video converter advertises MP3/MP4 and device-oriented presets (older marketing says 100+ devices) | ffmpeg-powered MP3, MP4 re-encode, and MP4 remux | **Basic output parity; fewer presets and less turnkey setup.** The large device count is mostly preset/encoding convenience. |
| Queues and scheduler | Named queues, ordering, scheduled start/stop, queue-driven downloads | Named queues, ordering, timetable and engine release; default queue/concurrency and scheduled releases | **Parity.** Verify real-world time-zone and shutdown interactions as a release-quality check. |
| Batch and link collection | URL-pattern batches, batch/link grabbing; XDM source UI includes batch download and selection | Pattern expansion plus pasted links; browser “links on page” and “images on page” collected into one review list | **Parity for common cases.** XDM's mature host plugins/deep crawl may expose more site-specific links; Stretto should not imply arbitrary recursive website crawling. |
| Clipboard capture | Clipboard monitoring; new-download/manual URL flow | Clipboard monitoring setting and new-download prefill | **Parity.** |
| Download organization | File-type categories, automatic file sorting, configurable save locations | Categories with per-category folders, auto-sort, Active/Complete views, search, completion retention | **Parity / Stretto adds useful library controls.** |
| FTP | HTTP, HTTPS, FTP listed | Separate FTP downloader path | **Protocol-label parity.** Test authentication, passive mode and large files before describing broad FTP parity. |
| Authentication | Cookies, redirects, proxy auth and several HTTP auth modes listed (Basic, Digest, NTLM, Kerberos, Negotiate) | Browser cookies/referrer/user-agent for handoff/media; saved per-site HTTP credentials with Basic/Digest challenge support; Windows credentials for NTLM/Kerberos; proxy credentials | **Mostly parity.** Stretto now answers Basic/Digest challenges with origin-scoped credentials. It intentionally does not persist browser cookies, so manual downloads may need cookies supplied again. |
| Proxy support | Proxy types, auto proxy configuration/PAC, ISA and several auth schemes | Windows system proxy, direct, manual HTTP or SOCKS5 proxy, per-download override; proxy credentials; optional Windows credentials | **Remaining gap:** no explicit PAC editor/override or SOCKS4 support. Verify Windows system proxy/PAC and authenticated proxy cases. |
| Antivirus scan after download | XDM has automatic antivirus checking | Configurable external scanner path/arguments; can scan finished files | **Capability parity.** Requires user configuration. |
| Power actions | Shutdown after scheduled queue completes | Keep-awake, finish actions, shutdown with cancellable Windows countdown, start with Windows/start minimized | **Parity, with thoughtful safety behavior.** |
| Operating systems | XDM offers packages beyond Windows | WinUI 3 Windows app only | **Out of scope by decision.** Stretto is targeting Windows; revisit only if user demand justifies it. |
| Backup / migration | Import/export download list in XDM UI | Import/export download list, persistence | **Parity on list migration.** XDM-list import could be a migration nicety if formats permit, but is not required for feature parity. |
| Updates / support / language | XDM has release/update workflow and translation support | Local log path and troubleshooting affordance exist; public support/update channel and localization were not evident in this source pass | **Potential release gap:** define update delivery, rollback, signing, help, and language strategy. This affects trust more than another feature. |
| Torrents | Not part of the XDM feature list reviewed | Explicitly out of scope | **No parity gap.** Don't add it just to grow a checklist; it changes product category and maintenance burden. |

## Real gaps, ranked

### P0 — close user-visible product gaps

1. **Advanced proxy support.** Manual SOCKS5 is implemented. Verify SOCKS5 authentication and Windows system proxy/PAC behavior; add a PAC editor or SOCKS4 only if Windows users need it.
2. **Polish the video workflow.** Ensure the user can get from page URL to selected format/output without needing to understand ffmpeg/yt-dlp setup; clarify what is supported and report extraction failures usefully. XDM 8 also delegates to yt-dlp, so the important difference is integration polish rather than architecture.
3. **Close narrow format gaps only on demand.** Adobe HDS is not implemented; it is a low-priority legacy format unless users request it.

### P1 — close meaningful capability gaps

4. **Localization**, if the intended market extends beyond English-speaking Windows users. XDM's repository includes translations; Stretto is currently English-only.
5. **Update and support story:** update checks or Store updates, diagnostics/log export, and a visible support path. XDM source language includes update check, support links, and problem reporting; Stretto's public support/update paths are not apparent in this repo.

### P2 — only if users ask

7. **More converter presets** beyond MP3/MP4, with codec/container/profile choices. Add them only if users ask; current remux and common formats cover the meaningful base.
8. **Shell and browser affordances:** drag/drop URL/file intake, richer context menus, shell integration and URL clipboard convenience. Stretto already has browser context menus and clipboard monitoring, but compare the full affordance set hands-on.
9. **Cross-platform ports** after Windows installation and browser capture are solid. Linux/macOS require real platform work in the shell, tray, system policy, and installer.

## Documentation corrections

The top-level README is behind the checked-in implementation:

- It says the Chromium extension is “not yet loaded in a browser,” but `extensions/chromium/README.md` says Brave is tested.
- It says Firefox is “not built” in `extensions/README.md`, but Firefox manifest/capture code exists and the project assembles a Firefox output and `.xpi`.
- It says per-category folders are “not done” near the top, then later says they are done and Settings exposes a folder per category.
- It omits clipboard monitoring, antivirus scanning, import/export, download search, Windows-account credentials, media conversion, startup/tray, and other completed workflows.

This matters competitively: users and reviewers compare published claims, not hidden code. Reconcile docs and label capabilities honestly as **implemented**, **tested**, **signed/shipped**, or **planned**.

## Direct answer

If Stretto is positioned as **“a modern Windows alternative to XDM,”** the feature foundation is credible. With extension installation and cross-platform support set aside, the meaningful gaps are advanced proxy compatibility, a more turnkey media workflow, and public update/support polish. The README still undersells the product.

If you mean **“a direct XDM replacement on every platform,”** then Windows-only is a major gap. If you mean **“the download manager Windows users wish XDM felt like,”** focus first on signed installs, extension reliability, clear troubleshooting, and polish. That is more likely to win users than torrents or 100 conversion presets.

## Sources

- [XDM main repository and feature list](https://github.com/subhra74/xdm)
- [XDM releases](https://github.com/subhra74/xdm/releases)
- [XDM current download page](https://xtremedownloadmanager.com/)
- [XDM queue/scheduler guide](https://github.com/subhra74/xdm/wiki/Using-download-queues-and-schedulers)
- [XDM streaming-video guide](https://github.com/subhra74/xdm/wiki/How-to-download-streaming-video)
- [XDM 8 migration notes about yt-dlp and extension changes](https://github.com/subhra74/xdm/discussions/768)
- [Stretto README](../README.md)
- [Stretto extension README](../extensions/README.md)
- [Stretto Chromium extension README](../extensions/chromium/README.md)
- Source reviewed: Stretto app settings, menus, handoff, download service and conversion code; core FTP/media/download engine; both extension manifests and capture scripts.

**Limits:** XDM marketing language spans releases and some pages are older than current 8.x code. Current repository/release documentation was favored where claims conflict; platform availability and version-specific details should be verified before publishing a comparison chart.
