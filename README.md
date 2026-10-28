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

## Deploy (Docker + Nginx Proxy Manager)

1. `cp .env.example .env` and fill it in. `NPM_NETWORK` is the Docker network NPM is on
   (`docker network ls`).
2. `docker compose up -d --build`
3. In NPM add two proxy hosts, both with SSL:
   - `rsvp.example.com` → `http://wedding-rsvp:8080`
   - `admin.example.com` → `http://wedding-rsvp:8081`, with an **Access List** (basic auth) attached.
     Once sign-in is built this becomes a second layer; the app's own sessions are the real protection.
   - Set `App__BootstrapAdminEmail` / `App__BootstrapAdminPassword` in `.env` for the
     first site admin.
4. Data (SQLite DB and the invitation image) lives in `./data`. Back it up.

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
