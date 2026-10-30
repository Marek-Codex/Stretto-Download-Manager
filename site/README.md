# Stretto project website

Static, dependency-free project pages. No analytics, third-party scripts or
automatic network requests are included. GitHub links point to the planned
repository; the page explicitly says publication is pending.

Run `python tools/build-site.py` from the repository root when PRIVACY.md,
docs/LICENSING.md, LICENSE or NOTICE changes. It regenerates the corresponding
site pages and copied notices. Review the generated pages before publication.

For GitHub Pages, deploy this directory as the Pages artifact using an approved
workflow, or publish its contents to a separate Pages branch. Pages cannot use
an arbitrary `/site` folder as a branch source. Hosting is not enabled merely
by committing these files. Confirm the public URL before adding it to the app
or Store listing, and update the pending-publication copy when downloads exist.
