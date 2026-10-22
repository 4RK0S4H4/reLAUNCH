# Set up the Cloudflare relay (free, no domain)

The relay is a small Cloudflare Worker that sits between your phone and your PC. Your PC keeps an outbound connection open to it, so you need **no port-forwarding, no VPN, and no extra program on the phone**. Everything fits in Cloudflare's free plan.

You do this once. Total time: about 10 minutes.

## 1. Create a Cloudflare account and a `workers.dev` name

1. Sign up at <https://dash.cloudflare.com/sign-up> (or sign in). No domain and no payment card are needed.
2. Open **Workers & Pages**. If it asks, pick a **workers.dev subdomain** (for example `alex`). Your relay will live at `https://relaunch.<your-subdomain>.workers.dev`.
3. Note your **Account ID**: on the Workers & Pages overview it is shown on the right side (also under *Account home → ⋯ → Copy account ID*).

## 2. Create an API token

1. Go to **My Profile → API Tokens → Create Token**.
2. Use the template **Edit Cloudflare Workers** and create the token.
3. Copy the token now. Cloudflare shows it only once.

## 3. Create a PC key

1. Install reLAUNCH on the PC (see below), open the desktop app and go to **Remote Access**.
2. Click **Generate Key**, then **Copy**. This key lets *your* PC, and nothing else, connect to the relay.

## 4. Add three secrets to the GitHub repository

In the repository: **Settings → Secrets and variables → Actions → New repository secret**.

| Secret | Value |
|---|---|
| `CLOUDFLARE_API_TOKEN` | the token from step 2 |
| `CLOUDFLARE_ACCOUNT_ID` | the account ID from step 1 |
| `RELAUNCH_PC_KEY` | the key from step 3 |

## 5. Deploy

Push to the repository, or open **Actions → build → Run workflow**. When the `relay` job finishes, its *Deploy to Cloudflare* step prints the address, for example `https://relaunch.alex.workers.dev`. The same step stores your PC key on the relay as the secret `PC_KEY`.

If you prefer not to use GitHub for this, deploy by hand from the `relay` folder:

```
cd relay
npm ci
npx wrangler login
npx wrangler deploy
npx wrangler secret put PC_KEY     # paste the key from step 3
```

## 6. Connect the PC

In the desktop app, **Remote Access**, paste the relay address into **Relay URL** and click **Save URL**. The status should turn **Connected** within a few seconds.

- **Rejected key** means the relay has a different `PC_KEY` than this PC. Set the same key in both places (step 4, then deploy again).
- **Reconnecting** means the PC cannot reach the relay (check the internet connection and the URL).

## 7. Pair your phone

1. In the desktop app open **Phones** and click **Pair a Phone**. A QR code appears.
2. On the phone, scan it with the camera and open the link. The phone is now paired.
3. Optional: use the browser menu to **Add to Home Screen** so reLAUNCH opens like an app.

Each QR code works once and expires after 5 minutes. To remove a phone, click **Revoke** next to it in the **Phones** page.

## Install the desktop part on the PC

Download **ReLaunchSetup.msi** from the latest successful run of the *build* workflow (Actions → the run → Artifacts → `reLAUNCH-installer`) and run it. It installs to `C:\Program Files\reLAUNCH`, registers the **reLAUNCH** Windows service (starts automatically, restarts itself if it ever crashes) and adds **reLAUNCH** to the Start menu.

## Good to know

- **Free plan limits.** Cloudflare's free Workers plan allows 100,000 requests a day. reLAUNCH uses a few hundred plus one small heartbeat every 30 seconds, far below that.
- **If the PC is off or asleep** the phone shows "PC is offline" with your favorites greyed out. Wake the PC with your Wake-on-LAN app and the phone updates by itself.
- **If nobody is logged in** the PC cannot start apps (they need a Windows session). The phone says so. A locked PC works normally.
- **Security.** Phones hold a long random token that you can revoke at any time. The relay can only start, stop and restart apps from the PC's own Start menu list; it can never run an arbitrary command or path.
