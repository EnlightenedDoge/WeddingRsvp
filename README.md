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
- Admin: http://localhost:8081/admin
- With `Sms:Provider=Log` (default) messages are written to the console instead of being sent. `Sms4Free` sends for real
  (set `Sms__ApiUser/ApiPassword/ApiKey`). Couples start with **0 SMS allocation**: grant credits on the site-admin couple page before anything sends.
- Tests: `dotnet test` (projects target net10.0).

## Deploy (Docker + Nginx / Nginx Proxy Manager, e.g. TrueNAS Scale)

1. `cp .env.example .env` and fill it in:
   - `App__PublicBaseUrl` / `App__AdminBaseUrl`: your real hosts. They are not in `docker-compose.yml`, and the app logs a warning at
     startup if they are missing. The SMS link is `App__PublicBaseUrl` + `/` + a 10-character token, so a short host saves SMS characters.
   - `PUBLIC_HOST_PORT` / `ADMIN_HOST_PORT` / `BIND_ADDRESS`: host ports the container publishes (default 8080 / 8081) and the address
     they bind to (`127.0.0.1` if the proxy runs on the same machine).
   - `DATA_DIR`: where the SQLite DB, uploaded images and the data-protection keys live. On TrueNAS use an absolute dataset path
     (e.g. `/mnt/tank/apps/wedding-rsvp`). Keep the folder private: the keys are stored unencrypted. Back it up.
   - `App__BootstrapAdminEmail` / `App__BootstrapAdminPassword` for the first site admin (remove them afterwards), and the `Sms__*` settings.
2. `docker compose up -d --build`. On TrueNAS Scale you can instead paste the compose file into a Custom App and set the same
   variables there. If the proxy is another container on the same host and you prefer a shared Docker network over published ports,
   add the override: `docker compose -f docker-compose.yml -f docker-compose.npm.yml up -d --build`.
3. In the proxy add two hosts, both with SSL, pointing at `http://<docker-host>:<port>`:
   - your public host (e.g. `example.com`) → `PUBLIC_HOST_PORT`
   - `admin.<your host>` → `ADMIN_HOST_PORT`, with an **Access List** (basic auth) as a second layer; the app's own sign-in
     (Identity, lockout, TOTP for the site admin) is the real protection.
   - Both hosts need `X-Forwarded-Proto` / `X-Forwarded-For` (Nginx Proxy Manager sends them). Without HTTPS reaching the app as
     forwarded, sign-in returns a 500 because the antiforgery cookie is secure-only.
   - Raise the upload limit: invitation images can be up to 10 MB and nginx's default is 1 MB. In Nginx Proxy Manager's host
     "Advanced" box add `client_max_body_size 10m;`.
   - Do not expose the admin port to the internet directly; only through the proxy. `/admin*`, `/signin*` and `/set-password` return
     404 on the public port.
4. Check: `https://<public host>/healthz` returns `ok`, `https://<public host>/signin` returns 404, `https://admin.<your host>/signin` loads.

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
