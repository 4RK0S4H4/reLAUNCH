#!/usr/bin/env python3
"""Check a built S4 Flat page for rule breaks.

Usage: python3 check_page.py page.html
Exit code 1 when there are errors. Warnings are printed but do not fail.

It checks only the page's own CSS and markup; the kit itself (between the
"S4 Flat v2" header and "end S4 Flat" marker) is trusted.
"""
import re
import sys

KIT_CSS = re.compile(r"/\* S4 Flat v[\d.]+ — Plate & Ink\..*?/\* end S4 Flat \*/", re.S)
KIT_JS = re.compile(r"/\* S4 Flat v[\d.]+ runtime.*?/\* end S4 Flat \*/", re.S)
COLOR = re.compile(r"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(|\boklch\(")
LAYOUT_ANIM = re.compile(r"transition[^;{}]*\b(width|height|top|left|right|bottom|margin|padding)\b")


def page_css(html):
    css = "\n".join(re.findall(r"<style[^>]*>(.*?)</style>", html, re.S))
    return KIT_CSS.sub("", css)


def check(html):
    errors, warns = [], []
    css = page_css(html)
    body = re.sub(r"<script[^>]*>.*?</script>", "", html, flags=re.S)
    body = re.sub(r"<style[^>]*>.*?</style>", "", body, flags=re.S)
    styles = " ".join(re.findall(r'style="([^"]*)"', body))

    # Structure
    if not re.search(r"<title>[^<]+</title>", html):
        errors.append("No <title>.")
    if 'class="f-wrap' not in html and "class='f-wrap" not in html:
        errors.append("No .f-wrap container (the page content must sit inside one so plates can recede it).")
    app_js = KIT_JS.sub("", html)
    if not re.search(r"\bS4Flat\b", app_js) or not re.search(r"\.init\(\s*\)", app_js):
        errors.append("S4Flat.init() is never called.")
    if not KIT_CSS.search(html):
        errors.append("The S4 Flat kit CSS is missing (build with build_page.py).")
    if not KIT_JS.search(html):
        errors.append("The S4 Flat kit JS is missing (build with build_page.py).")
    for href in re.findall(r'<link[^>]+rel="stylesheet"[^>]+href="([^"]+)"', html):
        if "fonts.googleapis.com" not in href and "remixicon" not in href:
            warns.append("External stylesheet %s (artifacts block everything except Google Fonts)." % href)

    # Flat rules
    for m in re.finditer(r"box-shadow\s*:\s*([^;}\"]+)", css + ";" + styles):
        val = m.group(1)
        for part in val.split(","):
            p = part.strip()
            if not p or p == "none" or "inset" in p:
                continue
            p2 = re.sub(r"(rgba?|hsla?|oklch|color-mix|var)\([^)]*\)+|#\w+", "", p)
            nums = [float(re.sub(r"[a-z%]+$", "", n)) for n in re.findall(r"-?\d*\.?\d+(?:px|rem|em)?", p2)]
            # A ring (0 0 0 Npx) is allowed; any offset or blur is a shadow.
            if any(abs(n) > 0 for n in nums[:3]) and len(nums) >= 2:
                errors.append("Shadow with offset or blur: box-shadow: %s" % val.strip())
                break
    if re.search(r"(?<!-)\b(linear|radial|conic)-gradient\(", css + styles):
        errors.append("Gradient used (S4 Flat is flat; the only allowed gradient is the kit's range track).")
    if re.search(r"backdrop-filter|filter\s*:\s*blur", css + styles):
        errors.append("Blur used (no glass in S4 Flat).")
    if re.search(r"border-radius\s*:\s*0(px)?\s*[;}\"]", css + ";" + styles):
        warns.append("A border-radius of 0 (no sharp corners; use --r-xs at least).")
    for m in LAYOUT_ANIM.finditer(css):
        warns.append("Layout property animated (%s); animate transform/opacity instead." % m.group(1))

    # Colours outside tokens
    app_css_no_vars = re.sub(r"--[\w-]+\s*:\s*[^;]+;", "", css)
    lits = COLOR.findall(app_css_no_vars)
    if lits:
        warns.append("%d literal colour(s) in page CSS; use tokens (var(--fg), var(--surface)...)." % len(lits))
    style_lits = [s for s in re.findall(r'style="([^"]*)"', body) if COLOR.search(re.sub(r"--[\w-]+\s*:\s*[^;]+;?", "", s))]
    if style_lits:
        warns.append("%d inline style(s) with literal colours." % len(style_lits))

    # Resets that can outweigh components
    for m in re.finditer(r"(^|[}\s,])((?:[.#][\w-]+\s+)?(?:button|input|select|a))\s*\{([^}]*)\}", css):
        if re.search(r"\b(background|padding)\s*:", m.group(3)) and ":where(" not in m.group(2):
            warns.append("Element reset '%s' sets background/padding; wrap it in :where() so components win." % m.group(2).strip())

    # Pressables
    buttons = re.findall(r"<button\b([^>]*)>", body)
    no_tap = [b for b in buttons if "f-tap" not in b]
    if no_tap:
        warns.append("%d <button> element(s) without f-tap (no press response)." % len(no_tap))

    # Icons
    used = set(re.findall(r'href="#(ri-[\w-]+)"', html))
    have = set(re.findall(r'<symbol id="(ri-[\w-]+)"', html))
    missing = sorted(used - have)
    if missing:
        errors.append("Icons used but not in the sprite: " + ", ".join(missing))

    return errors, warns


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    html = open(sys.argv[1], encoding="utf-8").read()
    errors, warns = check(html)
    for e in errors:
        print("ERROR  " + e)
    for w in warns:
        print("WARN   " + w)
    if not errors and not warns:
        print("OK     No S4 Flat rule breaks found.")
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
