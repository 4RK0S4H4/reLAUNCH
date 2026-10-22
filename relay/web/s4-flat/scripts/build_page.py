#!/usr/bin/env python3
"""Build an S4 Flat page: inline the kit and only the icons the page uses, then check it.

Usage:
  python3 build_page.py page.src.html out.html [--remix DIR] [--no-check]

In the source page put these three placeholders:
  /* S4FLAT:CSS */      inside your <style>, before your own CSS
  <!-- S4FLAT:ICONS --> right after the opening of the page content (anywhere in <body> works)
  <!-- S4FLAT:JS -->    before your own <script>

Icons are referenced as <svg class="f-i"><use href="#ri-NAME"/></svg>. Every #ri-NAME the page
uses is pulled from assets/remix-sprite.svg. For icons outside the bundled set, pass --remix with
the folder of an unpacked Remix Icon package (npm pack remixicon@4.5.0 && tar xzf remixicon-4.5.0.tgz
gives package/icons), or the build lists what is missing.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "assets")


def read(p):
    with open(p, encoding="utf-8") as f:
        return f.read()


def remix_index(folder):
    idx = {}
    if folder and os.path.isdir(folder):
        for d, _, fs in os.walk(folder):
            for f in fs:
                if f.endswith(".svg"):
                    idx[f[:-4]] = os.path.join(d, f)
    return idx


def build(src, remix=None):
    css = read(os.path.join(ASSETS, "s4-flat.css"))
    js = read(os.path.join(ASSETS, "s4-flat.js"))
    bundled = dict(re.findall(r'<symbol id="ri-([\w-]+)" viewBox="0 0 24 24">(.*?)</symbol>', read(os.path.join(ASSETS, "remix-sprite.svg")), re.S))
    extra = remix_index(remix)

    used = sorted(set(re.findall(r'#ri-([\w-]+)', src)))
    symbols, missing = [], []
    for name in used:
        if name in bundled:
            inner = bundled[name]
        elif name in extra:
            inner = re.search(r"<svg[^>]*>(.*)</svg>", read(extra[name]), re.S).group(1).strip()
        else:
            missing.append(name)
            continue
        symbols.append('  <symbol id="ri-%s" viewBox="0 0 24 24">%s</symbol>' % (name, inner))
    sprite = '<svg width="0" height="0" style="position:absolute" aria-hidden="true">\n' + "\n".join(symbols) + "\n</svg>"

    for ph in ("/* S4FLAT:CSS */", "<!-- S4FLAT:JS -->", "<!-- S4FLAT:ICONS -->"):
        if ph not in src:
            sys.stderr.write("Placeholder %s not found in the source page.\n" % ph)
    out = src.replace("/* S4FLAT:CSS */", css)
    out = out.replace("<!-- S4FLAT:ICONS -->", sprite)
    out = out.replace("<!-- S4FLAT:JS -->", "<script>\n" + js + "</script>")
    return out, used, missing


def main():
    args = sys.argv[1:]
    if len(args) < 2:
        print(__doc__)
        sys.exit(2)
    remix = None
    if "--remix" in args:
        i = args.index("--remix")
        remix = args[i + 1]
        del args[i:i + 2]
    no_check = "--no-check" in args
    args = [a for a in args if a != "--no-check"]
    src_path, out_path = args[0], args[1]
    out, used, missing = build(read(src_path), remix)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(out)
    print("Built %s (%d KB, %d icons)." % (out_path, len(out) // 1024, len(used) - len(missing)))
    if missing:
        print("Missing icons (not in the bundled set): " + ", ".join(missing))
        print("Fetch them: npm pack remixicon@4.5.0 && tar xzf remixicon-4.5.0.tgz, then rebuild with --remix package/icons")
    if not no_check:
        sys.path.insert(0, HERE)
        import check_page
        errors, warns = check_page.check(out)
        for e in errors:
            print("ERROR  " + e)
        for w in warns:
            print("WARN   " + w)
        if not errors and not warns:
            print("OK     No S4 Flat rule breaks found.")
        sys.exit(1 if errors or missing else 0)


if __name__ == "__main__":
    main()
