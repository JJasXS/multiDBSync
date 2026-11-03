# SQL Accounting SDK Live migration (Phase 1–3)

Branch: `YHang`  
Local CHM: `D:\sql account sync\SQLSDK\SQLSDK.chm` (extracted under `SQLSDK\_extracted\`)  
Wiki: https://wiki.sql.com.my/wiki/SDK_Live

## Verified from local CHM

| Topic | Detail |
|--------|--------|
| ProgID | `SQLAcc.BizApp` / `CreateObject` / `ActiveXObject` |
| Login | `Login(UserName, Password, DCF Path, File Name)` — file name without path |
| IsLogin | boolean |
| BizObject lifecycle | `BizObjects.Find`, `DataSets.Find('MainDataSet')`, `New` / `Params`+`Open`+`Edit`, `FindField`, `Save`, `Close`, `Delete`, `FindKeyByRef` |
| Sample entities | **Agent**, **SL_IV**, **AR_PM**, **GL_CB** |

## Write adapter mapping (integration layer)

| Sync write | REST path | SDK BizObject | Status |
|------------|-----------|---------------|--------|
| Customer | `/customer` | `AR_CUSTOMER` | Wiki sample implemented |
| Supplier | `/supplier` | `AP_SUPPLIER` | Wiki name + customer-mirrored fields |
| Item | `/stockitem` | `ST_ITEM` | Wiki sample implemented |
| Account | `/account` | `GL_Acc` (name only) | **Blocked for CUD** — wiki `.Select` list only; no New/Edit/Save sample |
| PI | `/purchaseinvoice` | `PH_PI` | Wiki sample implemented |
| ST | `/stocktransfer` | `ST_XF` | Wiki sample implemented |
| PR | `/purchasereturned` | `PH_SC` | Wiki linking name |
| DN/CN | `/salesdebitnote` `/salescreditnote` | `SL_DN` / `SL_CN` | Wiki linking names |
| GR | `/goodsreceived` | `PH_GR` (name only) | **Blocked for create** — Text Import name; no SDK Live PostData. `ST_RC` ≠ GR |

Company targeting is dynamic: `SqlAccSdk:DefaultDcfPath` + `SYNC_DATABASE.FbPath` file name (no hardcoded company codes). POC aliases (Miracle/Onyat) stay in tools only.

## Remaining paid REST dependencies (API key blockers)

See `RestDependencyCatalog` in code. Summary:

| Dependency | Call site | Blocker |
|------------|-----------|---------|
| Write adapter (default) | `Provider=Rest` | Flip only after SDK write lab |
| GL Account CUD | Accounts / sync / chart edit | No official `GL_Acc` New/Edit/Save sample |
| DO→GR | `TransactionEngine` `/goodsreceived` | `PH_GR` Text Import name only; Linking table omits it |
| Stock group create | `ItemPrerequisiteService` `/stockgroup` | No `ST_GROUP` maintain sample |
| UOM master | `ItemPrerequisiteService` `/uom` | Often HTML; SKU uses `cdsUOM` instead |
| Optional chart proxy | `GET /api/accounts?source=api` | Prefer Firebird RO |
| FB timeout fallback | `SyncOrchestrator` | Auto-skipped when `Provider=Sdk` |

**Vendor support questions** (exact samples needed): `RestDependencyCatalog.VendorSupportQuestions`.

## Windows website runtime (hosting)

Designed host model: **Windows Service** (`Host.UseWindowsService`) with in-process Kestrel + `SyncWorker` / `AccountAutoPushWorker`. Local profiles also allow `dotnet run` and IIS Express. Design checklist assumes a **single Windows host** (multi-instance workers deferred).

SDK requirement: `SQLAcc.BizApp` must be installed/registered on the **same machine that runs the website process** (COM LocalServer → `SQLACC.exe`). Shared DCF/FDB access must work as the **service/app pool identity**, not only an interactive console user. CHM login notes for ASP/IIS: always pass DCF + FDB; prefer Firebird **TCP** under non-interactive accounts.

Console POC Login success does **not** by itself prove website service-account COM readiness.

## Website backend write path

```
UI / Workers
  → SyncOrchestrator | TransactionEngine | TransferMasterEnsure | AccountChartEdit | Push/Accounts pages
    → ISqlAccountWriteAdapter  (DI: SqlAccountingWrite:Provider Rest|Sdk)
         Rest → SqlAccountApiWriteAdapter (SigV4 HTTP)
         Sdk  → SqlAccountSdkWriteAdapter → ISqlAccSessionManager.RunAsync(RegisteredDatabase)
              → DefaultDcfPath + Path.GetFileName(FbPath) → COM Login → BizObject write
```

Reads stay on `ISqlAccountReadAdapter` / Firebird RO. Dry-run uses `DryRunWriteAdapter` (no COM/REST).

`SqlAccSessionManager` is a **singleton** with one COM app and a gate (`SemaphoreSlim(1,1)`): concurrent website requests serialize; company switches Logout→Login under the gate.

## Secure credential setup (read POC / session)

Existing mechanisms only (`ISecretResolver` + env / `.env` overrides):

| Setting | How to provide |
|---------|----------------|
| DCF | `SqlAccSdk__DefaultDcfPath` or POC `--dcf` / `--company` |
| User | `SqlAccSdk__UserName` or POC `--user` |
| Password | `SqlAccSdk__PasswordSecretRef=env:SQLACC_SDK_PASSWORD` **or** process env `SQLACC_SDK_PASSWORD` **or** interactive prompt in POC |
| Live COM | `SqlAccSdk__AllowLiveCom=true` (app session); POC passes `AllowLiveCom` locally |
| Writes | Keep `SqlAccountingWrite__Provider=Rest` until lab complete |

Never put passwords in source, CLI `--password`, or committed files.

## Get DCF Database List (discovery research)

Official SDK Live C# sample pack (`Example CSharp-20230819`) lists a menu item **Get DCF Database List**, but the wiki page does **not** inline the C# source or a documented `SQLAcc.BizApp` method signature for it. Local `SQLSDK.chm` documents Login with explicit DCF+FDB and does not document a DCF-list API.

**Reuse with Dynamic Company Management:** do **not** replace `SYNC_DATABASE` / `SYNC_DBLINK` discovery. If the sample zip is obtained later, treat DCF-list as an optional cross-check of FDB filenames against registry rows — not a redesign of company management.

`Logout` is wiki-documented; session manager uses it for multi-company switch.

## Configuration (REST remains default / rollback)

```json
"SqlAccountingWrite": { "Provider": "Rest" },
"SqlAccSdk": {
  "ProgId": "SQLAcc.BizApp",
  "AllowLiveCom": false,
  "DefaultDcfPath": "",
  "UserName": "",
  "PasswordSecretRef": ""
}
```

Set `Provider` to `Sdk` and `AllowLiveCom` to `true` only after Login/Read/company-switch verification and approved backups. REST adapter stays registered for rollback.

## Safety

- No live COM in tests (`AllowLiveCom: false`).
- SDK adapter does **not** fall back to Firebird writes.
- REST adapter remains registered and is the default `ISqlAccountWriteAdapter`.

## SDK Read POC (isolated)

Tool: `tools/SqlAccSdkReadPoc`

- Always requires explicit `--dcf` and `--fdb` (no last-used defaults).
- SELECT-only via `SqlGuard` + `DBManager.NewDataSet`.
- LAN/UNC targets blocked unless `--authorize-lan` and exact `--authorized-target "DCF|FDB"` (or semicolon allow-list).
- Default run without `--live` performs **no** COM Login (safety preflight only).
- `--company miracle|onyat` resolves shared DCF + FDB; `--switch` tests Miracle → Onyat.
- **`--inspect-bizobjects`**: CHM `application_objects4` metadata only (`BizObjects` / `DataSets` / `Fields` / `XMLFieldDefs`). **No** New/Edit/Save/Delete/Post/Open.
- **`--inspect-targets-only`**: compact REST-blocker report (`GL_Acc`, `PH_GR`, `ST_GROUP`, `ST_CATEGORY`, UOM candidates, `ST_ITEM` for cdsUOM; `ST_RC` labeled as not Purchase GR). No full catalog / no XML dump. Implies inspect mode.
- **`--export-field-map <path.md>`**: writes **all** Fields + XMLFieldDefs for inspected targets (metadata only; CUD unverified). Use with `--inspect-targets-only` before coding adapters.
- Field mapping + Write POC plan (not executed): [`SDK_REST_FIELD_MAPPING_AND_WRITE_POC.md`](SDK_REST_FIELD_MAPPING_AND_WRITE_POC.md).
- **`ACC-UAMAT.FDB` is refused** (not a project database).

```powershell
$env:SQLACC_SDK_PASSWORD = '***'   # or interactive prompt
dotnet run --project tools/SqlAccSdkReadPoc -- `
  --live --inspect-targets-only --company miracle --user ADMIN `
  --authorize-lan `
  --authorized-target "\\192.168.100.201\Share\SQLACC.DCF|ACC-MIRACLE.FDB;\\192.168.100.201\Share\SQLACC.DCF|ACC-ONYAT.FDB"
```

## Project databases (authorized)

| SYNC DBCODE | SYNC role | Master link | DCF CompanyName | SDK FDB | SYNC FBPATH (host 192.168.100.201) |
|-------------|-----------|-------------|-----------------|---------|-------------------------------------|
| MIRACLE | SUB | MAINTEST → MIRACLE | MIRACLE COMPASS SDN BHD | `ACC-MIRACLE.FDB` | `C:/eStream/SQLAccounting/DB/ACC-MIRACLE.FDB` |
| ONYAT | SUB | MAINTEST → ONYAT | ON YAT SECURITIES (MALAYSIA) SDN BHD | `ACC-ONYAT.FDB` | `C:/eStream/SQLAccounting/DB/ACC-ONYAT.FDB` |

Shared DCF: `\\192.168.100.201\Share\SQLACC.DCF`  
Master registry DB: `MAINTEST` / `ACC-MAIN.FDB` (not a write POC target unless separately authorized).

## SDK Agent Write POC (Phase 3+)

Tool: `tools/SqlAccSdkWritePoc`

Documented Agent surface used by the POC (local CHM + wiki; no undocumented methods):

| Step | API |
|------|-----|
| Resolve object | `BizObjects.Find("Agent")` |
| Dataset | `DataSets.Find("MainDataSet")` |
| Fields | `Code`, `Description` |
| Create | `New` → set fields → `Save` |
| Open/Edit | `Params.Find("Code")` → `Open` → `Edit` → `Save` → `Close` |
| Exists | `FindKeyByRef("CODE", code)` (wiki) with Params+Open fallback |
| Delete | `Params` → `Open` → `Delete` → `Close` |
| Session | `Login` / `Logout` / `IsLogin` |

Safety:

- Requires `--backup-path` to an existing `.fbk`/`.zip`/`.7z`/`.bak` before any write.
- Unique codes `SDKPOC…` only; never overwrites existing Agents.
- If Delete fails, reports code for manual cleanup (`OrphanPossible`).
- After CRUD, optionally verifies the other project FDB does not contain the POC Agent.
- No Firebird DML; REST write path unchanged; app not switched to SDK.

### Run status (project DBs)

| Check | Result | Kind |
|-------|--------|------|
| SYNC_DATABASE MIRACLE/ONYAT + links | Verified (both SUB of MAINTEST) | central DB |
| DCF registration | Verified in `SQLACC.DCF` | live DCF |
| Restorable MIRACLE/ONYAT backup on Share | **NOT FOUND** | live preflight |
| `SQLACC_SDK_PASSWORD` | **NOT SET** | credential gate |
| Live SDK Read / Write / Switch | **NOT RUN** until password + backup | live blocked |
