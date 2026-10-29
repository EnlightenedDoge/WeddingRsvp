# WeddingRsvp — Adversarial Security Review (2026-10-29)

Two-stage adversarial assessment against a **live isolated instance** of the app at commit `d45c4f9`
(throwaway git worktree + throwaway SQLite DB; the real project data was never touched).

- **Stage A — black box.** An attacker with no source access, three realistic roles: a malicious but
  legitimate couple-customer (couple 1), a guest who received one SMS link, and an unauthenticated
  outsider. Every finding backed by a live request/response.
- **Stage B — white box.** Full source audit cross-checked against the live instance, plus a
  point-by-point re-verification of the pre-existing `SECURITY-REPORT.md` (2026-10-28).

Severity: **Critical > High > Medium > Low > Info**.

---

## Remediation applied (2026-10-29)

All actionable findings below were fixed in this commit and verified (build clean, 271 tests pass):

- **Finding 1 — `X-Forwarded-For` trust:** forwarded headers are now honored only from a configurable trusted-proxy
  network list (`App:TrustedProxyNetworks`, default = loopback + RFC1918 + IPv6 ULA + **Tailscale `100.64.0.0/10`**).
  A direct connection from outside those ranges can no longer spoof its IP or scheme. Verified live: with the peer
  untrusted, a rotating `X-Forwarded-For` is ignored and the limiter still throttles (75/75); behind a trusted proxy
  (loopback/Tailscale), the forwarded IP is honored as before. `Program.cs:148-162`, `Options.cs`.
- **Finding 2 — login timing:** a constant-time dummy password hash now runs on the unknown-user branch. Verified
  live: known vs unknown email medians 38.7 ms vs 36.4 ms (was 35 vs 1 ms). `SignIn.cshtml.cs`.
- **Finding 3 — CSP:** a `Content-Security-Policy` is now sent (origins locked to the Turnstile/cdnjs/Fonts/OSM/Photon
  third parties; `object-src 'none'`, `frame-ancestors 'none'`, `base-uri`/`form-action 'self'`). Script/style keep
  `'unsafe-inline'`; a nonce migration is noted as follow-up. `Program.cs:228-253`.
- **Finding 4 — `TotalAllocated`:** now filtered by `CoupleId`. `CoupleDetail.cshtml.cs:90`.
- **Finding 5 — `StampTenant`:** the cross-tenant guard now also covers `EntityState.Deleted`. `AppDbContext.cs`.
- **Finding 6 — hardening:** ICS `Escape` now handles a lone `\r`; `ImportService` clamps `InvitedCount` to 0..50;
  the guest-page log sanitises/caps the user-agent; the container now defaults to the image's non-root user 1654
  (**one-time `chown 1654:1654 ./data` required on existing deployments**). `CalendarLinks.cs`, `ImportService.cs`,
  `Rsvp.cshtml.cs`, `Dockerfile`, `docker-compose.yml`, `.env.example`.

Follow-ups (deliberately not auto-applied): CSP script/style nonce migration; a breached-password check for the
site admin; the planned Phase H (hide `/{token}` content from scanners). The original report's findings below are
retained for the record.

---

## Bottom line

**The core authorization model is sound, and it is the part that matters most for this product.**
Tenant isolation between couples — the headline risk — held under every attack from both stages:
a couple session cannot read, edit, export, delete, invite, or otherwise touch another couple's
guests, messages, or settings (cross-tenant access is 404 / verified no-op in both directions).
Site-admin pages and every site-admin write handler are denied to couple sessions; the site admin
cannot authenticate or act on the public port; the port guard holds; output encoding, CSRF,
per-account lockout, one-time set-password links, server-side headcount bounds, token entropy,
the SMS allocation/breaker logic, and the delivery webhook are all correctly implemented.

**No Critical or confirmed-High application vulnerability was found.** The real weakness is in
**anti-automation / request-provenance** controls: the app trusts a client-supplied
`X-Forwarded-For` header unconditionally, which undermines the per-IP rate limiter, the HTTPS
guard, and the audit-log client IP on any directly-reachable port. Everything else is
low-severity hardening.

**The bundled `SECURITY-REPORT.md` is now largely out of date** (see the verification table): its
two HIGH application bugs do **not** reproduce, and most of its MEDIUMs are fixed. This review
supersedes it.

### Findings at a glance

| # | Finding | Severity | Status |
|---|---|---|---|
| 1 | `X-Forwarded-For` trusted unconditionally → rate-limit bypass, HTTPS-guard bypass, audit-IP spoofing | **Medium** (High if any port is directly reachable) | Confirmed live, both stages |
| 2 | Username enumeration via login timing side channel (~35 ms vs ~1 ms) | **Medium** | Confirmed live (Stage A) |
| 3 | No Content-Security-Policy header | Low | Confirmed |
| 4 | `CoupleDetail.TotalAllocated` sums allocations across all couples | Low | Confirmed (site-admin display bug) |
| 5 | `StampTenant` guard omits `Deleted` state | Low (latent, not reachable today) | Confirmed by code + live no-op |
| 6 | Hardening: length-only password policy, verbose UA/IP logging, root-by-default container, Import doesn't clamp `InvitedCount`, ICS `\r` escaping | Low | Confirmed |
| 7 | Unauthenticated `/{token}` serves personal content; cross-tenant POST returns 302 not 404; `Server: Kestrel` banner | Info | Known/benign |

---

## Findings

### 1. Client-supplied `X-Forwarded-For` is trusted unconditionally — **Medium** (confirmed, both stages independently)

**Where:** `Program.cs:148-156` configures `ForwardedHeaders` with `KnownProxies.Clear()` and
`KnownIPNetworks.Clear()` (`ForwardLimit = 1`). The sign-in rate limiter partitions on
`ctx.Connection.RemoteIpAddress` (`Program.cs:161-173`), which the middleware overwrites from the
header; `PortGuard.RequiresHttps` (`Program.cs:428-429`) trusts `ctx.Request.IsHttps`, derived from
`X-Forwarded-Proto`; the audit log and guest-page log record the forwarded IP (`Rsvp.cshtml.cs:80-81`).

**Root cause:** with both known-proxy lists cleared, the forwarded-headers middleware applies
`X-Forwarded-For` / `X-Forwarded-Proto` from *whatever connects directly*, with no proxy allow-list.
On a directly-reachable socket the attacker's own headers become the trusted client IP and scheme.

**Confirmed live (both stages, both ports):**
- A rotating fake `X-Forwarded-For` per request defeats the limiter entirely. Stage B
  (`stageB_ratelimit.py`): 14 credential POSTs with a **fixed** header → `[200×10, 404×4]` (throttled);
  14 with a **rotating** header → `[200×14]` (all pass). Stage A reproduced the same on both ports and
  showed the original no-XFF bucket stays throttled while each fresh spoofed IP gets a new bucket.
- `curl -H "X-Forwarded-For: 9.9.9.9" .../{token}` → the app logs the request "from 9.9.9.9", and the
  spoofed value is written verbatim to the `AuditLog.Ip` column on failed sign-ins.
- `X-Forwarded-Proto: https` satisfies the production HTTPS guard on the public port over plain HTTP.

**Impact:** the per-IP limiter is the control against password **spraying** (one common password across
many accounts) and username **enumeration** (finding 2). Rotating the header defeats it, leaving only
per-account Identity lockout (5 fails / 15 min, verified working) — which caps single-account brute
force but not spraying/enumeration across the whole couple roster. The site-admin login has no Turnstile
(admin-port by design), so on a reachable admin port this is the only throttle. The attacker also fully
controls the IP recorded in the security audit log (forensics poisoning / framing third-party IPs).

**Severity reconciliation (why Medium, not High):** this bites only on a **directly-reachable port**.
In the documented standard deployment — one container behind Nginx Proxy Manager — a correctly configured
NPM appends the real client IP as the right-most `X-Forwarded-For` entry and `ForwardLimit=1` takes that
entry, discarding the attacker's injected value. It becomes **High** in two supported/plausible
situations: (a) the LAN/VPN admin-port deployment (`App__AdminAllowPlainHttp=true`, admin port bound to a
LAN address, no proxy in front — an attacker on that network reaches Kestrel directly), and (b) any
misconfigured or bypassed proxy. Because the project explicitly supports the proxy-less LAN admin
deployment, treat the fix as high-priority regardless of the headline number.

**Fix:** set `KnownProxies` / `KnownIPNetworks` to the NPM hop so forwarded headers are honored only
from it; when the connecting socket is not a known proxy, derive both the limiter key and `IsHttps` from
the socket, not the header. Add a global non-IP-partitioned backstop limiter on `/signin`. SPEC §14 H1
already lists "real client IP behind the proxy" as a prerequisite — this is that work.

### 2. Username enumeration via login timing side channel — **Medium** (confirmed, Stage A)

**Where:** `SignIn.cshtml.cs:40-54`. The response body and status are correctly identical for a wrong
password whether or not the email exists (one generic message, ~2364 bytes) — but a registered email
runs a password-hash verification (~35 ms) while an unknown email returns almost immediately (~1 ms).

**Confirmed live:** 5-sample medians — known `alice@example.test` **35.4 ms** `[35.4, 36.0, 35.4, 36.9, 2.1]`
vs unknown `ghostN@example.test` **1.1 ms** `[1.2, 1.1, 1.5, 1.0, 1.0]`.

**Root cause:** the early-return path (`user is null`) in `SignIn.cshtml.cs:47-54` skips the hash
verification, so no constant-time dummy hash runs for unknown users.

**Impact:** an attacker reliably distinguishes registered couple emails from non-existent ones.
Medium on its own; combined with finding 1 (unthrottled requests) the full valid-email set can be
harvested, feeding targeted spraying/phishing.

**Fix:** perform a constant-time dummy `PasswordHasher.VerifyHashedPassword` against a throwaway hash on
the unknown-user branch so both paths do equal work.

### 3. No Content-Security-Policy header — **Low**

**Where:** `Program.cs:228-235` sets `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`,
`Referrer-Policy: same-origin`, but **no** `Content-Security-Policy` on any response (verified on the
guest page and authenticated admin pages). Output encoding is correct everywhere (see "Correctly
defended"), so there is no injection to exploit today — this is purely a missing second line of defence.

**Fix:** add a strict CSP (`default-src 'self'`; allow the Google Fonts origins; `object-src 'none'`;
`frame-ancestors 'none'`). The guest page uses an inline `style="--bg:…"` for couple colours, so use a
per-response style nonce or move the custom properties into a classed `<style>` block rather than
`'unsafe-inline'`.

### 4. `CoupleDetail.TotalAllocated` sums allocations across all couples — **Low** (site-admin display bug)

**Where:** `Pages/SiteAdmin/CoupleDetail.cshtml.cs:90`:
`db.SmsAllocations.IgnoreQueryFilters().SumAsync(e => e.Delta)` with no `.Where(e => e.CoupleId == id)`.
Every other allocation query in `AllocationService` is correctly scoped by `CoupleId`.

**Impact:** the "total allocated" figure on one couple's detail page shows the platform-wide net
allocation, not that couple's. Site-admin-only page; **no cross-tenant data reaches a couple**. Display
correctness only.

**Fix:** `.IgnoreQueryFilters().Where(e => e.CoupleId == id).SumAsync(e => e.Delta)`.

### 5. `StampTenant` cross-tenant write guard omits `Deleted` state — **Low** (latent, not reachable today)

**Where:** `Data/AppDbContext.cs:103-120` — the guard inspects `EntityState.Added` and
`EntityState.Modified` but not `EntityState.Deleted`.

**Confirmed NOT reachable today:** every couple-facing delete loads through the tenant-filtered `DbSet`
(`Edit.cshtml.cs:128` `db.Guests.FindAsync(id)` applies the query filter), so a cross-tenant row is never
loaded for removal. Proven live (`stageB_tenant.py`): couple 1 deleting couple 2's guest id 5 returns the
normal redirect but the guest remains in the DB.

**Impact:** none now; a latent hazard if a future path ever loads cross-tenant rows (`IgnoreQueryFilters`)
and `Remove()`s them within a couple scope.

**Fix:** extend the guard to `EntityState.Deleted`. Cheap defence-in-depth consistent with the invariant.

### 6. Low-severity hardening items

| Item | Where | Note |
|---|---|---|
| Length-only password policy (10 chars, no class/entropy) | `Program.cs:90-94` | Long passphrases are fine, but consider a breached-password check for site-admin. |
| Guest-page log records client IP + full `User-Agent` verbatim/unbounded | `Rsvp.cshtml.cs:80-81` | Log verbosity / log-injection surface; bound and sanitise. |
| Container runs as **root** by default | `Dockerfile`, compose `APP_UID:-0` | Set a non-root UID for the runtime image. |
| `ImportService` does not clamp `InvitedCount` (Edit clamps 1..50) | `ImportService.cs:64,93-98` | Dashboard sums could be inflated by a crafted CSV; no RSVP overflow (`(long)` cast guards it). |
| ICS lone-`\r` escaping in venue | `CalendarLinks` | Re-check against prior L7; low — ICS smuggling only for lenient parsers. |

### 7. Informational / known

- **Unauthenticated `/{token}` and `/{token}/event.ics` serve personal content** (guest name, couple,
  date, venue) to anyone holding the link — already documented as planned Phase H work. Tokens are 10-char
  CSPRNG from a 56-char unambiguous alphabet (~5.8e17 space), non-enumerable; pages are `noindex` + `no-store`.
- **Cross-tenant POST handlers return 302 instead of 404.** `POST /admin/guests/edit/{foreign id}?handler=Delete|SendInvite`
  returns a redirect rather than 404, but it is a verified **no-op** (DB unchanged, nothing queued) — the
  tenant filter misses and the handler falls through. Not even a reliable existence oracle. Optionally return
  404 for consistency.
- **`Server: Kestrel` banner** — minor fingerprinting; strip at the proxy.

---

## Verification of the prior `SECURITY-REPORT.md`

| Prior ID | Prior claim | Verdict at `d45c4f9` | Evidence |
|---|---|---|---|
| **H1** | Set-password links reusable for 7 days | **DISPUTED — not exploitable** | `ResetPasswordAsync` rotates the security stamp the reset token is bound to. Live: after reusing the link the password stays the first value; the second never takes effect. |
| **H2** | Stored XSS via guest name in Event `<script>` | **DISPUTED — not exploitable** | `System.Text.Json`'s default encoder escapes `<`,`>`,`&`; `</script><img onerror=…>` serializes to `</script>…`. Live: no breakout. |
| **H3** | Admin port published on `0.0.0.0` by default | **FIXED** | `docker-compose.yml` defaults `ADMIN_BIND_ADDRESS` to `127.0.0.1`; `docker-compose.npm.yml` uses `ports: !reset []`. |
| **H4** | Per-IP limits keyed on spoofable `X-Forwarded-For` | **STILL PRESENT** (re-rated Medium) | Finding 1. |
| **M1** | `TurnstileVerifyUrl` honored in production | **FIXED** | `Program.cs:28,32` nulls it outside Development. |
| **M2** | Webhook: path secret only; `to` optional; phone logged | **FIXED** | Now requires non-empty `to` + phone match (`DeliveryReports.cs:26`); constant-time secret compare; phone not logged. |
| **M3** | Integer overflow in RSVP headcount | **FIXED** | `(long)adults + children` (`RsvpService.cs:71`, `Rsvp.cshtml.cs:138`). |
| **M4** | Provider status text leaks to couple's Messages page | **FIXED** | Neutral couple-facing strings; raw provider text to log only (`MessageDispatcher.cs`). |
| **M5** | Test→fail→RequeueFailed loop hammers provider | **Largely mitigated** | Requeue excludes `Test`; dispatcher checks allocation. A couple can still re-click Test (portal is limiter-exempt) but each success debits allocation. |
| **M6** | Requeue double-sends (no dedupe vs queued) | **FIXED** | Dedupes `(Kind,GuestId)` against existing Queued rows (`CampaignService.cs:100-106`). |
| **M7** | CSV export formula injection | **FIXED** | Export uses `CsvReader.EscapeText` (prefixes `'` to `= + - @ \t \r`). |
| **M8** | World-readable secrets/PII on disk | Deploy-time; not re-testable here | Compose still `APP_UID:-0` (root) — see finding 6. |
| **L1/L2/L10/L18** | no CSP / length-only passwords / UA logging / root container | **STILL PRESENT** | Findings 3 and 6. |
| **L11** | Self password change doesn't rotate stamp | **DISPUTED** | `ChangePasswordAsync` rotates the stamp and calls `RefreshSignInAsync` (`Account.cshtml.cs`, `Security.cshtml.cs`). |

---

## Correctly defended (verified live and/or in source)

- **Tenant isolation (strong).** `couple_id` claim minted only from the signed cookie for `Role=Couple`
  (`AppClaimsFactory.cs:16-17`); scoped `TenantContext`, null by default, EF filter makes null match nothing
  (`AppDbContext.cs:60-63`); `StampTenant` throws on cross-tenant Added/Modified. Live: couple 1 cannot view,
  edit, export, delete, or queue-invite couple 2's guests (404 / no-op both directions).
- **"Open as couple"** re-issues the cookie only from the SiteAdmin-policy + admin-port-gated handler; a couple
  cannot reach it or forge the `acting_as_couple` / `couple_id` claims (cookie is data-protection-signed); the
  account page is blocked while acting.
- **Port guard & site-admin isolation.** `/admin/site/**` → 404 on the public port; site admin cannot sign in
  or get a cookie on the public port; `SiteAdmin` policy additionally requires the real admin socket.
- **Auth.** Per-account lockout (5/15 min, verified in DB); one-time set-password link is truly single-use (GET
  and POST replay both fail, original password preserved); Turnstile verified before any credential lookup,
  refusals not counted toward lockout; generic sign-in failure message; fresh auth ticket at login; cross-port
  cookie replay grants nothing.
- **Tokens.** 56-char unambiguous alphabet, 10 chars, CSPRNG, unique index with collision-retry.
- **Output & colours.** Guest name / couple names / venue / notes are Razor-auto-escaped on admin and guest
  pages (0 unescaped XSS occurrences live); the only `@Html.Raw` sinks go through `System.Text.Json`; couple
  colours pass strict `#RRGGBB` validation on save and at render.
- **CSRF.** Every state-changing POST requires `__RequestVerificationToken` (400 without); `wr.auth` is
  `HttpOnly; SameSite=Strict` (`Secure` over HTTPS); antiforgery cookie `SameSite=Strict`.
- **Headcount bounds.** Attending over invited, 0 adults, `Children`/legacy `Count` overflow all rejected;
  negative coerced to 1; 5000-digit field → 400.
- **No SQL injection.** No `FromSqlRaw` on user input; the only `ExecuteSqlRaw` is `VACUUM INTO {0}` with a
  server-built filename; `ExecuteDelete` calls are tenant-filtered.
- **SMS layer.** Allocation debited only on `Sent`, atomically with the Sent mark; no negative balance; single
  sequential per-couple worker; `AccountLevel` leaves the message Queued (attempts restored) and trips a breaker
  a couple cannot trigger; Sms4Free credentials in the JSON body only, recipient strictly `+9725`+digits, no SSRF.
- **Delivery webhook.** Constant-time secret compare (`CryptographicOperations.FixedTimeEquals`), requires `to`
  present + phone match, forward-only status transitions, always 200 (no id oracle); not guessable with a common
  wordlist; wrong secret does not mutate.
- **Error handling.** Malformed ids, null byte, bad routes, JSON body to the RSVP POST, oversized fields → clean
  400/404, no stack traces; no secrets/passwords in the app log.

---

## Recommended order of work

1. **Finding 1** — fix forwarded-headers trust (`KnownProxies`/`KnownIPNetworks`), re-derive the limiter key
   and `IsHttps` from the socket when the peer isn't a known proxy, add a non-IP backstop limiter on `/signin`.
   This is also the documented SPEC §14 H1 prerequisite and matters most for the proxy-less LAN admin deployment.
2. **Finding 2** — constant-time dummy hash on the unknown-user sign-in branch.
3. **Finding 4** — one-line `CoupleId` filter on `TotalAllocated`.
4. **Findings 3, 5, 6** — CSP header, `Deleted` state in `StampTenant`, non-root container, Import `InvitedCount`
   clamp, log hygiene — defence-in-depth, batchable.
5. Replace the stale `SECURITY-REPORT.md` with (or point it at) this review.

*Method note: two independent passes (black box with no source, white box with source) reached the same
conclusions — tenant isolation solid, `X-Forwarded-For` the one real weakness — which raises confidence in both.
All live tests ran against an isolated throwaway instance; the real project DB was never modified.*
