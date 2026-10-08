# WeddingRsvp — Project Spec & Checklist

Spec for Claude Code. Read together with `CLAUDE.md` (architecture, invariants, design-handoff contract).
Where the two disagree, this file is newer and wins.

---

## 1. Goal

A platform where each couple invites their wedding guests, collects RSVPs with headcounts, reminds people who
haven't answered, sees the results live, and thanks guests after the event. A **site admin** creates and manages
the couples' accounts; each couple signs in and only ever sees their own data (originally the project was built
for a single client couple; multi-tenancy was added later, see §10). Delivery channel is **SMS with a
personal link** to a mobile web page (WhatsApp was considered and rejected: template approval, Meta setup,
ban risk).

### Original requirements (from the client)

| # | Requirement | Status in scaffold |
|---|---|---|
| 0 | Input: list of names + phone numbers | Done: CSV import |
| 1 | Send the invitation (image) automatically | Done: SMS with link to page showing the image |
| 2 | Ask whether they're coming + how many | Done: RSVP page |
| 3 | Reminder to those who haven't answered | Done: planner, every N days, max M |
| 4 | Table/dashboard of confirmed names and "X confirmed out of Y invited" | Done: admin dashboard + CSV export |
| 5 | "תודה ששמחתם בשמחתנו" the day after the event | Done: planner, day after at a set hour |

Additions agreed with the developer: add-to-calendar (ICS + Google), Waze navigation link, per-invitation
headcount cap, manual overrides for phone-call answers, pause switch, sending window.

Added later (2026-10):

| # | Requirement | Status |
|---|---|---|
| 6 | **Site admin** page where the platform owner creates couples' accounts (names, date, optional venue, sign-in email, SMS quota, sender name) | Designed, not built (§10) |
| 7 | Couples **sign in** and manage only their own guests/messages/settings | Designed, not built (§10) |
| 8 | **Hebrew and English** UI (RTL/LTR) on every admin, site-admin and sign-in page | Designed, not built (§11) |
| 10 | Per-couple **SMS allocation** set by the site admin (the admin buys provider credit; customers never see the provider side); provider is swappable; provider outages/credit exhaustion and over-allocation handled gracefully | Designed, not built (§6, §10) |
| 11 | Each couple chooses a **language mode**: Hebrew only, English only, or both. SMS text and guest page follow it; the guest page has a he/en toggle (Hebrew default) when both are on; one invitation image per language | Designed, not built (§11, §4) |
| 12 | Couples can change their password and **delete all their information** from an account menu; no email change | Designed, not built (§10) |
| 9 | **No assumed couple data.** Names, date, venue, sender name are entered by the couple or by the site admin at creation. Nothing is sent while a field the message text uses is empty | **Built** and tested (§5) |

---

## 2. Users and flows

### Guest (on a phone, opened from an SMS)
1. Receives SMS: `{name}, שמחים להזמין אתכם לחתונה של {couple} ב-{date}! לפרטים ואישור הגעה: {link}`
2. Opens `https://rsvp.example.com/{token}` → sees greeting, invitation image, date, venue.
3. Chooses מגיעים / לא מגיעים; if attending, picks headcount 1..InvitedCount → submits.
4. Sees confirmation. Can reopen the link and change the answer until the event starts.
5. Can add the event to a calendar and open Waze.
6. If still pending after N days: reminder SMS (same link). Day after the event: thank-you SMS.

### Site admin (platform owner)
1. Signs in at `/signin`; sees the couples list with platform totals (couples, weddings in the next 30 days,
   messages this month, failed messages).
2. Creates a couple: partner names, wedding date, optional venue, sign-in email, how the couple gets access
   (emailed set-password link, or a one-time temporary password), SMS quota, sender name.
3. Can "open as couple" to help, reset a password, suspend an account.

### Couple (admin, phone or desktop, signs in with their own account)
1. Fill event details: names, date/time, venue, coordinates, invitation image, message texts, schedule.
   Anything the site admin left blank at creation is completed here. The page shows which `{placeholders}`
   are set or missing and a live preview of the invitation text.
2. Import guest list CSV; fix flagged numbers.
3. Send a test SMS to themselves; then send invitations (button) or set an automatic send time.
   The send button stays disabled, with a link to event details, while a needed field is missing.
4. Watch the dashboard; record answers received by phone; resend to fixed numbers.
5. Pause sending on holidays or if something looks wrong. Export the final list for the venue.

---

## 3. Stack and deployment (summary)

- ASP.NET Core 8 Razor Pages, EF Core 8 + SQLite, single container.
- Ports: **8080 public**, **8081 admin**. `/admin*` returns 404 on the public port.
- Auth (planned, §10): cookie sessions for site admin and couples. The NPM Access List on `admin.*` can stay as a
  second layer but is no longer the only protection.
- Dev machine: only the .NET 10 runtime is installed; run tests with `DOTNET_ROLL_FORWARD=Major`.
- Nginx Proxy Manager (cert already exists for example.com):
  - `rsvp.example.com` → `http://wedding-rsvp:8080`
  - `admin.example.com` → `http://wedding-rsvp:8081` + Access List (basic auth)
- Data in `/data` volume: `rsvp.db`, `media/invitation.*`.
- Background worker every 20 s: plan (queue due messages) → dispatch (send queued messages).

---

## 4. Data model (summary)

- **Couple** (tenant, planned): `Id`, `Name`, `Status` (Active/Suspended), `SmsQuota`, `SmsSent`, `CreatedAtUtc`.
- **User** (planned): `Email`, `PasswordHash`, `Role` (SiteAdmin/Couple), `CoupleId` (null for site admins),
  `MustSetPassword`, set-password token + expiry, `PreferredLanguage` (he/en).
- Planned: `Guest`, `MessageLog` and `EventSettings` each get `CoupleId`; `EventSettings` is one row per couple.
- Planned language fields on `EventSettings` (the **couple** chooses; it is not per guest):
  `LanguageMode` (`HebrewOnly` | `EnglishOnly` | `Both`), `DefaultPageLanguage` (he unless the couple changes it;
  only meaningful for `Both`), invitation image per language (`InvitationImageHeFile`, `InvitationImageEnFile`),
  and invite/reminder/thank-you templates per language (`InviteTemplateHe`/`InviteTemplateEn`, …).
- Planned guest-page look on `EventSettings`: `PageBackgroundColor` (`#RRGGBB`, validated), `PageBackgroundImageFile`
  (optional), `PageAccentColor` (`#RRGGBB`). The page picks light or dark text from the background's luminance.
- Planned **SmsAllocation ledger** (append-only): `CoupleId`, `Delta` (+ grant by site admin, − usage or admin
  correction), `Reason`, `CreatedAtUtc`, `ByUserId`. The unit is the **SMS segment** (what the provider bills; the
  exact provider credit formula is unverified, §6). A couple's remaining allocation is the sum; the dispatcher
  reserves the segments before sending and returns them if the send provably failed.
- **Guest**: one invitation = one phone. `Name`, `PhoneRaw`, `PhoneE164` (null if invalid), `PhoneError`,
  `InvitedCount`, `Token` (10 chars), `Status` (Pending/Attending/Declined), `AttendingCount`,
  `Source` (Web/Manual), timestamps for invite/reminders/thank-you/first & last open, `Notes`.
- **MessageLog**: outbox + history. `Kind` (Invite/Reminder/ThankYou/Test), `Status`
  (Queued/Sent/Delivered/Failed/Cancelled), `Body`, `Attempts`, `NotBeforeUtc`, `ProviderMessageId`, `Error`.
- **EventSettings**: single row today (one per couple once §10 lands). Event details, image, sender name,
  3 templates, auto-invite time, reminder interval/max, thank-you hour/audience, `SendingPaused`.
  `CoupleNames` and `Title` default to empty; `StartsAtLocal` and `EndsAtLocal` are nullable. Nothing is prefilled.


---

## 5. Messaging rules

- Sending window (Israel time): Sun–Thu 09:00–21:00, Fri 09:00–13:00, never Saturday. Holidays: pause switch.
- Throttle: 20 messages per 20 s tick (configurable).
- One queued message per guest per kind, at most. Planners are idempotent.
- **Missing fields block sending.** Templates use `{name} {couple} {link} {date} {venue}`. `{couple}`, `{date}`
  and `{venue}` come from event details; `{name}` from the guest list; `{link}` is generated per guest. If a
  template uses a placeholder with no value, `CampaignService` queues nothing of that kind: invitations (bulk and
  single), reminders and thank-yous. No start date at all means the planner does nothing. `{venue}` is required
  only if a template actually uses it. Test SMS are exempt. Built: `MessageTemplates.MissingFields`.
- Per-couple SMS allocation (planned): granted by the site admin. The dispatcher refuses to send for a couple
  whose allocation is used up; messages stay `Queued` (not `Failed`) and the couple's Messages page and the
  site admin's list show "allocation used up". The couple sees their own remaining allocation, nothing about the provider.
- **Over-allocation is allowed and handled.** The admin buys provider credit and grants allocations; nothing stops them
  granting more than the provider balance currently holds. The site-admin page shows *allocated-but-unsent total vs.
  provider balance* and warns (does not block) when a grant would exceed it. If the provider runs dry anyway, the
  circuit breaker (§6) holds everything Queued and sends in order once credit returns. Couples only see a neutral
  "messages are delayed and will go out automatically".
- **Language mode (planned, per couple):** `HebrewOnly`, `EnglishOnly` or `Both`.
  - SMS: `Both` sends **one SMS per guest containing the Hebrew text then the English text** (longer, so more
    segments and more allocation per guest: show the segment count and total cost before sending). The single-language
    modes send only that language's template.
  - Guest page: `Both` shows a he/en toggle; Hebrew is the default unless the couple sets `DefaultPageLanguage` to
    English. Single-language modes show no toggle and force that language.
  - Missing-fields rule applies to every template the mode uses. Invitation image: one per language; a language with
    no image falls back to the other language's image. English-only SMS are GSM-7 (160/153 chars per segment); any
    Hebrew makes the whole SMS UCS-2 (70/67), so `SmsSegments` must count by encoding.
- Reminders: only `Pending` guests whose invite was sent, every `ReminderIntervalDays`, max `MaxReminders`,
  never after the event starts. Re-checked at dispatch time.
- Thank-you: day after event start, from `ThankYouHourLocal`, valid for 48 h (no late sends after downtime).
  Audience: attending only (default) or everyone invited.
- Retries: only when the request provably never reached the provider (see §6); everything else goes to
  `Failed` for the admin to review and requeue. Never risk a double send.
- Hebrew forces UCS-2: **70 chars for one segment, 67 per segment when split**. The link counts.
  Default templates are 2 segments; the admin page shows the count per template.

---

## 6. SMS provider

### Decision: SMS4FREE (sms4free.co.il)

Chosen over 019 SMS / TextMe because their smallest packages (1,000–1,500 messages) are far more than a
single wedding needs. SMS4FREE sells small prepaid packs that never expire, has an HTTP API, and gives 10 free
messages on signup for testing.

Prices (before VAT, from the price page): 100 = 17 ₪ · 200 = 30 ₪ · 500 = 35 ₪ · 1,000 = 40 ₪.

**Sizing example** (assuming 1 credit per message, see "Credits" below): 150 invitations →
150 invites + reminders to the ~1/3 who don't answer (2 × 50) + thank-you to ~120 attending ≈ 370 credits →
the 500 pack.

### What SMS4FREE does and doesn't give us

| Capability | Status | Consequence for the code |
|---|---|---|
| Send via HTTP JSON, credentials in the body (`user` = registered mobile, `pass`, `key`) | Yes | Implement `HttpSmsSender` |
| Per-message provider id in the response | **No** — the API returns an accepted count | Send **one recipient per request** (already the design); a positive count = accepted. `ProviderMessageId` stays null |
| Delivery reports (DLR webhooks) | Advertised as a premium feature; how a report identifies a message is unknown | **Ask support** before building anything. Until confirmed, no `Delivered` status; messages end at `Sent` |
| Test endpoint | No | Dev uses `Sms:Provider=Log`; live tests use the 10 free messages |
| Custom sender name | Only after buying a package; free messages go out from the registered phone number | Buy the pack before go-live; set the Latin sender name (e.g. `DanaYossi`) |
| Balance query | Yes (`/ApiSMS/AvailableSMS`) | Show remaining credits on `/admin/messages` |

The provider may ask for ID verification after a purchase (Ministry of Communications requirement).

### Integration spec (`HttpSmsSender`)

- Endpoint: `POST https://api.sms4free.co.il/ApiSMS/v2/SendSMS`, JSON, UTF-8, Hebrew not escaped.
- Body: `key`, `user`, `pass`, `sender`, recipient, message. **Confirm the exact recipient/message field names on
  the account's API page** after signup (community clients exist for PHP and Node; no official field reference
  was found publicly).
- Recipient format: national `05XXXXXXXX` (convert from our E.164). International numbers: check with support;
  until then treat non-Israeli numbers as "send manually" in the admin.
- Response: positive `status` = number of accepted recipients → `SmsSendResult.Ok(null)`.
  Zero/negative = refused → `Fail("<status>: <message>")`.
- Account-level refusals (bad credentials, no balance, unverified sender) affect every message: set
  `EventSettings.SendingPaused = true`, log an error, show it on `/admin/messages`. Map the codes from the API page.
- **Retries — important change:** a timeout or broken connection doesn't tell us whether the provider sent the
  message, and an automatic retry can send and bill twice. So:
  - Auto-retry **only** when the request provably never reached the provider (DNS failure, connection refused).
  - Timeouts, non-2xx responses and unreadable bodies → `Failed` with error "תוצאה לא ידועה — בדקו ביומן הספק",
    no automatic retry. The admin can requeue after checking the provider's sent log.
  - Explicit provider refusals → `Failed`, no retry.
- **Credits:** a community client estimates 1 credit for the first 134 characters and +1 per 67 after that.
  That's unofficial — verify by sending a real invite to yourself and diffing the balance. Our `SmsSegments`
  shows network segments, which may differ from billed credits.
- Pre-flight on "send invitations": fetch the balance; if it's lower than (guests to invite × credits per
  invite), show a warning and require confirmation.

### Provider abstraction and provider-side failure (required, provider-independent)

**A. Interchangeable providers.** The dispatcher talks only to a provider-neutral interface; SMS4FREE is one
implementation. Provider choice and credentials are configuration, not code.
- `ISmsProvider`: `Name`, `SendAsync(SmsMessage)` → `SmsSendResult`, optional `GetBalanceAsync()`,
  optional `ParseDeliveryReport(...)`. `ISmsSender` today becomes this. A registry resolves the active provider
  from `Sms:Provider` (`Log`, `Sms4Free`, future ones). Adding a provider = one class + one config value.
- Provider-specific details (field names, phone format like `05…`, status codes) stay inside the provider class.
- Optional later: per-couple provider override and automatic failover to a secondary provider.

**B. Provider-side failure must not lose or double-send messages.** `SmsSendResult` carries a failure *kind*,
set by the provider class (which maps its own status codes):
| Kind | Meaning | Dispatcher action |
|---|---|---|
| `Sent` | accepted | mark Sent, ledger −1 |
| `Transient` | provably never reached the provider (DNS, connection refused) | retry with backoff |
| `Rejected` | provider refused this message (bad number, content) | mark Failed, no retry |
| `Unknown` | timeout / unreadable reply: may have been sent | mark Failed ("check provider log"), **never auto-retry** |
| `AccountLevel` | out of credit, bad credentials, unverified sender, account suspended | **circuit breaker**: stop sending *for everyone*, leave the message `Queued` and the attempt count untouched |

Circuit breaker: a platform-wide "provider unavailable" state with reason and time, shown as a banner to the site
admin; couples see only a neutral "messages are delayed and will go out automatically", never provider details. It auto-probes (balance call or a cautious single send)
and resumes when the provider answers again, or the site admin resumes it manually. Nothing is lost: queued
messages simply wait. The provider's own balance is shown on the site-admin page; couples' allocations are tracked
separately in our ledger, so the admin can grant more allocation than the provider balance holds (§5); the breaker
is what makes that safe.
Tests: a fake provider returning each failure kind; assert queue state, attempts, ledger, breaker.

### Delivery-report endpoint

Keep `/webhooks/sms-delivery/{secret}` disabled (empty secret) until support confirms DLR availability and
payload format. If DLRs exist but can't identify individual messages, drop the endpoint.

## 7. Design handoff

The admin, site-admin, sign-in and **guest portal** designs exist in Claude Design:
https://claude.ai/artifact/6cvjtSuF3xYHfSrzLV3Wyi (artboard → Razor page map and the phone-first rules in `CLAUDE.md`).
The guest page is designed phone-first around the couple's uploaded invitation image. The couple controls its look
(background colour or photo, button colour) from Event details; text contrast adapts automatically.
Integration rules are in `CLAUDE.md` → "Design handoff".
Short version: replace layouts, page markup and CSS; keep routes, form field names (`Attending`, `Count`, …),
page-handler names, and antiforgery; Hebrew RTL; mobile-first for the guest portal. Don't move logic into views.

Guest page states to cover: first visit · already answered (editable) · just saved (attending / declined) ·
validation error · RSVP closed · unknown link.

---

## 8. Checklist

### Phase 0 — Make the scaffold real
- [x] `dotnet build` the solution (0 errors)
- [x] `dotnet test`; all green (38 tests, with `DOTNET_ROLL_FORWARD=Major`)
- [x] Missing-fields rule enforced in `CampaignService` + Messages page + guest edit, with unit and SQLite tests
- [x] Remove assumed couple data from `EventSettings` (empty defaults, nullable dates)
- [ ] First EF migration — **after** Phase M's schema change (needs `dotnet-ef` installed); delete `src/WeddingRsvp/data/rsvp.db` once
- [ ] Run locally; confirm `/admin` is 404 on :8080 and works on :8081
- [ ] Import `samples/guests-sample.csv`: 4 added, 1 flagged (landline)
- [ ] Walk the full flow with `Sms:Provider=Log`: invite → open link → answer → change answer → reminders skip responders
- [ ] Verify sending window and pause switch actually block dispatch (unit test with a fake `TimeProvider`)
- [ ] Add integration tests for `CampaignService` planners (idempotency, reminder timing, thank-you window) using SQLite in-memory
      (started: `CampaignMissingFieldsTests` covers the missing-fields rule only)

### Phase M — Multi-tenancy and auth (new; do before the migration and design integration)
- [ ] `Couple` and `User` entities; `CoupleId` on `Guest`, `MessageLog`, `EventSettings` (one settings row per couple)
- [ ] `ITenantContext` + EF global query filter on `CoupleId`; tests proving couple A cannot read/write couple B's data
- [ ] Public flow resolves the tenant from the guest token; `/event.ics` and the invitation image become per-guest/per-couple URLs
- [ ] `MessagingWorker` runs per couple; per-couple pause, window and SMS quota
- [ ] ASP.NET Core Identity (cookie auth): `/signin`, sign-out, lockout, set-password / reset via single-use expiring
      links the **site admin copies and passes on** (no email provider), change-password for couples, no email change,
      suspended couples cannot sign in
- [ ] Hardening: Secure/HttpOnly/SameSite cookies, short idle timeout for site admin, rate-limited sign-in,
      TOTP two-factor for site admin (recommended; optional for couples), audit log (sign-ins, open-as-couple,
      password resets, allocation grants, deletions), security headers, backups encrypted
- [ ] SMS allocations: ledger, site admin grants, dispatcher enforcement, couple-facing balance
- [ ] "Delete all information" in the couple's account menu: offer CSV export first, typed confirmation, wipes guests,
      messages, settings, images (and the account); keeps only a minimal tombstone (couple id, date) for accounting;
      site admin can also purge
- [ ] Bootstrap first site admin from config on an empty database
- [ ] Site admin pages: couples list, create couple, reset password, suspend, "open as couple"
- [ ] Data migration: existing single-couple rows → one `Couple`
- [ ] Then the first EF migration

### Phase L — Bilingual UI
- [ ] Request localization for `he` and `en`; `dir`/`lang` in layouts; HE/EN switch persisted (cookie, then user preference)
- [ ] Move UI strings to resources; no Hebrew-only literals in new pages
- [ ] Couple language mode (`HebrewOnly`/`EnglishOnly`/`Both`) + `DefaultPageLanguage`; per-language templates and
      invitation images; he/en toggle on the guest page when `Both` (Hebrew default); bilingual single-SMS composition
      and its segment/allocation cost preview; GSM-7 vs UCS-2 segment counting

### Phase 1 — SMS provider layer + SMS4FREE
- [ ] Replace `ISmsSender` with the provider-neutral `ISmsProvider` + registry (see §6 A/B)
- [ ] `SmsSendResult` failure kinds; dispatcher handling per kind; platform circuit breaker + site-admin banner
- [ ] Fake provider and tests for every failure kind
- [ ] Open an account (whose? see open questions); note `user`, `pass`, `key` from the API page
- [ ] Ask support: (1) exact send field names and status/error codes, (2) are DLR webhooks available and how do
      they identify a message, (3) international recipients
- [ ] Implement `HttpSmsSender` per §6 (E.164 → `05…`, one recipient per request, status parsing)
- [ ] Change dispatcher retry policy per §6: auto-retry only on provable non-delivery to the provider
- [ ] Auto-pause on account-level refusals; surface it on `/admin/messages`
- [ ] Balance on `/admin/messages` + pre-flight warning before "send invitations"
- [ ] Unit tests: phone conversion, response parsing, retry classification
- [ ] Live test with the free messages (sender = registered number); confirm credits per Hebrew invite via balance diff
- [ ] Decide on the DLR endpoint per support's answer (implement or remove)

### Phase 2 — Design integration
- [ ] Apply the guest portal design (done in Claude Design) to `_PublicLayout`, `Rsvp`, `Index`, `NotFound`, `public.css`;
      add the couple-chosen page look to `EventSettings` and the Event page; check on real iPhone Safari + Android Chrome, 320px width, landscape
- [ ] Apply the existing admin / site-admin / sign-in designs to `_AdminLayout`, `Pages/Admin/**`, new site-admin and sign-in pages, `admin.css`
- [ ] Check every guest-page state on a real phone (iOS Safari + Android Chrome), RTL, and in dark mode if designed
- [ ] `/event.ics` opens in iOS Calendar; Google link prefills correctly; Waze opens to the venue
- [ ] Keyboard focus visible, form usable at 320 px width

### Phase 3 — Deploy
- [ ] `.env` from `.env.example` (SMS4FREE `user`/`pass`/`key`, `NPM_NETWORK`, bootstrap site-admin email/password)
- [ ] `docker compose up -d --build`; `/healthz` OK through NPM
- [ ] NPM: `rsvp.example.com` → 8080; `admin.example.com` → 8081 with Access List; SSL on both
- [ ] Confirm `https://rsvp.example.com/admin` → 404 and admin requires sign-in
- [ ] Register the DLR URL with SMS4FREE (only if Phase 1 confirmed DLRs)
- [ ] Back up `./data` (cron copy of `rsvp.db` + `media/`)

### Phase 4 — Go-live with the client
- [ ] Client fills event details, uploads the invitation image, edits texts (check segment counts)
- [ ] Buy the package; set the sender name (Latin letters); complete ID verification if asked
- [ ] Import the real list; resolve every flagged number and duplicate
- [ ] Test SMS to the couple and the developer; open the link on both phones
- [ ] Check balance covers the run; send invites (or set the auto time) on a weekday morning; watch the log
- [ ] After the first wave: review unreachable/failed list with the couple

### Phase 5 — After the event
- [ ] Thank-you messages sent the day after; check the log
- [ ] Export the final list for the client
- [ ] Agree on data retention; delete guest data when the client confirms

---

## 9. Open questions for the client

1. Does the list include how many people each invitation covers? If not, who fills it in?
2. Sender name to show (Latin letters only, max 11 characters)?
3. Invitation date/time for the send-out, reminder cadence (default: every 4 days, max 2)?
4. Thank-you to attendees only, or to everyone invited?
5. Any guests abroad (international SMS may not be supported)?
6. RSVP deadline earlier than the event itself (e.g. one week before, for the caterer)?
7. Whose SMS4FREE account: the couple's (their ID verification, their payment) or yours, billed on?
   With multiple couples this becomes: one platform account shared by all couples, with a per-couple SMS quota
   (planned). Per-couple provider credentials are the alternative (§12).

---

## 10. Multi-tenancy and auth (planned)

Proposed design. Decisions still open are in §12.

- **Tenant = Couple.** Everything a couple owns (guests, messages, settings, invitation image) carries `CoupleId`.
- **Isolation by global query filter**, fed by a scoped `ITenantContext` built from the signed-in user's claims.
  Pages and services never filter by couple by hand. Anything that must cross tenants (the worker, resolving a
  guest token, the site admin's totals) uses `IgnoreQueryFilters` explicitly, in one small, tested place.
- **Public routes:** the tenant comes from the guest token. Tokens stay globally unique. Routes that had no guest
  context (`/event.ics`, `/media/invitation`) become per-guest or per-couple URLs.
- **Worker:** each tick iterates active couples; planning and dispatch run inside that couple's tenant scope.
  Suspended couples send nothing. Pause switch, window and SMS quota are per couple.
- **Roles:** `SiteAdmin` (no `CoupleId`) and `Couple`. Site admin pages are under `/admin/site/**`.
  "Open as couple" starts a clearly marked session scoped to that couple (banner + audit log); it never
  disables the filter.
- **Sign-in:** one `/signin` page for both roles; cookie session on **ASP.NET Core Identity** (decision, §12):
  battle-tested password hashing, lockout, security stamps and single-use expiring tokens, rather than hand-rolled
  code, because the data is very sensitive. Site admin gets TOTP two-factor; couples can opt in.
- **Account creation (site admin):** no email is sent. The site admin sees a one-time set-password link and passes it
  on themselves. Couples can change their password but **not** their sign-in email.
- **Delete all information:** an account menu item for couples (typed confirmation, CSV export offered first).
- **Creating a couple (site admin):** partner names, wedding date, optional venue, sign-in email, SMS allocation,
  sender name. Anything left blank is completed by the couple in event details before sending (§5 rule).
  The design also offers a temporary-password option; per decision §12 the default and only required path is the link.
- **Security baseline (sensitive data):** HTTPS only (HSTS), Secure/HttpOnly/SameSite=Lax auth cookies, antiforgery on
  every POST, sign-in rate limiting + lockout, audit log, no PII in application logs, database file and backups
  readable only by the app user (consider at-rest encryption of the SQLite file / backups), data deletion on request.
- **Bootstrap:** first site admin from `App__BootstrapAdminEmail` / `App__BootstrapAdminPassword` when no users exist.
- **Migration of existing data:** one `Couple` created for the current single event; rows backfilled.

## 11. Bilingual UI (Hebrew + English)

- Every admin, site-admin and sign-in page supports Hebrew (RTL) and English (LTR); designed in the canvas.
- Request localization for `he` and `en`; `lang` and `dir` set in the layout; HE/EN switch in the sidebar,
  persisted in a cookie (and as `PreferredLanguage` on the user once accounts exist).
- UI strings live in resources, not inline literals. Layouts use logical CSS properties (`margin-inline`,
  `text-align: start`) so they flip with `dir`. Phone numbers, emails, links and message placeholders stay `dir="ltr"`.
- **Guest side (decision):** the *couple* picks a language mode: Hebrew only, English only, or both.
  - `Both`: the SMS contains both texts; the link opens a page with a he/en toggle, **Hebrew by default** unless the
    couple makes English the default. The toggle changes page language, direction and invitation image.
  - Single-language modes: SMS and page are that language only, no toggle.
- Couples upload **two invitation images**, one per language, and write templates per language (only the languages
  their mode uses are required).

## 12. Decisions

Resolved 2026-10-08:
1. **Set-password delivery:** a one-time link the site admin copies and passes on themselves. No email provider.
2. **Auth stack:** ASP.NET Core Identity (security over minimal code, since the data is very sensitive), plus lockout,
   audit log, site-admin TOTP and the baseline in §10. (Recommendation accepted by the owner's "security should be good".)
3. **SMS:** the site admin grants each couple an allocation (ledger, §4). The send layer is provider-interchangeable
   and survives provider-side credit exhaustion or API breakage via failure kinds + a circuit breaker (§6).
4. **Language:** per couple, not per guest: Hebrew only, English only, or both. In both modes the SMS holds both texts
   and the link opens a he/en toggle with Hebrew as default (couple can change the default). Two invitation images and
   per-language templates (§4, §11).
5. **Self-service:** couples can change their password, cannot change their email, and can delete all their
   information from an account menu.

Also resolved (2026-10-08):
- "Delete all information" **deletes the account** too; only a minimal tombstone (couple id, date) remains for accounting.
- Allocation corrections: the site admin may reduce a couple's allocation to fix a mistake (a negative ledger entry);
  it can never go below what the couple already used. No expiry.
- The admin handles buying provider credit; customers never see it. Over-allocation relative to the provider balance
  is allowed and handled gracefully (§5, §6).

Still open:
- How allocations map to money (business question, not code).
- Bilingual single SMS is long (Hebrew forces UCS-2). Alternative: send two SMS per guest. Current choice is one SMS;
  revisit after seeing real segment counts and cost.
