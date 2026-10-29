# WeddingRsvp

Wedding invitations and RSVPs over SMS: each guest gets a personal link to a mobile page where they
confirm attendance and headcount. Reminders and a thank-you message go out automatically; the couple
follows everything on an admin dashboard.

The platform is built for several couples: a **site admin** creates each couple's account, and each couple
signs in to see only their own guests and messages. The admin UI is available in **Hebrew (RTL) and English (LTR)**.
ASP.NET Core Razor Pages + EF Core SQLite, one container, meant to sit behind a reverse proxy.

## Features

- **Guest page** (phone first): the couple's invitation image, attending yes/no, adults and children, add to calendar
  (`.ics` and Google), Waze / Google Maps link. A guest can reopen the link and change the answer until the event starts.
  The couple chooses the page look (background colour or photo, button colour); text contrast adapts automatically.
- **Language mode per couple:** Hebrew only, English only, or both. In `Both` mode one SMS carries the Hebrew text, an empty line,
  then the English text (each with its own link), and the page has a he/en toggle (Hebrew by default). One invitation image and one
  set of templates per language.
- **Messages:** invitation, reminders to people who have not answered, a thank-you the day after. Test SMS to yourself. A pause switch.
  Status moves forward only: Sent, then Delivered (provider delivery report), then Opened (a person actually looked at the page).
- **Guest list:** CSV import (Hebrew or English headers, UTF-8 or Windows-1255), phone validation, duplicates skipped, manual edits and
  phone-call answers, CSV export. Each invitation covers a number of people (or "no limit").
- **Multi-tenant accounts:** site admin creates couples and grants each an SMS allocation; couples change their password and can
  delete all their information (CSV export offered first).
- **Security:** ASP.NET Core Identity, lockout, TOTP for the site admin, audit log, per-IP rate limiting, Cloudflare Turnstile (optional).

## How messaging works

- **Nothing about a couple is assumed.** Names, date, venue and sender name are entered by the couple or the site admin. Templates use
  `{name} {couple} {link} {date} {venue}`; **no invitation, reminder or thank-you is queued while a placeholder its template uses has no value**
  (`{name}` and `{link}` are never missing; test SMS are exempt). Without a start date the guest page hides date and calendar links and
  RSVP counts as closed.
- **Sending window (Israel time):** 09:00 to 21:00, Fridays and the eve of a yom tov until 13:00, never Saturday or a yom tov
  (computed from the Hebrew calendar). Nothing is sent outside it or while paused.
- **Reminders:** only to guests whose invitation was sent and who have not answered, every N days (default 4), at most M (default 2), never after
  the event starts; re-checked at dispatch. **Thank-you:** the day after the event at a set hour (default 12:00), valid for 48 hours, to attendees
  (default) or everyone invited.
- **No double sends:** a guest never has two queued messages of the same kind; each message is saved right after it is sent.
- **Encoding and cost:** Hebrew makes an SMS UCS-2 (70 characters, 67 per part when split); English-only is GSM-7 (160 / 153). The Event page shows
  a live character and credit count per template, using the active provider's cost rule. `Both` mode costs more and the Messages page counts real credits needed.
- **Allocation:** each couple has an append-only ledger of SMS credits granted by the site admin (couples start at 0). The dispatcher checks it before
  sending; a couple whose allocation is used up has messages held `Queued`, not failed. Over-allocation (granting more than the provider balance) is allowed
  because the circuit breaker below makes it safe. Couples see only their own remaining allocation, never provider details.

## SMS provider

Providers sit behind `ISmsProvider` and are chosen by `Sms:Provider`: `Log` (development, writes to the console) or `Sms4Free`
([sms4free.co.il](https://www.sms4free.co.il), prepaid packs that do not expire). Adding a provider is one class plus one config value;
field names, phone formats and status codes stay inside the provider class.

Each send returns a failure kind that decides what happens next:

| Kind | Meaning | Action |
|---|---|---|
| `Sent` | accepted | mark Sent, debit the ledger |
| `Transient` | provably never reached the provider (DNS, connection refused) | retry with backoff |
| `Rejected` | provider refused this message (bad number, content) | mark Failed, no retry |
| `Unknown` | timeout or unreadable reply, may have been sent | mark Failed ("check the provider log"), **never auto-retried** |
| `AccountLevel` | out of credit, bad credentials, unverified sender | trip the **circuit breaker**: all sending stops, messages stay `Queued` with attempts untouched, the site admin sees a banner, couples see "messages are delayed" |

SMS4FREE specifics (verified live): the API takes `user` (the registered mobile number), `pass` and `key` in a JSON body, one recipient per
request, and returns an accepted count rather than a message id; recipients are sent as `+9725XXXXXXXX`; Israeli numbers only. Credits for Hebrew:
1 credit up to 134 characters, then +1 per 67 (62 chars = 1, 138 = 2, 163 = 2). English-only (GSM-7) credit cost is not verified yet. A custom
(Latin) sender name works after a package is bought; the provider may ask for ID verification. Set `Sms__ApiUser`, `Sms__ApiPassword`, `Sms__ApiKey`.

**Delivery reports:** set `App__DeliveryWebhookSecret` (a long random value) and a public, non-localhost `App__PublicBaseUrl`. Every send then includes the
callback `<PublicBaseUrl>/webhooks/sms-delivery/<secret>` and our message id; the provider POSTs `to`, `status`, `msgId` back (1 delivered, 5 not delivered,
6 kosher voice call). The secret is compared in constant time and the phone number must match. **Opened** is recorded when the page has been visible for about two
seconds and the browser reports back (and Turnstile confirms, if on). Merely loading the page does not count, because scanners fetch links within a second of sending.

## Architecture

- `Domain/`: `Guest` (one invitation is one phone; `InvitedCount` 0 means no limit, answers up to 99), `MessageLog` (also the outbox),
  `EventSettings` (one row per couple), `Couple`, `AppUser`, allocation ledger.
- `Services/CampaignService` decides **what** to send and queues `MessageLog` rows; `MessageDispatcher` decides **when** and sends (window, pause,
  batch throttle, retries, per-message save); `MessagingWorker` ticks every 20 s, per couple: plan, then dispatch. `MessageTemplates` renders SMS bodies
  and reports missing placeholders.
- **Tenant isolation:** an EF global query filter on `CoupleId`, driven by a scoped `ITenantContext` built from the signed-in user's claim; never
  hand-write `Where(CoupleId == ...)`. Public routes resolve the tenant from the guest token. A site admin acts on a couple only through an explicit,
  audited "open as couple" session. `SaveChanges` refuses writes (add, modify, delete) that name another tenant.
- **Two ports:** 8080 public (guest pages, couples' sign-in and portal), 8081 private (site admin). `/admin/site/**` is a bare 404 on the public port and the
  site-admin account cannot sign in there. Sign-in and the portal need HTTPS (as forwarded by the proxy) outside Development; the only exception is the
  private port with `App__AdminAllowPlainHttp=true`.
- Times: `*Utc` properties are UTC, `*Local` are Israel local time (`IsraelClock`). SQLite via EF: use `DateTime`, not `DateTimeOffset`.
- UI strings are inline pairs, `@Ui.T("עברית", "English")`; the language switch is `/lang/{he|en}` (culture cookie). Guest tokens are 10 characters from an
  unambiguous alphabet with a unique index; the public route is `/{token}`.

## Run locally

```bash
dotnet run --project src/WeddingRsvp      # run from src/WeddingRsvp if static files (wwwroot) are not served
dotnet test                               # projects target net10.0 (.NET 10 SDK)
```

- Guest pages: http://localhost:8080/{token}
- Couples sign in at http://localhost:8080/signin (public port); the site admin only on the private port: http://localhost:8081/signin
- The first site admin is created from `App__BootstrapAdminEmail` / `App__BootstrapAdminPassword` when the database has no users (remove them afterwards).
- With `Sms:Provider=Log` (default) messages are written to the console instead of being sent. `Sms4Free` sends for real. Couples start with **0 SMS
  allocation**: grant credits on the site-admin couple page before anything sends.
- Schema changes: `dotnet ef migrations add <Name> -p src/WeddingRsvp` (needs `dotnet tool install --global dotnet-ef`); the app applies migrations on startup.
- To try it from a phone on your LAN: set `App__PublicBaseUrl=http://<lan-ip>:8080`, run with `ASPNETCORE_ENVIRONMENT=Development`, use `http://` (not `https://`),
  stay on the same Wi-Fi (no guest network or AP isolation), and open the ports in the host firewall (firewalld example:
  `sudo firewall-cmd --zone=home --add-port=8080/tcp --add-port=8081/tcp`).

## Deploy (Docker + Nginx / Nginx Proxy Manager, e.g. TrueNAS Scale)

1. `cp .env.example .env` and fill it in:
   - `App__PublicBaseUrl`: your real public host. It is not in `docker-compose.yml`, and the app logs a warning at startup if it is
     missing. Couples' set-password links use it too. The SMS link is `App__PublicBaseUrl` + `/` + a 10-character token, so a short host saves SMS characters.
   - `PUBLIC_HOST_PORT` / `ADMIN_HOST_PORT`: host ports the container publishes (default 8080 / 8081). `BIND_ADDRESS` is the address
     they bind to (`127.0.0.1` if the proxy runs on the same machine); `ADMIN_BIND_ADDRESS` sets the bind address of the private admin port only (default `127.0.0.1`, set a LAN IP to reach it from another machine).
   - `APP_UID` / `APP_GID`: the user the container runs as (default `1654`, the image's non-root user). Make the data folder writable by it
     once: `chown -R 1654:1654 <DATA_DIR>`. On TrueNAS use the app's uid, e.g. `568`.
   - `DATA_DIR`: where the SQLite DB, uploaded images and the data-protection keys live. On TrueNAS use an absolute dataset path
     (e.g. `/mnt/tank/apps/wedding-rsvp`). Keep the folder private: the keys are stored unencrypted. Back it up.
   - `App__BootstrapAdminEmail` / `App__BootstrapAdminPassword` for the first site admin (remove them afterwards), and the `Sms__*` settings.
2. `docker compose up -d --build`. On TrueNAS Scale you can instead paste the compose file into a Custom App and set the same
   variables there. If the proxy is another container on the same host and you prefer a shared Docker network over published ports,
   add the override: `docker compose -f docker-compose.yml -f docker-compose.npm.yml up -d --build`.
3. **Public host** (e.g. `example.com`), with SSL → `http://<docker-host>:<PUBLIC_HOST_PORT>`. It serves the guest pages and the couples'
   sign-in and portal (`/signin`, `/admin/**`, `/set-password`). `/admin/site/**` returns 404 here, and the site-admin account cannot sign in here.
   - It needs `X-Forwarded-Proto` / `X-Forwarded-For` (Nginx Proxy Manager sends them). The app honours them **only from trusted proxy networks**:
     `App__TrustedProxyNetworks` (comma-separated CIDRs; default loopback, RFC1918 private ranges, IPv6 ULA and Tailscale `100.64.0.0/10`).
     A client connecting directly from elsewhere cannot spoof its IP (rate limits, audit log) or claim HTTPS. Narrow the list to your proxy's address if you can. Without HTTPS reaching the app as forwarded,
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

### TrueNAS Scale (24.10+) custom app
A published image plus a ready YAML: see [`deploy/truenas/README.md`](deploy/truenas/README.md). GitHub Actions builds and publishes the image on every push to `main`.

## Using it (site admin)

1. Sign in on the private port at `/signin` (set up TOTP under **Security**), open **Couples**.
2. **Create a couple**: names, wedding date, optional venue, sign-in email, sender name. There is no email provider: the page shows a **one-time
   set-password link** that you copy and pass on. **Generate a new link** is also the password reset.
3. **Grant SMS allocation** on the couple page (the ledger shows every grant and use; the page warns when a grant exceeds the provider balance).
4. Use **Open as couple** to help (audited, with a banner), or suspend an account. **Provider** shows the SMS balance and the circuit breaker;
   **Audit** lists sign-ins, password links, grants and deletions.

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

## Status and roadmap

Built and tested: the multi-tenant platform, sign-in, site admin, bilingual UI, language modes, SMS provider layer with circuit breaker and
allocation ledger, delivery reports (verified live), Turnstile, guest page and preview. Check the current test count with `dotnet test`.

Still open before relying on it for a real wedding:
- A full run with a real couple on the public host (reminders and the thank-you have never run live), and a live check of Turnstile on the real domain.
- Real-phone QA of the guest page (iOS Safari, Android Chrome, in-app browsers), including the error, saved and closed states and the background-photo variant.
- Backups of the data folder (a cron copy of `rsvp.db` and `media/`; keep backups private and consider encrypting them), and a short idle timeout for the site admin.
- Untested TrueNAS deployment (see `deploy/truenas/README.md`).
- English-only (GSM-7) credit cost, not yet verified against the provider.
- A privacy note and a data-retention agreement with each couple (delete guest data when they confirm after the event).
- CSP nonces to replace `'unsafe-inline'` for scripts and styles.

**Planned, not built: hidden invitation content.** A plain fetch of `/{token}`, `/{token}/invitation` or `/{token}/event.ics` returns personal content to
anything that reads the SMS: carrier scanners, link previews, crawlers. The plan ("reveal on verify"): `/{token}` returns a neutral shell until the browser passes
an invisible Turnstile check, then sets a signed cookie scoped to that guest's path; the per-guest files and the RSVP POST need the cookie; a "tap to view"
fallback if Cloudflare is blocked; all behind a feature flag so it can be rolled back. It cannot hide anything from the SMS provider, from a real browser that
passes Turnstile, or from the couple. Prerequisite: the real client IP behind Cloudflare and nginx (read `CF-Connecting-IP` from trusted proxies only); an optional
Cloudflare WAF rule on the guest-link path complements it. Open decision to settle first: count "Opened" at the verified reveal instead of the current
two-second report.

## Notes for contributors

- Keep the invariants: a couple can never touch another's data; a guest never has two queued messages of one kind; reminders never go to someone who
  answered; provider outages never lose or double-send (`AccountLevel` leaves messages `Queued`, `Unknown` is never retried); nothing is hardcoded about a couple.
- A Razor `<form>` with an explicit `action=` gets **no antiforgery token**: add `asp-antiforgery="true"`.
- Razor local functions containing tag helpers fail (MVC1006/CS4033): use a partial.
- Tests that seed messages must use `Kind = Test`, or the planner (correctly) queues nothing for a guest that already has a queued invite.
- Colours from the couple are validated server-side as `#RRGGBB` before they reach CSS; never accept free-form CSS.
- Not in git: `.env`, `data/`, `.local-test/` (a throwaway test database), local tool config.
