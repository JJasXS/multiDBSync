# multiDBSync (SqlAccountSync) — Security Audit Report

**Repository:** `JJasXS/multiDBSync`  
**Branch reviewed:** `master` (includes prior OTP hardening) + follow-up on `sdk-conversion`  
**Date:** 2026-11-03 (updated)  
**Scope:** ASP.NET Core Razor Pages, Firebird, SQL Account SDK COM, AWS Secrets Manager, OTP auth, background workers

## Executive summary

Initial Critical/High OTP and artifact issues were remixed in PR #14. This follow-up closes remaining audit gaps against current master: **public debug-OTP disclosure**, **HTTPS-only cookies in Production**, **trusted reverse-proxy configuration**, **HMAC OTP hashing + distributed rate-limit counters**, **AllowedHosts hardening**, and the **verified MimeKit transitive `System.Security.Cryptography.Pkcs` advisory** (pinned to **8.0.2**).

| Metric | Result |
|--------|--------|
| OTP / host hardening regression tests | **13 passed** |
| `dotnet list package --vulnerable --include-transitive` | **No vulnerable packages** (after Pkcs 8.0.2 pin) |

SDK synchronization paths (`SdkWriteAdapter` / `SyncWorker` / Firebird reads) were not redesigned.

---

## Findings table

| ID | Severity | File / Line | Vulnerability | Evidence | Impact | Fix Applied | Verification |
|----|----------|-------------|---------------|----------|--------|-------------|--------------|
| SEC-001 | Critical | OTP auth stack | Development OTP auth bypass | Fixed mock OTP / debug return | Auth bypass | Random OTP; mock ignored | Tests |
| SEC-002 | High | `OtpAuthService` | Brute-force / reuse / enumeration | Unlimited verifies | Takeover / enum | Fail lockout, atomic consume, generic send | Tests |
| SEC-003 | High | `.env.example` | Default Firebird password | `masterkey` example | Weak defaults | Placeholder password | Review |
| SEC-004 | Medium | `/health` | Public queue/SDK disclosure | Anonymous rich JSON | Recon | Minimal `/health`; Admin `/health/detail` | Review |
| SEC-005 | Medium | `AccountsController` | Error detail leakage | Raw write messages | Info leak | Generic + `requestId` | Review |
| SEC-006 | High | `_buildcheck`, logs | Tracked build artifacts | ~3970 binaries in Git | Noise / leak risk | Removed from tracking + gitignore | Review |
| SEC-007 | Medium | `DatabaseFileBrowser` | Full-drive FDB search | All fixed drives | Disclosure / DoS | Approved roots only | Review |
| SEC-008 | Low→Fixed | Cookie auth | Non-HTTPS cookies in prod | `SameAsRequest` only | Cookie theft on HTTP | **Production `CookieSecurePolicy.Always`** | Code + review |
| SEC-009 | Low | Security headers | Missing XFO/nosniff | — | Clickjacking | Headers added | Review |
| SEC-010 | Info | Bootstrap email | Personal email in repo | — | Privacy | `admin@localhost` | Review |
| SEC-011 | Medium (accepted) | Role policies | Manager/Operator = Admin | Policy collapse | Coarse RBAC | Documented; no redesign | Review |
| SEC-012 | Info→Fixed | AllowedHosts / proxies | `*` hosts; unconfigured proxies | Broad trust | Host-header / IP spoof | **Local-only default AllowedHosts; Production rejects `*`/empty; explicit KnownProxies/Networks** | Tests |
| SEC-013 | Low | Dynamic SQL identifiers | Allowlisted tables | Injection if bypassed | Data integrity | Existing SqlGuard / params | Review |
| SEC-014 | Info | Stack note | SDK not REST | — | — | Documented | — |
| SEC-015 | High→Fixed | Debug OTP on public hosts | `Development+dev` returned OTP to any client | Misconfigured public Dev host | OTP disclosure | **Require `AllowLocalOtpReveal` + loopback; Login strips non-loopback** | Tests |
| SEC-016 | Medium→Fixed | Plain SHA-256 OTP | 6-digit space offline-guessable if cache dumped | Weak hash | Offline OTP recovery | **HMAC-SHA256 with pepper (`OtpHashPepper`)** | Tests |
| SEC-017 | Medium→Fixed | Process-local rate limits | Multi-instance bypass | Soft limit gap | Brute-force across nodes | **`IDistributedCache` counters + local locks** (Redis-ready) | Code |
| SEC-018 | High→Fixed | NuGet GHSA-846f-f764-76mx | MimeKit → Pkcs 8.0.1 | DoS via SignedCms | Pin **System.Security.Cryptography.Pkcs 8.0.2** | `dotnet list --vulnerable` clean |

---

## Follow-up changes (this iteration)

- `Services/Auth/OtpAuthService.cs` — reveal gate, HMAC, distributed rate limits  
- `Services/Auth/OtpHashPepper.cs` — shared/process pepper  
- `Helpers/HostHardening.cs`, `Helpers/ForwardedHeadersSetup.cs`  
- `Program.cs` — Secure cookie Always in Production; forwarded headers DI; AllowedHosts check  
- `appsettings.json` / `Production` / `.env.example`  
- `SqlAccountSync.csproj` — Pkcs 8.0.2 override  
- `tests/OtpAuthServiceTests.cs` — 13 tests  
- `docs/CREDENTIAL_ROTATION.md` — pepper / AllowedHosts notes  

---

## Manual deployment actions (no automatic credential changes)

1. Set **`AllowedHosts`** env to the public DNS name(s) in Production (startup fails on `*` / empty).  
2. Terminate TLS (or set `X-Forwarded-Proto` from a **listed** `ForwardedHeaders:KnownProxies` / `KnownNetworks` entry) so Secure cookies work.  
3. Prefer **`Sync:OtpHashPepperSecretRef`** (`env:` / `sm:`) for multi-instance OTP HMAC.  
4. Never set `Sync:AllowLocalOtpReveal=true` on publicly reachable hosts.  
5. Keep strong Firebird / AWS / SMTP secrets per `docs/CREDENTIAL_ROTATION.md`.  
6. Optional: register Redis as `IDistributedCache` for cross-node OTP rate limits.

---

## Remaining residual risks

- Role policies still collapse Manager/Operator to Admin (product decision).  
- OTP challenge objects remain in process `IMemoryCache` (sticky sessions or single instance recommended for verify; rate limits are distributed-ready).  
- Git history may still contain old `_buildcheck` blobs — rotate only if secrets were ever embedded; do not rewrite history without agreement.  
- Pre-existing unrelated unit test failures (GL mapper / item link) may still exist outside this audit scope.
