# Handoff (written 2026-10-25, refreshed 2026-10-28)

Everything needed to continue this project on another machine. Read `CLAUDE.md` (project rules) and `SPEC.md`
(newer, wins on conflicts) too. This file is the session context that isn't in either.

## What the product is
SMS wedding invitation + RSVP platform. ASP.NET Core 8 Razor Pages + EF Core SQLite, multi-tenant: a **site admin**
creates **couple** accounts; each couple sees only its own guests/messages/settings. Guests get an SMS with a personal
link (`/{token}`) to a phone-first RSVP page. Hebrew RTL / English LTR throughout.

## State
- Builds with 0 errors, **160 tests pass** (`dotnet test`, net10.0).
- Built: tenancy (EF query filters via `ITenantContext`), Identity sign-in (lockout 5/15 min, 10-char passwords, cookie
  `wr.auth`), one-time set-password links the site admin passes on, TOTP for the site admin, audit log, "open as couple"
  (audited, banner, account page blocked while acting), "delete all information" (deletes the account, keeps a tombstone),
  per-couple worker, language mode (Hebrew only / English only / both with toggle, Hebrew default), two invitation images,
  per-language templates, couple-chosen guest-page look (validated `#RRGGBB`), bilingual admin/site-admin/sign-in pages.
- Manually smoke-tested end to end (sign-in, create couple, tenant separation, open/exit couple, guest page he/en, RSVP POST, .ics).
- The user was mid manual test from a phone over the LAN (see recipe below).

## Deferred / open (priority order)
1. **SMS provider layer: built and live-tested 2026-10-28** (providers, breaker, ledger). Live results with the real account:
   balance diff confirmed 62 chars = 1 credit, 138 = 2, 163 = 2; sender name `Invitation` accepted. One early 138-char message was accepted by
   the API but never received; later ones arrived (cause unknown: first send from a new sender name, a carrier filter or a delay).
   Recipients are now sent country code first (`+9725…`), not yet live-verified. Still open: English-only (GSM-7) credit count,
   delivery reports (DLR) work live (verified 2026-10-28: callback `msgId` = our `usermsgid`; Delivered arrived ~2 min after sending). Turn on with `App__DeliveryWebhookSecret` + a public `App__PublicBaseUrl`. Opened links mark messages Opened. Couples start with 0 allocation.
2. ~~First EF migration~~ done: `InitialCreate` (run `dotnet ef migrations add <Name> -p src/WeddingRsvp` for later schema changes).
3. ~~TOTP banner, audit-log viewer~~ done (`/admin/site/audit`).
4. Guest page visual QA done in headless Chromium (320/360/390, landscape); still unchecked: error/saved/closed states, background-photo variant, real iOS Safari.
5. Done: Jewish yom tov days in `SendingWindow`, live credit counter (per-provider cost rule). WhatsApp share link is out of scope.

## Decisions already made (don't re-ask)
Identity cookie auth (data is very sensitive); no email provider (admin copies a set-password link); couples change
password but not email; over-allocation of SMS allowed, the circuit breaker makes it safe; couples never see provider
details, only a neutral "messages are delayed" note; English templates fall back to Hebrew couple/venue names unless the
`*En` fields are set; both-mode SMS = Hebrew text, newline, English text, each with its own link.

## Run / test
```bash
dotnet test
cd src/WeddingRsvp && dotnet run          # public :8080, admin+sign-in :8081
```
Run from `src/WeddingRsvp` (cwd) or `wwwroot` isn't served. `/admin/site/**` is 404 on 8080 (public); the couples' `/signin` and `/admin/**` work on both ports; the site admin can only sign in on 8081.

LAN test recipe (what was used):
```bash
cd src/WeddingRsvp && ASPNETCORE_ENVIRONMENT=Development App__DataDir=<dir> \
  App__PublicBaseUrl=http://<lan-ip>:8080 \
  App__BootstrapAdminEmail=<email> App__BootstrapAdminPassword='<10+ chars>' \
  setsid nohup dotnet bin/Debug/net10.0/WeddingRsvp.dll > app.log 2>&1 & disown
```
Phone could not connect: host uses **firewalld**; LAN interface is in zone `home`. Fix:
`sudo firewall-cmd --zone=home --add-port=8080/tcp --add-port=8081/tcp` (runtime only). Also: same Wi-Fi (no guest
network/VPN/AP isolation), and use `http://` not `https://`. The bootstrap admin is created only on an empty DB.

## Pitfalls hit (save yourself the time)
- Razor `<form>` with an explicit `action=` gets **no antiforgery token**: add `asp-antiforgery="true"`.
- Razor local functions containing tag helpers fail (MVC1006/CS4033): use a partial (`Admin/_ImageSlot.cshtml`).
- Never hand-write `Where(CoupleId == ...)`; rely on the query filter. Tests seeding messages must use `Kind = Test`
  or the planner (correctly) queues nothing for a guest with a queued Invite.
- `pkill -f "<pattern>"` can kill your own shell in the Claude sandbox; kill by exact pid from `ss -ltnp`.

## Design
Canvas "Wedding Admin" (private to the owner, on claude.ai): https://claude.ai/artifact/6cvjtSuF3xYHfSrzLV3Wyi.
Implemented tokens are in `wwwroot/css/admin.css` (couple accent `#1F6F5F`, site admin purple `#5B4BB5`, Heebo) and
`public.css` (guest portal).

## New machine
`git clone git@github.com:EnlightenedDoge/WeddingRsvp.git`; install the .NET 10 SDK;
`dotnet test`; `cp .env.example .env`; `docker compose up -d --build` (see README for Nginx Proxy Manager).
Not in git: `.env`, `data/`, `.local-test/` (throwaway test DB), `opencode.json` (local model config).
