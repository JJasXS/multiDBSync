# sqlSign — SQL Account SDK Deployment & Update Guide

**Purpose:** A repeatable checklist for the team when reviewing, updating, or deploying the sqlSign REST-to-SDK migration to the **Test Server**.

| Field | Current information |
|---|---|
| Application | SQL Account Sync (sqlSign), ASP.NET Core / .NET 8 |
| Development branch | `YHang` |
| Latest SDK migration commit reported | `61c02070ba2bf1f35534fdd9b123c6c5c5f337bb` |
| Deployment branch | `origin/master` was observed; verify the actual deployment trigger before merging (`origin/main` was not present) |
| Test website | `http://192.168.100.201:5030` |
| Test control database | Server-side `SYNC.FDB` (12-company registry) |
| Rollback/default write provider | `Rest` |
| Document status | Deployment runbook; **not** evidence that the Test Server is SDK-ready |

> **Important:** The website called "Live" in project discussions is a **Test environment**. Merging into the deployment branch may automatically redeploy it. Do not interpret a successful Git push or unit test as deployment approval.

## 1. Scope and success criteria

The migration covers **only paid SQL Account REST API operations reachable from the existing sqlSign Website**—including active features, conditional actions, and background workers. Unused APIs are out of scope.

A successful SDK deployment must:

- Route every currently used paid SQL Account REST operation through the SQL Account COM SDK or an existing **read-only** Firebird query.
- Prevent paid REST HTTP calls and silent fallback when `Provider=Sdk`.
- Preserve existing website behavior, authorization, business rules, reports, and dynamic multi-company targeting.
- Keep the **database schema unchanged**: no table/column/index/constraint changes and no direct Firebird INSERT/UPDATE/DELETE as an SDK substitute.
- Preserve a deliberately selectable `Provider=Rest` rollback path, without accidentally running REST and SDK write workers against the same job queue.

**Current evidence (as reported):** 20 active/conditional REST operations mapped in SDK mode; 232/232 unit tests passed; live STEPPES SDK tests passed for Customer/Supplier/GL Account CUD, Stock Group creation, Item + `cdsUOM`, and PI/GR/PR/CN/DN creation. This does **not** establish complete authenticated Website end-to-end behavior.

## 2. Mandatory pre-deployment checklist

Ask the Test Server administrator/owner to complete this table. Record **Yes / No / Unknown**, evidence, and approver. **Do not paste passwords or connection strings into the checklist.**

| Check | Required confirmation | Status / evidence |
|---|---|---|
| Deployment ownership | Confirm the real deployment branch, pipeline, and who can roll back | TBD |
| Existing server build | Capture deployed binary path/version and determine why Test `/health` differs from known Git history | TBD |
| Runtime environment | Confirm effective `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` for the website process | TBD |
| Effective config | Confirm the actual running `Provider`, `AllowLiveCom`, and background worker settings, including server overrides | TBD |
| SQL Account installation | SQL Account application/runtime is installed on the **same Windows host** that runs the website | TBD |
| COM registration | `SQLAcc.BizApp` is registered with architecture compatible with the website process | TBD |
| Service identity | Identify the website's Windows service/application-pool identity and its COM activation permissions | TBD |
| Company access | That identity can access the configured DCF and registered company FDB targets | TBD |
| SDK password | Secure server-side secret source exists and the **website process identity** can read it | TBD |
| SDK entitlement | Confirm licensing/SDK Live permissions for the companies used on the Test Server; do not assume from local POC alone | TBD |
| Job isolation | Confirm which Test workers are active; prevent competing REST/SDK claims or writes during cutover | TBD |
| Backup and rollback | Confirm restore capability for relevant Test databases and preservation of prior application build/config | TBD |
| Monitoring | Confirm access to application logs, COM errors, worker/job status, and recovery procedures | TBD |

**Stop deployment if any critical item is Unknown or No.** In particular, the server environment, unknown deployed binary, COM availability, service identity, and shared job queue cannot be inferred from local testing.

## 3. SDK password and secret handling

A dedicated extension or NuGet package is **not inherently required**. The code uses late-bound Windows COM (`SQLAcc.BizApp`). A secure secret source and correctly configured COM installation are still required on the website host.

**Acceptable approaches, subject to the organization's security policy:**

1. **Existing organization-managed secret store** (for example AWS Secrets Manager), **only if the deployed application already has an approved integration to retrieve it**. A secret stored there is not automatically readable by the current application.
2. **Protected service-level environment variable**, if approved and readable by the actual website service identity. Limit access and avoid exposing the value in logs, command-line arguments, or source control.
3. Another approved Windows/service secret mechanism, after confirming how it is wired into the .NET application.

**Do not use `.NET User Secrets` as a production/server secret store.** It is designed for development and is not a secure encrypted production vault. The local development user's `SQLACC_SDK_PASSWORD` does not prove the website service can read the same value.

Administrator checks:

- Confirm **where** `SQLACC_SDK_PASSWORD` (or the configured SDK password source) is supplied at runtime.
- Confirm the website service identity can read the configured source **without displaying or logging the password**.
- Confirm restart/deployment behavior: will the website receive the secret after a service restart?
- Verify an SDK **Login → read-only query → Logout** under the website's actual identity.
- Document password rotation responsibility and the secure rollback procedure.

Do not write SDK passwords to `appsettings.json`, `appsettings.Development.json`, Git, screenshots, or this guide.

## 4. Windows host and COM verification

Run these **read-only inventory commands** on the Test Server with administrator assistance. They identify the process and files but **do not prove** that the running service can activate COM.

```powershell
# Identify the process listening on port 5030.
$webProcessId = Get-NetTCPConnection -LocalPort 5030 -State Listen |
    Select-Object -First 1 -ExpandProperty OwningProcess
Get-CimInstance Win32_Process -Filter "ProcessId=$webProcessId" |
    Select-Object ProcessId, Name, ExecutablePath

# Inspect possible website services and service identities.
Get-CimInstance Win32_Service |
    Where-Object { $_.PathName -match 'SqlAccountSync' } |
    Select-Object Name, State, StartName

# Inspect which config files are deployed next to the running executable.
$webExe = (Get-CimInstance Win32_Process -Filter "ProcessId=$webProcessId").ExecutablePath
if ($webExe) {
    Get-ChildItem (Split-Path $webExe) -Filter 'appsettings*.json' |
        Select-Object Name, Length, LastWriteTime
}

# Check whether the COM ProgID appears registered (registration alone is not sufficient).
Test-Path 'Registry::HKEY_CLASSES_ROOT\SQLAcc.BizApp'
```

**Important caveats:**

- A PowerShell session's `Process` environment variables are **not** those of the already running website. Verify effective process configuration using an approved service/host inspection method, not a separate shell's environment.
- `SQLAcc.BizApp` registry presence is not proof of proper 32-bit/64-bit compatibility, activation, permissions, or successful COM Login.
- A successful console POC is not proof that a Windows Service/IIS app-pool identity can use COM. Test the real hosting identity, and confirm COM lifecycle/STA behavior.
- The SQL Account GUI generally does not need to stay manually open for COM calls, but the underlying runtime and service permissions must work.

## 5. Configuration and environment isolation

**Current committed safe defaults:**

```text
SqlAccountingWrite:Provider = Rest
SqlAccSdk:AllowLiveCom = false
```

**Local-only settings previously used for lab work** (`Provider=Sdk`, `AllowLiveCom=true` in `appsettings.Development.json`) were deliberately **not committed**. Do not copy that local file to Test or assume the Test Server uses `Production`.

Before enabling SDK on Test:

1. Confirm the environment name and actual configuration precedence (JSON, environment variables, service/container/pipeline overrides, and secret providers).
2. Check which configuration file is loaded by the **deployed build**, which may differ from Git history.
3. Configure `Provider=Sdk` and live COM **only through an approved server-specific deployment change**, with rollback instructions.
4. Verify `DefaultDcfPath` and each company `FbPath` come from the **registered company configuration**, not hardcoded pilot aliases.
5. Ensure local development and Test do **not** share write access to the same `SYNC.FDB` while Test workers are active. A local read-only 12-company registry is not equivalent to sharing the control DB for jobs and writes.
6. Review `EnableBackgroundWriteWorkers` and `AllowDevSingleJobProcess` in the **actual bound options** before cutover. The Dev single-job tool is a guarded test utility, not a replacement for the normal job workers.
7. Verify the deployed default stays safe if no SDK override is supplied.

**No blind merge:** Previously, Test `/health` exposed SDK diagnostics not found in the known branch history; its binary, environment, and deployment source remain unverified. Preserve the running build and establish its provenance before replacing it.

## 6. Update and deployment sequence

### A. Before merging `YHang`

- [ ] Review the exact `YHang` diff against the **confirmed deployment branch**.
- [ ] Review non-SDK adjacent changes (for example optional LoginDb and tenant refresh wiring) for unintended behavior changes.
- [ ] Confirm build and full test suite pass for the exact intended merge commit.
- [ ] Record the existing Test binary/config, database backups, and rollback owner.
- [ ] Confirm all mandatory items in Section 2, including service COM readiness.
- [ ] Ensure no local SDK password, `appsettings.Development.json` lab override, `_buildcheck*`, or generated artifacts are included.
- [ ] Agree on a maintenance/cutover window if background workers or jobs may be impacted.

### B. Deploy safely

- [ ] First deploy only with the **existing safe provider configuration** unless a separately approved SDK cutover is scheduled.
- [ ] Check `/health`, Login, authorization, and basic read-only reports; compare with the pre-deploy state.
- [ ] Confirm expected workers and queue ownership. Do not let two application instances process the same jobs against different write providers.
- [ ] For the approved SDK cutover only: enable the effective SDK provider and COM gate through the secured Test configuration.
- [ ] Restart using the approved process, then prove which effective provider is running **without revealing secrets**.

### C. Verify Website behavior

Check both routes and outcomes, not just UI availability:

- [ ] Login and authorized access; Dashboard, company selection, reports, and read-only functions.
- [ ] Customer/Supplier/GL Account/Item/Stock Group operations where authorized test records exist.
- [ ] Push/Sync/AutoPush queue lifecycle, including enqueue, single claim, completion/error, and company selection.
- [ ] Document Transfer creation and mapping for PI, PR, CN, DN, GR, and ST where suitable test data exists.
- [ ] Error handling, duplicate prevention, and no accidental paid REST call when `Provider=Sdk`.
- [ ] Multi-company switch and correct target FDB; avoid cross-company writes.
- [ ] Compare expected field mapping and final business results, including conditional logic.

If test data is absent, mark **DATA-BLOCKED**; do not invent business data or call the feature PASS. A feature is PASS only after successful relevant verification.

### D. Acceptance and handover

- [ ] Save evidence of the **exact deployed commit/version, effective configuration, and service identity**.
- [ ] Save build/test results and a concise Website feature matrix with PASS / DATA-BLOCKED / UNVERIFIED / FAIL.
- [ ] Save job/audit evidence and confirm no paid REST endpoint was invoked in SDK mode.
- [ ] Document known gaps, monitoring owner, support contact, and rollback trigger.
- [ ] Obtain supervisor/deployment-owner approval before considering cutover complete.

## 7. Known limitations to keep visible

| Item | Current evidence | Implication |
|---|---|---|
| Authenticated Website end-to-end soak | Not completed; Test/Login user data blocked local testing | **UNVERIFIED / DATA-BLOCKED** |
| Stock Transfer (`ST_XF`) | SDK mapping exists; STEPPES had only one stock location | **DATA-BLOCKED**; not a proven SDK failure |
| DO → GR `FROM*` link | Mapping exists; STEPPES had no suitable source `SL_DO` | **DATA-BLOCKED**; linkage semantics not fully live-proven |
| Push / Sync / AutoPush full workflow | Adapter routing verified; no complete live job soak | **UNVERIFIED** |
| Note vs `udf_txnid` | Potential behavioral difference from REST implementation | Validate field semantics in business review |
| Server-side COM service identity | Local COM POC works; Test service identity not verified | **BLOCKER** before SDK cutover |
| Actual Test deployment identity | Existing Test health behavior did not match known Git tips | **BLOCKER** before replacing deployed binary |

## 8. Rollback plan

1. **Stop further SDK job processing through the approved operational procedure**; do not arbitrarily kill services or edit job tables.
2. Record in-flight/pending/failed jobs and any document numbers created. Check whether operations succeeded before retrying to prevent duplicates.
3. Restore the previously approved application version and server configuration; return to `Provider=Rest` and `AllowLiveCom=false` **only under deployment-owner control**.
4. Confirm workers resume with exactly one intended queue owner and that no SDK/REST cross-environment competition exists.
5. Verify Login, health, read-only pages, and carefully chosen non-destructive operations.
6. Escalate any ambiguous COM write or partial document transfer. Do **not** delete/cancel financial documents or manipulate `SYNC.FDB` tables as a shortcut.

## 9. Sign-off record (complete during each update)

| Field | Record |
|---|---|
| Change request / target commit | |
| Deployment date / operator | |
| Pre-deploy application version | |
| Post-deploy application version | |
| Website environment | |
| Effective provider / COM enabled? | |
| Website service identity verified? | |
| SDK secret source verified (name only)? | |
| COM registration + service Login/Read/Logout | |
| DCF/FDB / registry connection verification | |
| Worker/queue isolation verified? | |
| Backup and rollback readiness | |
| Build/test/Website verification result | |
| REST HTTP prohibition checked in SDK mode? | |
| Known DATA-BLOCKED / UNVERIFIED cases | |
| Supervisor / deployment-owner approval | |

---

**Operational rule:** Do not mark the migration fully deployed merely because the source code routes all 20 Website REST operations or unit tests pass. The exact Test Server runtime, service identity, secret delivery, worker isolation, and relevant Website behavior must be confirmed at deployment time.
