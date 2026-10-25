# Handoff (written 2026-10-25)

Everything needed to continue this project on another machine. Read `CLAUDE.md` (project rules) and `SPEC.md`
(newer, wins on conflicts) too. This file is the session context that isn't in either.

## What the product is
SMS wedding invitation + RSVP platform. ASP.NET Core 8 Razor Pages + EF Core SQLite, multi-tenant: a **site admin**
creates **couple** accounts; each couple sees only its own guests/messages/settings. Guests get an SMS with a personal
link (`/{token}`) to a phone-first RSVP page. Hebrew RTL / English LTR throughout.

## State
- Builds with 0 errors, **59 tests pass** (`DOTNET_ROLL_FORWARD=Major dotnet test` while only .NET 10 runtime is installed).
- Built: tenancy (EF query filters via `ITenantContext`), Identity sign-in (lockout 5/15 min, 10-char passwords, cookie
  `wr.auth`), one-time set-password links the site admin passes on, TOTP for the site admin, audit log, "open as couple"
  (audited, banner, account page blocked while acting), "delete all information" (deletes the account, keeps a tombstone),
  per-couple worker, language mode (Hebrew only / English only / both with toggle, Hebrew default), two invitation images,
  per-language templates, couple-chosen guest-page look (validated `#RRGGBB`), bilingual admin/site-admin/sign-in pages.
- Manually smoke-tested end to end (sign-in, create couple, tenant separation, open/exit couple, guest page he/en, RSVP POST, .ics).
- The user was mid manual test from a phone over the LAN (see recipe below).

## Deferred / open (priority order)
1. **SMS provider layer** — the user explicitly deferred this ("We'd do the providers later"): `ISmsProvider`, failure kinds
   `Sent/Transient/Rejected/Unknown/AccountLevel`, platform circuit breaker (AccountLevel => stop all sending, messages stay
   Queued, attempts untouched, site-admin banner), `Sms4FreeProvider` (SPEC §6), per-couple append-only allocation ledger and
   the allocation card on the site-admin couple page. Only `LogSmsSender` exists now.
2. First EF migration (`dotnet tool install --global dotnet-ef`; then delete `src/WeddingRsvp/data/rsvp.db` once).
3. TOTP-required banner for the site admin; audit-log viewer page.
4. Visual QA of the guest page at 320/360/390 and landscape 844x390 in a real browser.
5. Jewish holidays in `SendingWindow`; optional WhatsApp share link; live SMS segment counter.

## Decisions already made (don't re-ask)
Identity cookie auth (data is very sensitive); no email provider (admin copies a set-password link); couples change
password but not email; over-allocation of SMS allowed, the circuit breaker makes it safe; couples never see provider
details, only a neutral "messages are delayed" note; English templates fall back to Hebrew couple/venue names unless the
`*En` fields are set; both-mode SMS = Hebrew text, newline, English text, each with its own link.

## Run / test
```bash
DOTNET_ROLL_FORWARD=Major dotnet test
cd src/WeddingRsvp && dotnet run          # public :8080, admin+sign-in :8081
```
Run from `src/WeddingRsvp` (cwd) or `wwwroot` isn't served. `/admin*`, `/signin*`, `/signout`, `/set-password` are 404 on 8080.

LAN test recipe (what was used):
```bash
cd src/WeddingRsvp && ASPNETCORE_ENVIRONMENT=Development App__DataDir=<dir> \
  App__PublicBaseUrl=http://<lan-ip>:8080 App__AdminBaseUrl=http://<lan-ip>:8081 \
  App__BootstrapAdminEmail=<email> App__BootstrapAdminPassword='<10+ chars>' \
  DOTNET_ROLL_FORWARD=Major setsid nohup dotnet bin/Debug/net8.0/WeddingRsvp.dll > app.log 2>&1 & disown
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
`git clone git@github.com:EnlightenedDoge/WeddingRsvp.git`; install the .NET 8 SDK (or 10 + `DOTNET_ROLL_FORWARD=Major`);
`dotnet test`; `cp .env.example .env`; `docker compose up -d --build` (see README for Nginx Proxy Manager).
Not in git: `.env`, `data/`, `.local-test/` (throwaway test DB), `opencode.json` (local model config).
