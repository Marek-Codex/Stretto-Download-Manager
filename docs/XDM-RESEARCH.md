# Stretto vs XDM — research notes

Working notes on Xtreme Download Manager, gathered to inform design decisions. **Not legal
advice.** Every licence item needs the actual licence text read before release; this file
records what was found and where the risk sits.

Last checked: 29 September 2026.

**Relationship to `XDM-GAP-AUDIT.md`:** that audit compared the two *products* and
explicitly set browser extension distribution aside. This file covers that gap, plus the
licensing question it does not raise at all. Where they disagree, this file is newer.

## The one thing to act on: XDM is GPL-2.0

`subhra74/xdm` is licensed **GPL-2.0**. The browser extension ships inside the desktop app
and is part of that project. XDM's own [privacy policy][privacy] describes exactly what
that extension does — collects "URL, internet address, cookies, encrypted credentials,
query parameters, post data, namely all the data that the browser sends to the server when
requesting the file" — which is the same behaviour Stretto's extension implements.

Stretto's extension is a **reimplementation**. Design was derived by reading XDM's
extension and observing its behaviour. The practical position:

- Behaviour, ideas and interface choices are not copyrightable. Anyone reading the same
  Chromium APIs reaches the same conclusions about how to intercept a download and hand it
  to a desktop app.
- Expression is. The practical exposure here is small: the only place a line of XDM's
  logic was reproduced rather than independently written is the redirected-download referrer
  fallback, a one-line boolean expression that is not copyrightable in isolation.
- The risk is not in any single line, it is in the *record*. If Stretto ever ships closed or
  under a permissive licence, nobody should have to guess how the extension came to be.

**Recommendation:** write `LICENSING.md` for Stretto following `Interlude/LICENSING.md`, with
a dependency table and an explicit note that the extension was written clean against
observed browser and app behaviour rather than derived from XDM source. Keep that note
truthful and short. Do not restate it as a warranty — it is a description of method, and
it is worth nothing if it is not true.

**Unverified:** whether the extension source in the XDM repository is byte-identical to the
copy that ships in the app. The path tried (`app/src/main/resources/static`) 404s and was
not chased further. The copy at `%USERPROFILE%\.xdm-app-data\chrome-extension` is definitive
for present purposes, because that is what was read.

## Browser integration — the gap the audit set aside

### XDM 8 abandoned extension-based video detection, and the reason matters

From [discussion #768][d768], the author's own account:

> From version 8.0.6, browser extension will only be used to intercept file downloads.
> Video download option is added in the XDM application itself.
>
> I am forced to make this change because of repeated warning and threats from Chrome web
> store and Firefox extension store to disable/delete the extension. Since detecting
> streaming video requires monitoring users network traffic, the reviewers often thinks that
> XDM extension is doing some malicious activity. In the past the extension was taken down
> several times for this reason.

> I also received warning from Chrome web store that the extension is being used to download
> copyright protected contents, so from now on XDM will delegate all its video downloading
> tasks to another open source tool yt-dlp. XDM will not have any video downloading code
> itself and will take video download links from yt-dlp as general URL and will leave the
> decision of determining whether content is legal/copyright protected up to the user.

Three consequences, and the first is the important one:

1. **Watching `webRequest` for media is a store-removal risk, not just a permissions
   question.** Stretto's extension does exactly this and posts to `/media`. That is
   survivable *only* because Stretto is unpacked and installed by its own wizard, which
   means it never faces store review. **This is now a load-bearing assumption of the
   design, and it is undocumented.** If Stretto ever wants Chrome Web Store or AMO
   distribution, this is the first thing that gets rejected, and the redesign is not small.
   Record this constraint somewhere it will be read before that decision is made.
2. **XDM's current design already matches Stretto's** — video extraction delegated to
   yt-dlp, app decides nothing about legality. Stretto is not behind here. The audit's
   "near parity" note on video downloads is if anything generous to XDM.
3. Stretto's `MediaExtractor` is a *page* scraper, which is a different and legally
   cleaner mechanism than sniffing the network. Worth being deliberate about that
   distinction in positioning.

### Native messaging vs loopback HTTP

Same discussion, XDM 8's stated goals: *"Native messaging technology to have smooth
integration with browsers."*

Stretto does **not** do this — it uses a loopback HTTP listener with a pairing token
(`Stretto.Core/HandoffListener.cs`). This is a deliberate-looking divergence and Stretto's
side is defensible, so the reasoning should be written down before someone "fixes" it:

| | Native messaging | Stretto's loopback HTTP |
|---|---|---|
| Install | requires a host manifest at a fixed OS path | none — the wizard points at the extension folder |
| User grants | Chrome shows a native-messaging consent dialog | Stretto's own one-click Allow prompt |
| Multi-browser | one host manifest per browser | one port, browser-agnostic |
| Auth surface | OS handles the pipe | token + origin check, both implemented |

Native messaging is cleaner in principle but demands per-browser OS registration, which
breaks "drop the folder in and it works". For an unpacked, wizard-installed product on one
platform, loopback is the better trade. XDM can afford it because it ships installers for
three platforms; Stretto ships one.

### Feature-by-feature, browser side

| XDM behaviour | Stretto status |
|---|---|
| `downloads.onDeterminingFilename` takeover | Same approach, with a give-back path XDM lacks |
| Full request/response headers replayed | **Better.** XDM sends them; Stretto now captures and strips what must not be replayed |
| `mediaTypes` / content-type matching | Parity — configurable in both, via `/rules` |
| "always capture this host" | Parity — `alwaysHosts` |
| URL-regex pattern config | Not implemented, deliberately. `mediaTypes` + `alwaysHosts` cover the cases |
| Tab URL/title reporting to the app | Neither has a clean version. Stretto resolves names locally instead |
| Context menus: link, image, all links, all images | Parity |
| Popup: capture switch, media list, clear | Parity. Stretto adds connection state, per-site toggle, notifications |
| Popup shows quality/resolution per row | **XDM ahead.** App-supplied display text; Stretto shows kind and size, and the grabber window has the quality picker |
| "Download video" button overlaid on the player | **XDM ahead** in 7.x, deliberately dropped in 8.x for the store reasons above. Not worth chasing |
| One-click launch the app from the popup | Parity now (`stretto:` scheme) |
| Capture POST downloads | Neither can re-issue a POST. Stretto's Chromium path releases unwanted downloads untouched, but one offered to the app and declined is re-issued by address, losing the body; Firefox declines POSTs outright. XDM claims POST data in its privacy policy but cannot re-issue it either |
| Extension store distribution | Stretto: unpacked only. XDM: listed, and repeatedly removed for it |

## XDM 8 — the current reality, correcting a stale number

The widely-copied figure "latest release 7.2.11" is wrong for the current line. The
releases page serves **8.0.29**. More importantly, [discussion #768][d768] is the real
story: **XDM is being rewritten from Java to C#/.NET**, because Java was causing HiDPI
problems, weak non-English text handling, ~40 MB idle RAM, and a ~25 MB bundled JRE.

XDM 8 targets: WPF on Windows (**moving to WinUI 3**), GTK3 on Linux, Xamarin on Mac;
installer 5–7 MB; RAM 15–20 MB.

**For Stretto this is a genuine data point, not just trivia.** Stretto is WinUI 3 on .NET 10
with a native Windows shell. It is on the framework XDM is *migrating toward*, and it does
not have the JRE, HiDPI or footprint problems XDM spent years describing. On UI technology
and packaging weight, Stretto is currently ahead of the thing it is compared against. That
is worth knowing before writing another feature list.

## Competitive reality

Numbers as of 29 September 2026:

| | XDM | Stretto |
|---|---|---|
| Licence | GPL-2.0, open source | not yet stated — see above |
| Repo | ~8.0k stars, 1.2k forks, ~497 commits | private |
| Open issues | ~820 | — |
| Stable line | 8.0.29 | 1.2.0 |
| Platforms | Windows, Linux, macOS (in progress) | Windows only, by decision |
| Firefox add-on rating | **3.6 / 5 from 24 reviews** | n/a, unpacked |
| Native language | Java (7.x), C#/.NET (8.x) | C#/.NET |

The 3.6 is the interesting number. XDM's extension is its most visible surface, it is
rat mediocre on the one store that publishes ratings, and it has been removed from stores
several times. Stretto ships no store listing at all, so there is nothing to rate — but
that also means **Stretto's extension quality is invisible to anyone who has not installed
it**. If the extension is the differentiator, that is a marketing problem worth solving,
and the honest fix is a good store listing plus a real privacy policy, not more features.

## Torrents: settled, and settled by XDM

Not a parity gap. The author was asked repeatedly and declined in 2018:

> Personally I would not like to have torrent support in XDM. I don't want another
> systemd. Torrent managers (for eg. qBT) does great job at managing, searching torrents.
> Adding torrent would make more code and more debuggings.

There is also a separate Android "XDM" with torrent support. That is a different product and
must not be confused with the desktop one when comparing.

## Still thin, and worth chasing

- XDM's wiki pages on queues/schedulers and streaming video are the most detailed public
  description of the intended behaviour. Not read for this pass.
- The docs directory in the repository was not read.
- Whether XDM's `requestFileExts` (its separate list for request-level file matching) has
  any behaviour the unified `captureTypes` + `videoTypes` model misses. Probably not.
- Firefox add-on permissions and whether XDM's Firefox build uses blocking `webRequest`
  rather than the `downloads` handoff. Its Firefox version is 3.6 versus 3.5 for the
  Chromium copy in hand, so they differ; the difference is unexamined.

## Sources

- [XDM repository](https://github.com/subhra74/xdm) — GPL-2.0, feature list, build
- [Current status and roadmap, discussion #768](https://github.com/subhra74/xdm/discussions/768) — the .NET rewrite, the extension change, native messaging
- [Releases](https://github.com/subhra74/xdm/releases) — 8.0.29
- [Experimental binaries](https://github.com/subhra74/xdm-experimental-binaries/releases) — 8.0.25-beta
- [XDM privacy policy](https://xtremedownloadmanager.com/privacy.html) — what the extension collects
- [Torrent support thread](https://sourceforge.net/p/xdman/discussion/xdmhelp/thread/871c0cd5) — the author's refusal
- [XDM Integration Module on AMO](https://addons.mozilla.org/en-US/firefox/addon/xdm-integration-module) — 3.6 from 24 reviews
- Local copy of the shipped extension: `%USERPROFILE%\.xdm-app-data\chrome-extension`

**Limits:** XDM's marketing pages span releases and several are older than current 8.x
code; repository and discussion material was favoured where they conflict. Version numbers
in particular move faster than this file. The .NET rewrite's relationship to the `master`
branch was not established — the README still describes a Maven build while third-party
indexers report the repository as C#.
