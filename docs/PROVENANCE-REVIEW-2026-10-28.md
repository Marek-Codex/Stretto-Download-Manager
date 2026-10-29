# Source provenance review — October 28, 2026

This is an engineering evidence record, not a legal clearance or a new attribution
notice. It preserves the difference between a research note, available source
evidence, and a verified copying finding.

## What triggered the review

An older research note says the browser extension was developed after reading and
observing Xtreme Download Manager, and identifies a redirected-download referrer
fallback as the only reproduced expression. The note does not include the original
extension version, file hash or a side-by-side excerpt. It also recommends claiming
independent/clean development while saying source was consulted. That recommended
claim must not be adopted without evidence.

Removing competitor names from product copy did not resolve this question. The
earlier publication-readiness statements were broader than the evidence justified.

## Evidence checked

- Stretto's extension already exists in the earliest local commit, `6f2696e`.
  That commit contains both the fallback and the historical note. There is no
  earlier local development sequence establishing how that code was authored.
- The current fallback remains in `extensions/chromium/capture-chromium.js`: it
  prefers the browser's referrer and otherwise uses the original download URL
  when a distinct final URL is present. This is an observed expression in Stretto,
  not independent proof of which source supplied it.
- The installed reference extension described in the note was not found at the
  recorded user-profile path or the corresponding migrated-profile location.
- Retrieved the upstream tree at commit
  `e5a9ecda113cacce86daf038b1160d6dd4013231` and 13 Chromium extension JavaScript
  files (3,147 lines). The current repository metadata and root LICENSE identify
  **GPL-3.0**, whereas the older note says GPL-2.0. This does not establish the
  license of the unavailable historical reference version.
- Compared those 13 upstream files against the seven Stretto extension JavaScript
  files in both their current and earliest-commit forms. An exact comparison of
  six consecutive nontrivial code lines, normalizing whitespace and retaining
  identifiers, found no matching blocks.
- Inspected the relevant upstream download, request-watching and connection paths.
  The exact referrer fallback alleged by the old note was not found in that current
  upstream source snapshot.

Upstream snapshot: [source tree](https://github.com/subhra74/xdm/tree/e5a9ecda113cacce86daf038b1160d6dd4013231/browser-extension/chrome-extension),
[license](https://github.com/subhra74/xdm/blob/e5a9ecda113cacce86daf038b1160d6dd4013231/LICENSE).

## What these results mean

### Author's recollection

Marek reports that, to the best of his knowledge, he supplied a screenshot of the
installed extension popup as a visual reference, after which the implementation
was AI-assisted and developed substantially further. He does not recall providing
source code. This is the author's recollection, not a recovered transcript or
proof of every input available during the original implementation.

That account does not corroborate the older note's claim that upstream source was
consulted or a particular expression copied. The older note alone should not be
treated as a verified account of how development happened.

No substantial verbatim block was confirmed by this scoped comparison. It does
**not** detect all renamed, rearranged or adapted code, cover every older upstream
version, establish independent authorship, or settle copyright/license obligations.
The historical note's claim remains uncorroborated with the original reference
unavailable. It should not be restated as either proven copying or proven clearance.
No specific reused upstream source has been confirmed in this review. There is
therefore no established basis here for declaring the entire app GPL-derived;
that would be a broader conclusion than the evidence supports.

The U.S. Copyright Office distinguishes protectable code expression from ideas,
program logic, algorithms and methods. Shared behavior alone therefore does not
establish that a GPL program's source was copied. Applying that distinction to a
specific development history requires the actual evidence, not a product-name scan.
See [Copyright Office computer-program guidance](https://www.copyright.gov/register/tx-programs.html).

## Next steps

1. Retain the author's recollection above. If the original conversation or exact
   reference version becomes available, compare it with this record. Do not invent
   a clean-room story or treat an unsupported historical note as a proven admission.
2. If identifiable source was reused, review the applicable license and either
   comply with its terms or replace the affected implementation through a documented
   independent process. Renaming variables or deleting credit is not that process.
3. If no copied source can be identified, retain this scoped result and its limits.
   A definitive legal clearance cannot be produced by a code-similarity scan.
4. Finalize Stretto's intended distribution terms. The current LICENSE explicitly
   remains a placeholder with no general permission grant; offering free binaries
   and reserving source rights should be expressed deliberately rather than inferred.
5. Complete third-party notice/redistribution review for the actual installer payload.

## Separate dependency facts

The restored `Microsoft.WindowsAppSDK.WinUI` 1.8.260415005 NuGet package carries a
Microsoft Windows App SDK license file, including self-contained redistribution
terms. It must not be casually labeled MIT just because some upstream components
are open source. `System.Security.Cryptography.ProtectedData` 10.0.12 declares MIT
in its restored NuGet metadata. Self-contained .NET/runtime components also need
their applicable notices accounted for in the final distribution.

Optional ffmpeg and yt-dlp executables are obtained separately rather than bundled
in Stretto's installers. Their binary distributions and dependencies have their own
terms. That is a separate review from the alleged extension-source reuse.

No runtime code, source license, copyright notice or historical evidence was removed
or rewritten as a result of this assessment.
