#!/usr/bin/env node
/* Screenshot a built S4 Flat page at phone (dark + light) and desktop sizes, and report sideways overflow and script errors.

   One-time setup in any working folder (the npm registry is reachable from the sandbox; this bundles its own Chromium):
     npm i @sparticuz/chromium@131.0.1 puppeteer-core@23.10.4
   Run from that folder:
     node <skill>/scripts/screenshot.js page.html [outdir]
   Then view the PNGs (split tall ones before reading). Google Fonts don't load offline, so text falls back to a system font. */
const path = require('path');
const req = (m) => require(require.resolve(m, { paths: [process.cwd()] }));
(async () => {
  const chromium = req('@sparticuz/chromium'); const puppeteer = req('puppeteer-core');
  const file = path.resolve(process.argv[2] || ''); const out = process.argv[3] || '.';
  const browser = await puppeteer.launch({ executablePath: await chromium.executablePath(), args: chromium.args, headless: true });
  const page = await browser.newPage(); const errs = [];
  page.on('pageerror', (e) => errs.push(e.message));
  for (const [name, w, h, dark] of [['phone-dark', 400, 860, true], ['phone-light', 400, 860, false], ['desktop-dark', 1440, 900, true]]) {
    await page.setViewport({ width: w, height: h });
    await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: dark ? 'dark' : 'light' }]);
    await page.goto('file://' + file, { waitUntil: 'load' }); await new Promise((r) => setTimeout(r, 400));
    const over = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth);
    const p = path.join(out, name + '.png'); await page.screenshot({ path: p, fullPage: true });
    console.log(name + ': ' + p + (over > 0 ? '  OVERFLOW ' + over + 'px (something is wider than the screen)' : '  no overflow'));
  }
  console.log('script errors: ' + (errs.length ? errs.join(' | ') : 'none'));
  await browser.close();
})().catch((e) => { console.error(e.message); process.exit(1); });
