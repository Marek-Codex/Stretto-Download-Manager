# Stretto project website

Static project pages, published from the `gh-pages` branch at
https://marek-codex.github.io/Stretto-Download-Manager/. No analytics, third-party
scripts or remote embeds are added. The stylesheet is served alongside the HTML.

Run `python tools/build-site.py` from the repository root when PRIVACY.md,
docs/LICENSING.md, LICENSE or NOTICE changes. It regenerates the corresponding
site pages and copied notices. Review the generated pages before publication.

After reviewing changes, publish with `git subtree push --prefix site origin gh-pages`.
Pages is configured for the root of that branch. Committing to main alone does
not update the published site. Check the Pages build and public pages afterward.
