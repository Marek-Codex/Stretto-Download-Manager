# Stretto UI identity audit

Written 29 September 2026, before the redesign that followed it. The question: which parts of
Stretto's UI are just what a download manager is, which are just WinUI, and which read as
"Xtreme Download Manager in WinUI 3"?

The code comments were candid about it. `DownloadProgressWindow`, `DownloadCompleteDialog`
and `PropertiesDialog` each described themselves as "as XDM shows one", and the progress
window's status row said "the way XDM lays it out".

## 1. Generic download-manager conventions (kept)

These come from solving the same problem, and nobody reads them as XDM:

- A list of downloads with name, progress, speed, size and state.
- Pause, resume, remove, restart, open, and show in folder.
- A right-click menu with Copy link, Save as, Refresh link, Move to queue, Convert, and Properties.
- An add dialog with a link, a file name and a destination folder.
- Queues that run one after another, with a start and stop timetable.
- Sorting by file type into category folders.
- Browser capture, clipboard watching, a tray icon, and keep-awake.
- A search box.
- Per-download options: login, proxy and connections.

## 2. Plain WinUI 3 / Fluent conventions (kept)

- Mica backdrop, `TitleBar` control, accent-coloured primary buttons.
- `InfoBar` for notices, `MenuFlyout` for context menus, `ToggleSwitch`, `SelectorBar`.
- Settings pages built as cards of name/explanation rows with the control on the right,
  the Windows Settings pattern.
- Separate secondary windows sharing one shell (`UtilityWindow`) with a footer button row.

## 3. Specifically XDM-derived

| Element | Why it reads as XDM |
| --- | --- |
| Sidebar: two state buckets (Active / Complete), then a **FILE TYPES** list of Documents, Compressed, Music, Video, Programs, Other as sub-views of finished files | XDM's left panel, near enough verbatim. This was the single biggest tell. |
| A hamburger button in the top-right with almost every global command in it: Settings, Resume all, Pause all, Queues and scheduler, Browser, Media grabber, Open folder, Remove finished, Import, Export, log, About, Exit | XDM's main menu, in almost the same order. |
| A bottom status strip led by a **Browser monitoring** toggle | XDM's signature footer. |
| A fixed toolbar of Remove, Pause and Resume (swapping to Open and Open folder), most of it disabled most of the time | XDM's toolbar row. |
| One flat list whose columns are name/progress, then status, then size/date | XDM's columns. |
| Add dialog: *Address / File / Save in* labels down the left, then **Download Now**, **Download Later ▸ (Default queue / Do not use queue / Queue and scheduler)**, **Cancel**, plus **More/Advanced** | XDM's New Download window, down to the button wording and the Later menu. |
| A progress window opening for every download by default, with *Hide*, a *Time left* line and a speed-limit label | XDM's per-download window. |
| A **Download complete** window with Open / Open folder / Don't show again for every finished file | XDM's completion popup. |
| **Queues and scheduler** as a modal window: queue list on the left, Files and Schedule tabs on the right, Start/Stop | XDM's queue manager, down to the name. |
| Properties as a separate window | XDM. |
| Settings split along XDM's lines: General, Connections, Proxy, Browser, Capture rules, Passwords, System, Advanced | XDM's settings sections, with Proxy and Capture rules split out. |

## 4. What to redesign, and why

Each change has to earn its place on usability, not just be different.

1. **Navigation, organised by what you are doing rather than by status plus file type.**
   *Transfers* (everything not finished), *Library* (finished files) and *Queues* sit side by
   side as peers in a native `NavigationView`. File types stop being a sidebar tree and
   become filter chips inside Library. *Why:* in XDM's model a file type was only ever a
   sub-view of finished files, so it never belonged at the top level. Chips are one click,
   show counts, and leave the pane short.
2. **Transfers grouped by stage.** The list is split into *Downloading*, *Needs attention*,
   *Up next* and *Paused*, each headed with its count and, for Downloading, its combined
   speed. *Why:* a failed download used to sit unnoticed in the middle of a flat list. Now
   it is at the top under a heading that says why it is there.
3. **Library grouped by when things finished** (Today, Yesterday, This week and so on),
   with sorting by name, size or type. *Why:* "the thing I downloaded this morning" is the
   commonest lookup, and a date bucket answers it faster than a date column.
4. **Rows carry one status line and their own action.** Name, a progress bar only while it
   means something, and a line like "54 MB of 120 MB · 3.2 MB/s · 21 s left". Each row has
   one state-appropriate button: pause, resume, retry, or show in folder. *Why:* the
   commonest action is now one click rather than select-then-toolbar, and the time left
   appears in the list rather than only in a popup window.
5. **Commands follow the selection.** When nothing is selected, the command bar shows
   commands for the whole view (Pause all and Resume all, or Open folder and Clear list).
   Select rows and it switches to "3 selected" with the actions that apply to them. *Why:*
   no permanently disabled buttons, and bulk actions become obvious. This replaces both the
   fixed toolbar and most of the hamburger menu.
6. **An inline details pane in place of a Properties window.** It toggles from the
   command bar or from Properties in the right-click menu. *Why:* inspecting several
   downloads no longer means opening and closing a window for each.
7. **Status where it belongs.** Browser state moves to a *Browser* entry in the navigation
   footer, with a status dot and a flyout holding the monitoring switch, setup and
   settings. Live speed moves into the Transfers heading and the running count onto the
   Transfers entry as a badge. The bottom strip goes. *Why:* the switch is rarely touched
   but the state is worth a glance, and a whole strip for it was XDM's layout.
8. **Add flow.** Labels go above the fields, as in Fluent forms. The buttons become
   **Download**, **Add to queue ▾** (*Main queue*, *Add paused*, *Add and open Queues*) and
   **Cancel**, and *Advanced* becomes *Options*. **Add download** is a split button: one
   click opens the dialog, and the arrow holds Batch, Video and Media grabber. **Ctrl+N**,
   and **Ctrl+V** on the list, open it too. *Why:* it removes a click from the commonest
   action, and the wording says what happens rather than when.
9. **Queues become a page, not a modal window.** The same editor sits in the main window.
   *Why:* queues are a place you manage, and a modal hid the list whose state they control.
10. **The progress window is opt-in, and quieter.** It defaults to off for new installs,
    since the list now shows time left. It is restyled around a large percentage with a
    small stats grid, *Hide* becomes *Close*, and the speed-limit label only appears when a
    limit is set.
11. **Completion window kept.** An in-app notice was tried in its place and reverted at
    the owner's request: the window's Open / Show in folder / Close is wanted every time.
12. **Settings reorganised around intent.** The eight sections become six: *Downloads*,
    *Network* (Connections and Proxy), *Browser* (Browser and Capture rules), *Sign-ins*,
    *Windows* and *Components & about*.
13. **Empty states that lead somewhere.** Each view says what it is for and offers the next
    step: add a download, connect a browser, go to Library, show all, or clear the search.

## Not changed

- The download engine, capture, queue logic, per-download options and the right-click menu's contents.
- The accent, Mica, the title bar, and the icon set's single source file (`Assets/Icons.xaml`).
- Every setting. None were removed. One default changed for new installs only: the
  progress window.

## Note on icons

Stretto uses **Segoe Fluent Icons**, not Phosphor, despite what the brief assumed.
`Assets/Icons.xaml` says so, and no Phosphor font ships with the app. Every glyph still
resolves through that one file, so moving to Phosphor stays a one-file change. It was not
done here, because it would mean adding and licensing a font file.
