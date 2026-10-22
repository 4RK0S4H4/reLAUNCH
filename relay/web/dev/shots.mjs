// Screenshots of the PWA against a running `wrangler dev` + fake PC.
// Usage: node web/dev/shots.mjs [relayUrl] [outDir]
import { chromium } from "playwright-core";
import fs from "node:fs";

const base = process.argv[2] ?? "http://127.0.0.1:8787";
const out = process.argv[3] ?? "shots";
fs.mkdirSync(out, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM ?? "/opt/pw-browsers/chromium-1194/chrome-linux/chrome", args: ["--no-sandbox"] });
const errors = [];
let overflow = 0;

async function session(name, { w, h, dark }, steps) {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, colorScheme: dark ? "dark" : "light", deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on("pageerror", (e) => errors.push(`${name}: ${e.message}`));
  page.on("console", (m) => { if (m.type() === "error") errors.push(`${name} console: ${m.text()}`); });
  await steps(page, async (label) => {
    await sleep(500);
    const over = await page.evaluate(() => document.documentElement.scrollWidth - innerWidth);
    if (over > 0) { overflow++; console.log(`OVERFLOW ${over}px in ${name}-${label}`); }
    await page.screenshot({ path: `${out}/${name}-${label}.png` });
    console.log(`${name}-${label}.png`);
  });
  await ctx.close();
}

const flow = (code) => async (page, shot) => {
  await page.goto(`${base}/`);
  await sleep(600);
  await shot("0-unpaired");
  await page.click("#pairBtn");
  await sleep(600);
  await shot("1-pair-sheet");
  await page.fill("#code", "WRONG");
  await page.click("#pairSheet button[type=submit]");
  await sleep(500);
  await shot("2-pair-error");
  await page.fill("#code", code);
  await page.click("#pairSheet button[type=submit]");
  await page.waitForSelector(".f-tile", { timeout: 15000 });
  await sleep(1500);
  await shot("3-favorites");
  await page.click(".f-tile:first-child");
  await sleep(700);
  await shot("4-card");
  await page.click("#bRestart");
  await sleep(300);
  await shot("5-card-restarting");
  await page.waitForFunction(() => document.querySelector("#cardLoad").hidden, null, { timeout: 15000 });
  await sleep(400);
  await page.click("#cardClose");
  await sleep(500);
  await page.click('[data-go="apps"]');
  await sleep(700);
  await shot("6-all-apps");
  await page.fill("#q", "pla");
  await sleep(600);
  await shot("7-search");
  await page.fill("#q", "");
  await sleep(400);
  await page.click('[data-go="act"]');
  await sleep(800);
  await shot("8-activity");
  await page.click('[data-go="fav"]');
  await sleep(600);
  await page.click("#moreBtn");
  await sleep(500);
  await shot("9-menu");
};

const ctl = (path) => fetch(`http://127.0.0.1:8799${path}`).then((r) => r.text());

// Phone, dark: manual code entry, card, all apps, activity.
await session("phone-dark", { w: 400, h: 860, dark: true }, flow("SHOT000001"));

// Phone, light: pairing through the QR link, then the PC state variants.
await session("phone-light", { w: 400, h: 860, dark: false }, async (page, shot) => {
  await page.goto(`${base}/#pair=SHOT000002`);
  await page.waitForSelector(".f-tile", { timeout: 15000 });
  await sleep(1500);
  await shot("1-favorites");
  await ctl("/session/locked"); await sleep(800);
  await shot("2-locked");
  await ctl("/session/noSession"); await sleep(800);
  await shot("3-no-session");
  await ctl("/session/active");
  await ctl("/disconnect"); await sleep(1200);
  await shot("4-pc-offline");
  await page.click('[data-go="apps"]'); await sleep(800);
  await shot("5-all-apps-offline");
  await page.click('[data-go="fav"]'); await sleep(500);
  await page.click(".f-tile:first-child"); await sleep(800);
  await shot("6-card-offline");
  await ctl("/connect"); await sleep(2500);
});

// Desktop.
await session("desktop-dark", { w: 1440, h: 900, dark: true }, async (page, shot) => {
  await page.goto(`${base}/#pair=SHOT000003`);
  await page.waitForSelector(".f-tile", { timeout: 15000 });
  await sleep(1500);
  await shot("1-favorites");
  await page.click(".f-tile:nth-child(2)"); await sleep(800);
  await shot("2-card");
  await page.click("#cardClose"); await sleep(500);
  await page.click('[data-go="apps"]'); await sleep(800);
  await shot("3-all-apps");
});

await browser.close();
console.log(errors.length ? "ERRORS:\n" + errors.join("\n") : "no script errors");
console.log(overflow ? `${overflow} overflow report(s)` : "no sideways overflow");
