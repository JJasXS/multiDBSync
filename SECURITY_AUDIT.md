# multiDBSync (SqlAccountSync) — Security Audit Report

**Repository:** `JJasXS/multiDBSync`  
**Date:** 2026-11-03  
**Scope:** Full application tree (ASP.NET Core Razor Pages, Firebird, SQL Account SDK COM, AWS Secrets Manager, OTP auth, background workers)

## Executive summary

Critical OTP bypass paths (fixed mock code `123456`, debug OTP return, console OTP logging) were removed or strictly gated to local `Development` + `APP_MODE=dev`. OTP brute-force and enumeration protections were added. Public `/health` no longer exposes queue/SDK internals. Example Firebird credentials no longer advertise `masterkey`. ~3,968 tracked `_buildcheck` build artifacts plus diagnostic `_run.log` / `_api500.html` were removed from Git tracking.

| Metric | Result |
|--------|--------|
| OTP security regression tests | **7 passed** |
| Full `dotnet test` | **98 passed**, 2 failed (pre-existing, unrelated to this audit) |
| `dotnet list package --vulnerable` | See Dependency section |

---

## Findings table

| ID | Severity | File / Line | Vulnerability | Evidence | Impact | Fix Applied | Verification |
|----|----------|-------------|---------------|----------|--------|-------------|--------------|
| SEC-001 | Critical | `appsettings.Development.json`, `OtpAuthService.cs`, `Login.cshtml.cs`, `Program.cs` | Development OTP auth bypass | Committed `MockSignInCode=123456`, `DebugOtpToConsole=true`; OTP returned to client; console write of OTP; `APP_MODE=dev` forced debug flag even outside Development | Anyone who could hit a misconfigured host could sign in with a known OTP | Removed fixed OTP from config; always `RandomNumberGenerator`; debug OTP only if `IsDevelopment && IsDevAppMode`; no console OTP logging; obsolete mock ignored | `OtpAuthServiceTests` (mock ignored, prod never returns debug OTP) |
| SEC-002 | High | `OtpAuthService.cs` `VerifyAsync` | OTP brute-force / reuse / enumeration | Unlimited verify attempts; OTP removed only after success path; send revealed missing users | Account takeover via 6-digit guessing; email enumeration | Max 5 failures then invalidate; hashed OTP; per-email/IP send + verify throttles; cooldown; atomic consume under lock; generic send message | Brute-force, concurrent verify, cooldown, enumeration tests |
| SEC-003 | High | `.env.example`, `src/.../.env.example` | Default Firebird password in examples | `CENTRAL_FB_MASTERKEY=masterkey` | Operators copy insecure defaults into real hosts | Placeholder `REPLACE_WITH_STRONG_PASSWORD`; document least-privilege user | Manual review of examples |
| SEC-004 | Medium | `Program.cs` `/health` | Public health info disclosure | Anonymous JSON with queue depths, SDK company, lastError | Recon for attackers; ops fingerprinting | `/health` → `{status:ok}` only; `/health/detail` requires Admin | Code review |
| SEC-005 | Medium | `AccountsController.cs` | API error detail leakage | `502` returned raw `write.Message` | May expose paths/COM/DB detail | Generic client error + `requestId`; detail logged redacted | Code review |
| SEC-006 | High | `_buildcheck/` (~3968 files), `_run.log`, `_api500.html` | Committed build/debug artifacts | Tracked binaries/logs in Git | Supply-chain noise; possible future secret leakage | `git rm --cached`; `.gitignore` updated | Pattern scan of logs: **0** secret-like hits; no tracked `_buildcheck/**/*.env` |
| SEC-007 | Medium | `DatabaseFileBrowser.cs` | Full fixed-drive FDB search | Enumerated all fixed drives after preferred roots | Path disclosure / DoS | Search limited to approved roots only | Code review |
| SEC-008 | Low | Cookie auth | Missing SecurePolicy | HttpOnly+SameSite set; Secure unset | Cookie on plain HTTP if TLS terminated oddly | `CookieSecurePolicy.SameAsRequest` | Code review |
| SEC-009 | Low | `Program.cs` | Missing basic security headers | No XFO / nosniff | Clickjacking / MIME sniffing | Added X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy | Code review |
| SEC-010 | Informational | `appsettings.json` Bootstrap | Personal admin email in repo | `jason.choo2004@gmail.com` committed | Privacy / targeting | Replaced with `admin@localhost` | Diff review |
| SEC-011 | Medium (weakness) | Razor Pages / policies | Manager/Operator policies == Admin only | `PolicyNames.Manager/Operator` require Admin role | Fine-grained roles unused | Documented; no role redesign (preserve behavior) | Code review |
| SEC-012 | Informational | `AllowedHosts: *`, ForwardedHeaders | Broad host / proxy trust | Default config | Host-header / IP spoof if mis-proxied | Documented deployment hardening | Remaining risk |
| SEC-013 | Low (existing controls) | `FirebirdReadAdapter` dynamic SQL | Identifier interpolation | Table/column names from allowlisted schema discovery | Injection if allowlist bypassed | Values parameterized; identifiers from internal maps — monitored | Existing `SqlGuard` on ad-hoc SQL |
| SEC-014 | Informational | Stack note | “REST API” vs SDK | Writes are COM SDK (`SdkWriteAdapter`), not SQL Accounting REST | N/A | Audited actual SDK path | N/A |

**Classification notes**

- **Confirmed fixed:** SEC-001–SEC-010, SEC-007–SEC-009  
- **Weakness accepted (documented):** SEC-011 role policy collapse  
- **Remaining / runtime:** SEC-012, Firebird least-privilege account creation, history rewrite for `_buildcheck`  
- **Already partially protected:** parameterized values in reads; `CredentialRedactor`; antiforgery on login JSON handlers; cookie principal revalidation against SY_USER  

---

## Modified files (this remediation)

- `src/SqlAccountSync/Services/Auth/OtpAuthService.cs`
- `src/SqlAccountSync/Pages/Login.cshtml.cs`
- `src/SqlAccountSync/Program.cs`
- `src/SqlAccountSync/Options/AppOptions.cs`
- `src/SqlAccountSync/appsettings.Development.json`
- `src/SqlAccountSync/appsettings.json`
- `src/SqlAccountSync/Controllers/AccountsController.cs`
- `src/SqlAccountSync/Helpers/DatabaseFileBrowser.cs`
- `.env.example`, `src/SqlAccountSync/.env.example`
- `.gitignore`
- `tests/SqlAccountSync.Tests/OtpAuthServiceTests.cs` (new)
- `docs/CREDENTIAL_ROTATION.md` (new)
- `SECURITY_AUDIT.md` (this file)
- Git index: removed `_buildcheck/**`, `src/SqlAccountSync/_run.log`, `src/SqlAccountSync/_api500.html`

---

## Security improvements implemented

1. Cryptographically random 6-digit OTPs; SHA-256 stored hashes; single-use atomic consume.  
2. Send/verify rate limits and failure lockout.  
3. No account enumeration on OTP send.  
4. Debug OTP UI only on local Development + `APP_MODE=dev`.  
5. Minimal public health endpoint; admin detail endpoint.  
6. Safer example credentials and bootstrap email.  
7. Build artifact purge from Git tracking.  
8. DB file browser confined to approved roots.  
9. Basic security headers + cookie SecurePolicy.  
10. Credential rotation runbook.

---

## Test results

```
OtpAuthServiceTests: 7 passed
Full suite: 98 passed, 2 failed (pre-existing)
  - GlAccountMapperTests.ToApiPayload_uses_dest_parent_and_zero_dockey
  - ItemDestCodeLinkTests.ProcessMaster_links_when_destination_item_code_exists_without_api_create
```

Tests needing Firebird / AWS / SMTP / SQLACC COM were not run against production. OTP tests use in-memory fakes only.

---

## Remaining risks / manual deployment actions

1. **Set strong `CENTRAL_FB_MASTERKEY`** (and prefer non-SYSDBA) on every host; do not leave `masterkey`.  
2. **Production hosts:** `ASPNETCORE_ENVIRONMENT=Production`, `APP_MODE=prod`.  
3. **Monitoring:** switch probes that expected rich `/health` JSON to `/health` liveness or authenticated `/health/detail`.  
4. **Git history:** `_buildcheck` binaries may still exist in older commits — rotate any credentials if ever embedded; history purge is optional coordinated ops (see `docs/CREDENTIAL_ROTATION.md`). Do not force-push without agreement.  
5. **Tighten `AllowedHosts`** and reverse-proxy forwarded-header known networks.  
6. **CSRF:** Login validates antiforgery manually; keep tokens on other state-changing forms.  
7. Pre-existing failing unit tests should be fixed separately (not introduced by this audit).

---

## Dependency vulnerability check

```text
Transitive: System.Security.Cryptography.Pkcs 8.0.1
  High     GHSA-846f-f764-76mx
  Moderate GHSA-2fmv-rv2c-59x7
```

Pulled in transitively (likely MailKit / signing stack). Pinning 9.0.x / 10.0.0 still reported the same advisories in the NuGet vulnerability feed at audit time, so no forced upgrade was committed (would not clear the warning and risks package churn). **Remaining risk:** track .NET / MailKit updates; re-run `dotnet list package --vulnerable --include-transitive` after each restore.
