# WeddingRsvp

Wedding invitations and RSVPs over SMS: each guest gets a personal link to a mobile page where they
confirm attendance and headcount. Reminders and a thank-you message go out automatically; the couple
follows everything on an admin dashboard.

The platform is built for several couples: a **site admin** creates each couple's account, and each couple
signs in to see only their own guests and messages. The admin UI is available in **Hebrew (RTL) and English (LTR)**.

> **Status:** single-couple scaffold that builds and passes its tests. Multi-couple accounts, sign-in, the
> site-admin pages and the English UI are designed but not built yet (see `SPEC.md` §10–§12 and `CLAUDE.md`).
> Designs: https://claude.ai/artifact/6cvjtSuF3xYHfSrzLV3Wyi (private; ask the owner for access).

## Run locally

```bash
dotnet run --project src/WeddingRsvp
```

- Guest pages: http://localhost:8080/{token}
- Couples sign in at http://localhost:8080/signin (public port); the site admin only on the private port: http://localhost:8081/signin
- With `Sms:Provider=Log` (default) messages are written to the console instead of being sent. `Sms4Free` sends for real
  (set `Sms__ApiUser/ApiPassword/ApiKey`). Couples start with **0 SMS allocation**: grant credits on the site-admin couple page before anything sends.
- Tests: `dotnet test` (projects target net10.0).

## Deploy (Docker + Nginx / Nginx Proxy Manager, e.g. TrueNAS Scale)

1. `cp .env.example .env` and fill it in:
   - `App__PublicBaseUrl`: your real public host. It is not in `docker-compose.yml`, and the app logs a warning at startup if it is
     missing. Couples' set-password links use it too. The SMS link is `App__PublicBaseUrl` + `/` + a 10-character token, so a short host saves SMS characters.
   - `PUBLIC_HOST_PORT` / `ADMIN_HOST_PORT`: host ports the container publishes (default 8080 / 8081). `BIND_ADDRESS` is the address
     they bind to (`127.0.0.1` if the proxy runs on the same machine); `ADMIN_BIND_ADDRESS` overrides it for the private admin port only.
   - `DATA_DIR`: where the SQLite DB, uploaded images and the data-protection keys live. On TrueNAS use an absolute dataset path
     (e.g. `/mnt/tank/apps/wedding-rsvp`). Keep the folder private: the keys are stored unencrypted. Back it up.
   - `App__BootstrapAdminEmail` / `App__BootstrapAdminPassword` for the first site admin (remove them afterwards), and the `Sms__*` settings.
2. `docker compose up -d --build`. On TrueNAS Scale you can instead paste the compose file into a Custom App and set the same
   variables there. If the proxy is another container on the same host and you prefer a shared Docker network over published ports,
   add the override: `docker compose -f docker-compose.yml -f docker-compose.npm.yml up -d --build`.
3. **Public host** (e.g. `example.com`), with SSL → `http://<docker-host>:<PUBLIC_HOST_PORT>`. It serves the guest pages and the couples'
   sign-in and portal (`/signin`, `/admin/**`, `/set-password`). `/admin/site/**` returns 404 here, and the site-admin account cannot sign in here.
   - It needs `X-Forwarded-Proto` / `X-Forwarded-For` (Nginx Proxy Manager sends them). Without HTTPS reaching the app as forwarded,
     sign-in returns a 500 because the antiforgery cookie is secure-only.
   - Raise the upload limit: couples upload invitation images up to 10 MB and nginx's default is 1 MB. In Nginx Proxy Manager's host
     "Advanced" box add `client_max_body_size 10m;`.
   - The delivery-report callback (`/webhooks/sms-delivery/<secret>`) is on this host, so POSTs to it must pass through.
4. **Private admin port** → `http://<docker-host>:<ADMIN_HOST_PORT>`: the site admin (create couples, SMS credits, provider, audit log).
   **Keep it off the internet.** Two ways to reach it:
   - **Directly on your LAN over plain HTTP**: set `App__AdminAllowPlainHttp=true` in `.env`, then open `http://<docker-host>:<ADMIN_HOST_PORT>/signin`.
     Only this port is relaxed; the public port still requires HTTPS. Your admin password crosses the LAN unencrypted, so use it only on a
     network you trust (and not over Wi-Fi you share).
   - **Through a LAN-only HTTPS host**, e.g. `admin.<your host>` with SSL and, in Nginx Proxy Manager's "Advanced" box,
     `allow 192.168.1.0/24; deny all;` (add your VPN range if you use one). Leave `App__AdminAllowPlainHttp` off.
   Browsers may try `https://` on a bare IP:port; type `http://` explicitly (the app serves plain HTTP only).
5. Check: `https://<public host>/healthz` returns `ok`, `https://<public host>/signin` loads, `https://<public host>/admin/site/couples`
   returns 404, and the admin host is not reachable from outside your network.

## Bot protection (Cloudflare Turnstile)

Turnstile guards the couples' **sign-in pages** (`/signin`, `/signin/2fa`, `/set-password`) and the **invitation**: the guest page's
"seen" report only counts as "Opened" when Cloudflare confirms an invisible check. The page itself, the RSVP form and the private admin
port (site admin on your LAN) are never challenged, and the site admin never depends on Cloudflare.

1. In the Cloudflare dashboard create a Turnstile widget whose domains include your public host (for example `example.com`).
2. Put both keys in `.env`: `App__TurnstileSiteKey` (public) and `App__TurnstileSecretKey` (secret, never commit it). Turnstile is ON only
   when both are set; one alone keeps it off and the app logs a warning at startup.
3. Run `scripts/check-turnstile.sh`. It sends a dummy token to Cloudflare with your secret (the secret goes over stdin, is never printed)
   and tells you whether the secret is valid.
4. Restart: `docker compose up -d --build`. Then sign in once at `https://<public host>/signin` to see the widget work.

Behaviour: sign-in fails closed (if Cloudflare is unreachable or the check fails, the attempt is refused before the password is looked at and
does not count towards lockout; refusals are audited as `signin.failed` / `turnstile`). The "seen" report fails open for the guest: a failed
check only means that open is not counted. The widget's token must be for the expected action (`signin`, `signin-2fa`, `set-password`,
`seen`) and the hostname must be your public host (or `App__TurnstileAllowedHostnames`).

Privacy: Cloudflare's script sees each visitor's IP address and browser details. The couple's account page says so.

## Using it (site admin) — planned

1. Sign in at `/signin`, open **Couples**.
2. **Create a couple**: partner names, wedding date, optional venue, sign-in email, SMS quota, sender name.
   The couple gets a link to set their password (or a one-time temporary password).
3. Use **Open as couple** to help, **Reset password**, or suspend an account.

## Using it (for the couple)

Nothing about your wedding is pre-filled. Whatever the site admin left blank, you fill in yourselves.

1. **Event details** (`/admin/event`): names, date, venue, coordinates for Waze, invitation image, message texts.
   Message texts use fields like `{couple}`, `{date}`, `{venue}`, `{name}`, `{link}`; the page shows which are set
   and previews the invitation. **Invitations cannot be sent while a field the text uses is empty.**
2. **Import** the guest list (`/admin/import`): CSV from Excel with columns שם, טלפון, and optionally מוזמנים.
   Fix any invalid numbers it reports.
3. **Messages** (`/admin/messages`): send yourselves a test SMS, then send the invitations
   (or set an automatic send time in event details).
4. Reminders and the thank-you message go out on their own. Use **pause sending** on holidays or if anything looks wrong.
5. Follow responses on the dashboard; record phone-call answers by editing the guest.

Messages are only sent 09:00–21:00, Fridays until 13:00, and never on Saturday.
