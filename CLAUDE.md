# WeddingRsvp — notes for Claude Code

SMS-based wedding invitation & RSVP platform. Guests get an SMS with a personal link → a mobile RSVP page
(invitation image, attending yes/no, headcount, add-to-calendar, Waze). Each couple signs in to their own
account and manages their own guests and messages; a **site admin** creates and manages the couples'
accounts. ASP.NET Core 8 Razor Pages + EF Core SQLite, one container behind Nginx Proxy Manager.

Read `SPEC.md` too: where the two disagree, SPEC.md is newer and wins.

## Status (2026-10-28)

See `HANDOFF.md` for session context, deferred work, run recipes and pitfalls.

- The solution **builds with 0 errors** and **160 tests pass**.
- **Phase M is built (core):** `Couple`/`AppUser`, `CoupleId` on Guest/MessageLog/EventSettings, global query filter via
  `ITenantContext`, ASP.NET Core Identity (cookie auth, lockout 5/15 min, 10-char passwords), audit log, site-admin
  pages (`/admin/site/couples`, `/new`, `/{id}`, `/security` with TOTP), `/signin`, `/signin/2fa`, `/set-password`,
  `/admin/account` (change password, delete all information), per-couple worker, per-guest `/{token}/event.ics` and
  `/{token}/invitation`. UI strings use `@Ui.T("עברית", "English")` and `/lang/{he|en}` (culture cookie).
- **Frontend is ported:** design-system `admin.css`, sidebar layouts (`_AdminLayout`, `_SiteLayout`, `_AuthLayout`), every
  couple/site/sign-in page bilingual, "open as couple" (cookie re-issued with `couple_id` + `acting_as_couple`, banner,
  audited, account page blocked while acting), guest portal (`Rsvp.cshtml`, `public.css`, he/en toggle, couple-chosen look),
  Event page with language mode, per-language templates/images and page look. Language mode renders SMS via `MessageTemplates.Body`.
- **Delivery signal:** message status goes Sent -> Delivered (SMS4FREE delivery report via `/webhooks/sms-delivery/{secret}`, `DeliveryReports`) -> Opened (a person actually looked at the guest page: the page reports back with a "seen" beacon, `RsvpService.SeenAsync`; also implies delivered. Merely loading the page does NOT count, because scanners fetch it within a second of sending). Reports need `App__DeliveryWebhookSecret` and a public base URL. Verified live 2026-10-28: the callback `msgId` echoes our `usermsgid`.
- **Turnstile (built 2026-10-28):** `TurnstileVerifier` (siteverify; action and hostname checked; secret never logged) and `TurnstileGate` (active only on the
  public port; `Active`, `CheckFormAsync(action)`). Widget partial `Pages/Shared/_Turnstile.cshtml` on `/signin`, `/signin/2fa`, `/set-password`; token verified
  **before** any credential is looked at, refusals audited (`signin.failed` / `turnstile`) and never counted towards lockout; the guest page's invisible widget feeds the
  `/{token}/seen` report, which counts only when Cloudflare confirms (fails open for the guest). Strict on sign-in, lenient on "seen". On only when both keys are set.
  `scripts/check-turnstile.sh` validates the secret without printing it. `App__TurnstileVerifyUrl` is for tests only.
- **Docker:** `docker-compose.yml` publishes the ports (host ports, bind address and data path come from `.env`); `docker-compose.npm.yml` is an optional shared-network override. Data-protection keys persist in `{DataDir}/keys`. See README "Deploy".
- **SMS provider layer built (2026-10-28):** `ISmsProvider` + `Log`/`Sms4Free` providers (`Sms:Provider`), failure kinds, persisted circuit
  breaker (`PlatformState`, `/admin/site/provider`, banner), per-couple allocation ledger (`SmsAllocationEntry`, card on the couple page,
  couples start at 0). Needs a fresh DB. Live-tested 2026-10-28: billing matches 1 credit up to 134 chars, +1 per 67 (138 chars = 2, 62 = 1, 163 = 2); sender name `Invitation` accepted.
- **Still open before go-live:** a full run with a real couple on the public host (reminders and thank-you have never run live), backups of
  the data folder, TrueNAS deployment (untested; may need a pre-built image), real-phone QA (iOS Safari, Android Chrome), the live Turnstile check on the real domain, privacy note and data-retention agreement. See SPEC.md §8.
- Git repo exists. Couples sign in on the public host; the site admin signs in only on the private admin port (8081). Identity is the real protection.
- The projects target net10.0 (.NET 10 SDK/runtime). Run tests with `dotnet test`.

## Product scope

Original requirements (one couple): import names + phones, send invitations, ask attending + headcount, remind
non-responders, dashboard "X of Y", thank-you the day after.

**Added since:**
1. **Site admin + couple accounts** (multi-tenant). The original plan had no site admin. A site admin creates a
   couple's account; the couple signs in and sees only their own data. See "Multi-tenancy and auth".
2. **Bilingual UI (Hebrew RTL + English LTR)** for every admin, site-admin and sign-in page.
3. **Nothing about a couple is assumed.** Couple names, date, venue, sender name are entered by the couple (or by
   the site admin at account creation). See "Message fields" invariant.

## Requirements → where they live

| Requirement | Implementation |
|---|---|
| Input: names + phones | `/admin/import` → `ImportService` (Hebrew/English headers, Windows-1255 or UTF-8, dedupe by phone) |
| Send invitation automatically | `CampaignService.QueueInvitesAsync` (button on `/admin/messages`, or `AutoInviteAtLocal`) |
| Ask attending + headcount | Public page `Pages/Rsvp.cshtml` at `/{token}` → `RsvpService.SubmitAsync` |
| Reminders to non-responders | `CampaignService.PlanAsync` → reminders every N days, max M |
| Dashboard: who confirmed, X of Y | `/admin` (`DashboardService`), `/admin/guests`, `/admin/export.csv` |
| Thank-you the day after | `CampaignService.PlanAsync` → thank-yous at `ThankYouHourLocal` the day after the event |
| Block sending while event details are missing | `MessageTemplates.MissingFields` → guards in `CampaignService`, `Messages` page, guest edit |
| Site admin creates couples | `/admin/site/couples`, `/admin/site/couples/new` (private admin port only) |
| Sign in (couples on the public host, site admin on the private port) | `/signin` |

## Architecture

- `Domain/` — `Guest` (one invitation = one phone, `InvitedCount` people (adults and children together); **attendance is split: `AttendingCount` is everyone, `AttendingChildren` the children among them, `AttendingAdults` = the rest; old answers count as adults**; **`InvitedCount = 0` means no guest limit**: `Guest.NoGuestLimit`, answers may name up to `Guest.UnlimitedCap` = 99, use `Guest.MaxHeadcount`), `MessageLog` (also the outbox),
  `EventSettings` (event details, templates, schedule, pause switch; **today a single row Id = 1**, becomes one
  row per couple).
- `Services/CampaignService` decides **what** to send and enqueues `MessageLog` rows (Status = Queued).
- `Services/MessageDispatcher` decides **when** and sends: pause switch, sending window, batch throttle,
  retries with backoff, per-message save.
- `Services/MessagingWorker` — BackgroundService tick: `PlanAsync()` then `DispatchAsync()`.
- `Services/MessageTemplates` — placeholders `{name} {couple} {link} {date} {venue}`; `MissingFields()` reports
  event-level placeholders a template uses but that have no value yet.
- `Services/Sms/ISmsProvider` — `LogSmsProvider` (dev) and `Sms4FreeProvider`. **Character limits and credit cost are per provider**: `ISmsProvider.CostRule(unicode)` returns an `SmsCostRule` (single limit, part limit, estimated flag); use `provider.Credits(text)`, never a hardcoded 134/67 (dispatcher, `CampaignService.InviteCostAsync` and the Event page counter all do).
- Two Kestrel ports: **8080 public**, **8081 private admin**. The public port serves guest pages **and the couples' sign-in and
  portal** (`/signin`, `/signout`, `/set-password`, `/admin/**`); the **site-admin pages (`/admin/site/**`) exist only on the private
  admin port** (`PortGuard` middleware returns 404 for them on the public port; the `SiteAdmin` policy also requires the admin
  port; a site-admin account cannot sign in on the public port). Couples' set-password links use `App__PublicBaseUrl`.
  Sign-in and the portal require HTTPS (as forwarded by the proxy) outside Development, enforced per port in `PortGuard.RequiresHttps`;
  the only exception is the private admin port with opt-in `App__AdminAllowPlainHttp=true` (cookies are `SameAsRequest`).

## Multi-tenancy and auth (built — see SPEC.md §10)

Proposed design, confirm open decisions in SPEC.md §12 before building:

- New `Couple` (the tenant) and `User` (email, password hash, role `SiteAdmin` | `Couple`, `CoupleId`).
  `Guest`, `MessageLog`, `EventSettings` get a `CoupleId`; `EventSettings` becomes one row per couple.
- Tenant isolation by an EF **global query filter** on `CoupleId`, driven by a scoped `ITenantContext`
  (from the signed-in user's claim). Never hand-write `Where(CoupleId == …)` in pages.
- Public routes resolve the tenant **from the guest token** (`IgnoreQueryFilters` once, then read the guest's
  `CoupleId`). Tokens stay globally unique. `/event.ics` and `/media/invitation` must become per-guest/per-couple
  URLs (e.g. `/{token}/event.ics`) because they have no signed-in user.
- `MessagingWorker` iterates couples and runs each in its own tenant scope. Pause switch, sending window and SMS
  quota are per couple.
- Auth: **ASP.NET Core Identity** with cookie auth (decided: the data is very sensitive). Lockout, audit log,
  TOTP for the site admin. It replaces the NPM Access List as the real protection. Site admin pages live under
  `/admin/site/**` so the port guard still covers them.
- No email provider: the site admin copies a one-time set-password link and passes it on. Couples can change their
  password, not their email, and have a "delete all information" action (CSV export offered first).
- SMS allocation per couple: append-only ledger granted by the site admin; the dispatcher checks it before sending.
- Language is a **couple** setting: `HebrewOnly` / `EnglishOnly` / `Both`. `Both` = one SMS with both texts and a
  he/en toggle on the guest page (Hebrew default, couple can change it); single modes = that language only, no toggle.
  Two invitation images and per-language templates. English-only SMS are GSM-7, any Hebrew makes UCS-2: count by encoding.
  `Both` costs more: the Event page shows a live per-message estimate next to the Hebrew-only cost when that mode is chosen, and the
  Messages page warns and counts real credits needed (not guests) against the allocation.
- Over-allocation (admin grants more than the provider balance) is allowed; the circuit breaker makes it safe. Couples
  never see provider details, only a neutral "messages are delayed" note and their own remaining allocation.
- "Delete all information" deletes the account too (minimal tombstone kept).
- SMS layer: provider-neutral `ISmsProvider` + registry (swap by config). Failure kinds `Sent/Transient/Rejected/
  Unknown/AccountLevel`. `AccountLevel` (credit exhausted, bad credentials) trips a platform circuit breaker: all
  sending stops, messages stay Queued with attempts untouched, site admin sees a banner; never mark them Failed.
- First site admin is bootstrapped from config (`App__BootstrapAdminEmail` / password) on an empty database.
- Existing single-couple data migrates into one `Couple` row.

## Invariants — don't break these

- `/admin/site/**` (site admin) must stay unreachable on the public port, and the site admin must never be able to sign in
  there. Couples' `/signin` and `/admin/**` are public (Identity, lockout, 10/min per-IP limit on credential posts). Never expose the
  private admin port to the internet.
- **Tenant isolation:** a couple's session can never read or write another couple's guests, messages or settings.
  A site admin acts on a couple only through an explicit "open as couple" session, never by loosening filters.
- A guest never has two **queued** messages of the same kind (all planners are idempotent; they run every tick).
- Reminders are never sent to someone who already answered (cancelled at submit time **and** re-checked at dispatch).
- **Provider outages never lose or double-send:** an `AccountLevel` failure leaves the message Queued (no attempt
  consumed); an `Unknown` outcome (timeout) is never auto-retried.
- Nothing is sent outside `SendingWindow` (Israel time: 09–21, Friday and the eve of a yom tov until 13, never Saturday or a yom tov; see `JewishHolidays`) or while `SendingPaused`.
- **Message fields:** nothing about a couple is hardcoded or defaulted (`CoupleNames`/`Title` default to empty,
  `StartsAtLocal`/`EndsAtLocal` are nullable). No invitation, reminder or thank-you is queued while a placeholder
  its template uses (`{couple}`, `{date}`, `{venue}`) has no value. Enforced in `CampaignService` (not just the UI).
  `{name}` and `{link}` are never "missing". Test SMS are exempt.
- Without a start date: RSVP page hides date and calendar links, `/event.ics` is 404, RSVP counts as closed, the
  planner does nothing. End time defaults to start + 4h.
- Save after each sent message, so a crash can't re-send.
- `*Utc` properties are UTC (converter in `AppDbContext`); `*Local` properties are Israel local time. Use `IsraelClock`.
- SQLite + EF: use `DateTime`, not `DateTimeOffset` (SQLite provider can't compare/order DateTimeOffset).
- Tokens: 10 chars, unambiguous alphabet, unique index. The public route is `/{token:length(10)}`.

## Design handoff (Claude Design → these files)

The designs exist: **https://claude.ai/artifact/6cvjtSuF3xYHfSrzLV3Wyi** ("Wedding Admin" canvas, private to the
owner), including the guest portal (below). Current Razor markup is deliberately plain placeholder;
replace the **visuals**, keep the **contract**.

Canvas artboards (each in Hebrew RTL and English LTR via a `lang` tweak; `*-en` files import the Hebrew file):

| Artboard | Razor target |
|---|---|
| Dashboard (+ phone view) | `Pages/Admin/Index` |
| Guests | `Pages/Admin/Guests/Index` |
| Edit guest | `Pages/Admin/Guests/Edit` |
| Import | `Pages/Admin/Import` |
| Messages (blocked-send state, language-aware cost line, neutral "messages are delayed" banner) | `Pages/Admin/Messages` |
| Event settings (empty first-run state, language mode he/en/both, two invitation images, per-language templates with segment cost, "fields used in messages", live preview) | `Pages/Admin/Event` |
| Sign in | new `/signin` |
| Site admin: Couples | new `/admin/site/couples` |
| Site admin: Create a couple | new `/admin/site/couples/new` |
| Site admin: Couple detail (one-time set-password link to copy, SMS allocation grant/reduce with over-allocation warning, ledger, provider balance, suspend) | new `/admin/site/couples/{id}` |
| **Guest portal** (`/{token}`): 7 states × he/en (first visit, choosing, validation error, already answered, saved attending/declined, RSVP closed), desktop, small phone 320, Android 360, landscape, dark and photo backgrounds | `Pages/Rsvp.cshtml`, `_PublicLayout`, `public.css` |
| Unknown link | `Pages/NotFound.cshtml` |
| My account (change password, no email change, interface language, remaining allocation, delete all information) | new `/admin/account` |

Look: couple portal = dark green sidebar (`#1B2A2F`), accent `#1F6F5F`; site admin = purple sidebar (`#241F3A`),
accent `#5B4BB5`; font Heebo (covers Hebrew + Latin); tweakable accent. Layouts are fluid (flex-wrap, no media
queries) and work at 390px. All sample names/numbers in the canvas are placeholders.

**Guest portal — phone first** (most guests open the SMS link on a phone). Rules the design follows, keep them:
- The couple's uploaded invitation image is the hero: full width, never cropped (`object-fit: contain`), at most
  ~66% of the screen height (`max-width: min(100%, calc(66svh * aspect))`) so the page never opens as just an image.
  On phones a floating "RSVP ↓" cue (`.jump`, hidden ≥900px) sticks to the bottom while the image is on screen and
  scrolls away when the answer card arrives. Two columns (image | answer) only when there is room (flex-wrap, no layout media queries).
- Primary button is sticky at the bottom of the answer card, above the home indicator (`env(safe-area-inset-bottom)`).
- Tap targets ≥ 48px (answer cards 60px, headcount chips 56px, submit 56px); text ≥ 16px so iOS doesn't zoom;
  `100svh` not `100vh`; titles `overflow-wrap: anywhere`; verified layouts at 320, 360, 390 and landscape 844×390.
- Headcount as two groups of big tappable chips, "Adults" (radio `Adults`, from 1) and "Children" (radio `Children`, from 0; hidden for a 1-person invitation), only when attending is chosen; choices that would exceed the invitation are greyed out. Unlimited or large invitations use number boxes. `Attending` radios are `true`/`false`.
- **Couple-controlled look:** background colour (presets or custom hex), optional background photo (behind a dark
  overlay), button/accent colour. Text colours (greeting, title, venue) flip automatically by luminance;
  cards stay white with dark text. Stored on `EventSettings`: `PageBackgroundColor`, `PageBackgroundImageFile`,
  `PageAccentColor`. **Validate colours server-side as `#RRGGBB` before putting them in CSS** (no free-form CSS).
- Language toggle (he | EN) only in `Both` mode; Hebrew default.

**Guest portal** — `Pages/Shared/_PublicLayout.cshtml`, `Pages/Rsvp.cshtml`, `Pages/Index.cshtml`,
`Pages/NotFound.cshtml`, `wwwroot/css/public.css`.
- Hebrew, `dir="rtl"`, mobile-first (guests open it from an SMS on a phone). Keep `noindex`.
- `RsvpModel` provides: `Guest` (Name, InvitedCount, Status, AttendingCount), `Event` (Title, CoupleNames,
  StartsAtLocal (nullable), VenueDisplay), `ImageUrl`, `GoogleCalendarUrl` (empty when no date), `WazeUrl`
  (nullable), `IsClosed`, `JustSaved`, `Error`, `Attending` (bool?), `Count`.
- The form must POST to the same URL with fields named `Attending` ("true"/"false"), `Adults` (int, default 1) and `Children` (int, default 0),
  and include the antiforgery token (the form tag helper does this automatically). A legacy `Count` field is read as adults when `Adults` is missing.
  Attending needs at least 1 adult, and adults + children must fit the invitation (`Guest.MaxHeadcount`).
- States to design: first visit, already answered (can change), just saved (attending / declined),
  validation error, RSVP closed, unknown link (NotFound page). Headcount only matters when attending.
- Calendar: `/event.ics` (iOS/Outlook) and the Google link. Navigation: `WazeUrl`.

**Admin portal** — `Pages/Shared/_AdminLayout.cshtml`, `Pages/Admin/**`, `wwwroot/css/admin.css`.
- Hebrew RTL **and English LTR**, used on phone and desktop. The side menu is sticky on wide screens (one `min-width: 800px` media query); clickable table rows use `tr.clickrow`. Flash messages come via `TempData["Flash"]`.
- Keep page-handler names (`asp-page-handler="SendInvite"`, `Delete`, `QueueInvites`, `TogglePause`,
  `RequeueFailed`, `Test`, `Image`) and the bound field names.
- Language switch: HE/EN in the sidebar. Needs request localization for `he` and `en`, `dir`/`lang` set in the
  layout, and a persisted choice (cookie, later a user preference). Strings move out of the `.cshtml` into
  resources; do not keep Hebrew-only literals in new pages.

Don't move logic into views; if a design needs data the model doesn't expose, add it to the PageModel or a service.

## Open TODOs

- [x] Multi-tenant foundation + auth + site-admin pages (SPEC.md §10, Phase M) — core done (TOTP banner and `/admin/site/audit` viewer added 2026-10-28).
- [x] Bilingual UI plumbing (SPEC.md §11, Phase L): `Ui.T`, `/lang/{code}`, `Labels`.
- [x] Canvas designs ported to Razor (needs visual QA in a browser). Site-couple page lacks the SMS-allocation card (provider phase).
- [x] Added `PageBackgroundColor` / `PageBackgroundImageFile` / `PageAccentColor` to `EventSettings` + the "Guest page look" section on the Event page.
- [x] Sign-out / account entry in the couple sidebars (not in the canvas yet).
- [x] First EF migration (`InitialCreate`, 2026-10-28); Program.cs applies it on startup.
- [x] SMS provider layer, `Sms4FreeProvider` and per-couple allocations (live-tested 2026-10-28).
- [x] Couple language mode + per-language images/templates + guest-page toggle (SPEC.md §11).
- [x] "Delete all information" + audit log + site-admin TOTP are built.
- [x] Jewish yom tov days are in `SendingWindow` (computed from the Hebrew calendar). Other special days: the pause switch.
- [x] **Turnstile** (Cloudflare) on the public login pages and on the invitation's "seen" report (SPEC.md §13, Phase T). Keys `App__TurnstileSiteKey` / `App__TurnstileSecretKey`, both empty = off. Live check on the real domain still to do.
- [ ] (future, planned only, NOT built) **Hidden invitation content**: a plain fetch of `/{token}`, `/{token}/invitation` and `/{token}/event.ics` currently returns personal content to
  scanners and previews. Plan: SPEC.md section 14 and Phase H (neutral shell, content only after a Turnstile-verified reveal, path-scoped signed cookie, feature flag, real client IP
  first). Do not start without confirming the open decisions in 14.13.
- [x] Live SMS length/credit counter in the event-settings template boxes, driven by the active provider's cost rule. (WhatsApp share link is out of scope.)

## Commands

```bash
dotnet run --project src/WeddingRsvp      # public :8080, admin :8081/admin
dotnet test
dotnet ef migrations add <Name> -p src/WeddingRsvp   # needs: dotnet tool install --global dotnet-ef
docker compose up -d --build              # see README for NPM setup
```
