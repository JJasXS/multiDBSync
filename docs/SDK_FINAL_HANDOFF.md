# SqlAccountSync SDK conversion — what you need before Run as admin

Implementation lives on branch **`sdk-conversion`**. API-era restore point: tag **`before-sdk-conversion`** (commit `418a948`).

---

## A. Before you double-click DeployCommands (Run as admin)

1. **Push the branch** so the deploy script can clone it:
   ```powershell
   cd C:\Users\sqlsupport\SqlAccountSync
   git push -u origin sdk-conversion
   ```
2. **SQL Account installed** on that Windows PC; ProgID `SQLAcc.BizApp` registered.
3. **Set the UAMAT (and other) SQL Account password** in Secrets Manager — the secret was created with an empty password; Login will fail until you set it:
   ```powershell
   cd C:\Users\sqlsupport\SqlAccountSync
   powershell -ExecutionPolicy Bypass -File .\scripts\create-sdk-secret.ps1 -TenantCode TNT10006 -CompanyName UAMAT
   ```
   Then edit the same secret in AWS console (or put-secret-value) to append the other ~39 `companies[]` rows.
4. **`.env` values ready** for Notepad during deploy:
   - `TENANT_CODE` (e.g. TNT10006)
   - `TENANT_BOOTSTRAP_API_URL`
   - `AWS_REGION=ap-southeast-1`
   - `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (or rely on `aws login` profile + keys as you already use)
   - optional `SDK_SECRET_ID=proacc/tenant/TNT10006/sdkConnections`
   - `ASPNETCORE_URLS=http://0.0.0.0:5030`
5. Know the **Windows username** that stays logged on (scheduled task At log on).
6. Prefer **SQL Account UI closed** (or already on the same company) before first Connect to avoid AccountUI crashes.
7. Central **SYNC.FDB** path configured when you want full jobs UI (optional for `/health` only).

Deploy script:

`C:\Users\sqlsupport\DeployCommands\SqlAccountSync-SDK\deploy-SqlAccountSync-SDK-github.cmd`

It auto-elevates, disables the old Windows **service**, publishes `sdk-conversion`, and creates task **SqlAccountSync-SDK**.

---

## B. AWS Secrets Manager

| Item | Value |
|------|--------|
| Secret name | `proacc/tenant/TNT10006/sdkConnections` (already created) |
| Shape | One JSON document with `companies[]` |
| UAMAT sample | `name=UAMAT`, `dcfPath=C:\eStream\SQLAccounting\Share\SQLACC.DCF`, `database=ACC-UAMAT.FDB`, `username=ADMIN`, `password=…` |

Add more companies as more objects in the **same** `companies` array (do not create 40 separate secrets for this app).

`name` must equal the company **Code** in SqlAccountSync (`SYNC_DATABASE.DBCODE`).

**IAM for the runtime identity:** `secretsmanager:GetSecretValue` on that secret ARN.

---

## C. DynamoDB

Table: **`TENANT_CONFIG`**

Checked for `TNT10006`:

- `status` = **ACTIVE**
- `maxDatabases` = **41** (enough for ~40 companies)
- Sync UI flag present

No new DynamoDB table or attribute is required for SDK. The old `sqlApi` block can remain; SDK mode ignores SQL API keys.

If another tenant needs 40 DBs, set that tenant’s `maxDatabases` / `MAX_DBCREATE` ≥ 40 the same way.

---

## D. What changed in code (summary)

- Writes: `SdkWriteAdapter` + `SdkSessionManager` (COM `SQLAcc.BizApp`, STA thread, one company at a time)
- Secrets: `ISdkConnectionBundle` → `proacc/tenant/{TENANT}/sdkConnections`
- Removed: HTTP SigV4 client, API credential bundle, Firebird write fallback
- Reads: still Firebird read-only
- CLI: `--sdk-smoke`, `--sdk-discover`
- Health JSON now includes an `sdk` object

Verified on this machine: COM create works; Login rejected empty password (`Invalid user name or password`). After you set the real ADMIN password, re-run:

```powershell
dotnet run --project src\SqlAccountSync -- --sdk-smoke --db=UAMAT
```

---

## E. Rollback

```powershell
git checkout before-sdk-conversion
# redeploy with DeployCommands\SqlAccountSync\deploy-SqlAccountSync-github.cmd
```

---

## F. After deploy — register companies

In the UI, add each company (Code / Firebird path for reads). Ensure each Code has a matching `companies[].name` in the secret. Then use Push / Transactions as before; jobs write via SDK instead of HTTP API.
