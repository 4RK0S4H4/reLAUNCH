#!/usr/bin/env python3
"""Build the phone PWA into ../public.

Stitches app.css / app.js into index.src.html, runs the vendored S4 Flat build script (inlines the kit and the
icons used, then checks the rules), and copies static files. Fails on any S4 Flat error or warning.

Usage: python3 relay/web/build.py [--out DIR]
"""
import hashlib
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "s4-flat", "scripts"))
import build_page  # noqa: E402
import check_page  # noqa: E402


def read(path):
    with open(os.path.join(HERE, path), encoding="utf-8") as f:
        return f.read()


def main():
    out_dir = os.path.abspath(os.path.join(HERE, "..", "public"))
    if "--out" in sys.argv:
        out_dir = os.path.abspath(sys.argv[sys.argv.index("--out") + 1])

    src = read("index.src.html").replace("/* APP:CSS */", read("app.css")).replace("/* APP:JS */", read("app.js"))
    html, used, missing = build_page.build(src)
    if missing:
        sys.exit("Missing icons: " + ", ".join(missing))
    errors, warns = check_page.check(html)
    for e in errors:
        print("ERROR  " + e)
    for w in warns:
        print("WARN   " + w)
    if errors or warns:
        sys.exit(1)

    # Fresh output: remove files this build owns, keep nothing stale.
    shutil.rmtree(out_dir, ignore_errors=True)
    os.makedirs(out_dir)
    static = os.path.join(HERE, "static")
    for root, _, files in os.walk(static):
        for name in files:
            rel = os.path.relpath(os.path.join(root, name), static)
            dest = os.path.join(out_dir, rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.copy2(os.path.join(root, name), dest)

    version = hashlib.sha256(html.encode()).hexdigest()[:12]
    sw_path = os.path.join(out_dir, "sw.js")
    with open(sw_path, encoding="utf-8") as f:
        sw = f.read().replace("__VERSION__", version)
    with open(sw_path, "w", encoding="utf-8") as f:
        f.write(sw)
    with open(os.path.join(out_dir, "index.html"), "w", encoding="utf-8") as f:
        f.write(html)
    print("Built %s (%d KB, %d icons, version %s). S4 Flat checks: OK." % (out_dir, len(html) // 1024, len(used), version))


if __name__ == "__main__":
    main()
