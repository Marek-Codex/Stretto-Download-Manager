"""Generate static policy pages from the same notices shipped with Stretto."""
from html import escape
from pathlib import Path
import re
import shutil

root = Path(__file__).resolve().parent.parent
site = root / "site"


def inline(text):
    text = escape(text)
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)

    def link(match):
        label, target = match.groups()
        target = {"../LICENSE": "license.txt", "../NOTICE": "notice.txt"}.get(target, target)
        if target == "PROVENANCE-REVIEW-2026-10-28.md":
            target = "https://github.com/Marek-Codex/Stretto-Download-Manager/blob/main/docs/" + target
        return f'<a href="{target}">{label}</a>'

    return re.sub(r"\[([^\]]+)\]\(([^)]+)\)", link, text)


def render(source):
    blocks, paragraph, listing = [], [], False

    def flush():
        if paragraph:
            blocks.append("<p>" + inline(" ".join(paragraph)) + "</p>")
            paragraph.clear()

    for line in source.splitlines():
        if line.startswith("# "):
            continue
        if not line.startswith("- ") and listing:
            blocks.append("</ul>")
            listing = False
        if not line.strip():
            flush()
        elif line.startswith("## "):
            flush()
            blocks.append("<h2>" + inline(line[3:]) + "</h2>")
        elif line.startswith("- "):
            flush()
            if not listing:
                blocks.append("<ul>")
                listing = True
            blocks.append("<li>" + inline(line[2:]) + "</li>")
        else:
            paragraph.append(line.strip())
    flush()
    if listing:
        blocks.append("</ul>")
    return "\n".join(blocks)


for name, title, source in (
    ("privacy", "Privacy notice", root / "PRIVACY.md"),
    ("licensing", "Licensing", root / "docs" / "LICENSING.md"),
):
    content = render(source.read_text(encoding="utf-8-sig"))
    html = f'''<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Stretto · {title}</title><link rel="stylesheet" href="style.css"></head>
<body><header><a class="brand" href="index.html">PLEXUS APPS / STRETTO</a>
<nav aria-label="Main navigation"><a href="index.html">Home</a><a href="support.html">Support</a></nav></header>
<main class="document"><h1>{title}</h1>{content}</main>
<footer>© 2026 Marek Codex</footer></body></html>'''
    (site / f"{name}.html").write_text(html, encoding="utf-8")

shutil.copyfile(root / "LICENSE", site / "license.txt")
shutil.copyfile(root / "NOTICE", site / "notice.txt")
print("Generated privacy/licensing pages and license notices in site/.")
