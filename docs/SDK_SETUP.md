# SQL Account Sync — SDK Live setup checklist

## Before you click Run as admin on the DeployCommands SDK script

1. **Push branch** `sdk-conversion` to GitHub (`multiDBSync`) so the deploy clone works.
2. **SQL Account installed** on the Windows PC that will run the bridge/app.
3. **COM registered**: ProgID `SQLAcc.BizApp` (install SQL Account or `SQLACC.exe /regserver` elevated).
4. **Close** stray SQLACC/tester processes if you hit COM RPC failures earlier.
5. **AWS**: either valid `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` in `.env`, or `aws login` working for Secrets Manager.
6. **Secrets Manager secret created** (see below) with at least the UAMAT row; add the other companies to the same JSON.
7. **Tenant DynamoDB / bootstrap**: `TENANT_CODE` active; `maxDatabases` / `MAX_DBCREATE` high enough for your company count (e.g. ≥ 40).
8. **Central SYNC.FDB** path ready (optional for `/health`, required for full UI/jobs).
9. Know the **Windows username** that will stay logged on (scheduled task runs as that user).

## AWS Secrets Manager

**Secret name (default):**

`proacc/tenant/{TENANT_CODE}/sdkConnections`

Optional override via `.env`:

`SDK_SECRET_ID=proacc/tenant/TNT10006/sdkConnections`

**JSON shape (ONE secret, many companies):**

```json
{
  "companies": [
    {
      "name": "UAMAT",
      "dcfPath": "C:\\eStream\\SQLAccounting\\Share\\SQLACC.DCF",
      "database": "ACC-UAMAT.FDB",
      "username": "ADMIN",
      "password": "YOUR_SQL_ACCOUNT_PASSWORD"
    }
  ]
}
```

- `name` must match `SYNC_DATABASE.DBCODE` (company Code in the UI).
- `dcfPath` is the Windows path to the DCF that lists the company (for UAMAT: `SQLACC.DCF`, not `Default.DCF`).
- `database` is the **filename only** (e.g. `ACC-UAMAT.FDB`).
- Add the other ~39 DBs as more objects in `companies[]` (same secret). Prefer this over one secret per company for this app.

**IAM (runtime user/role used by the app):**

- `secretsmanager:GetSecretValue` on that secret ARN
- Optionally `secretsmanager:ListSecrets` if you use tooling; create/put only needed for admins who edit the secret

**Do not** put SQL Account passwords in the browser, Razor pages, or git.

## DynamoDB (tenant config)

No new DynamoDB table is required for SDK.

Confirm the existing tenant item (via ProAcc tenant bootstrap API / Dynamo):

- `tenantCode` = your `TENANT_CODE`
- `status` = `ACTIVE`
- Plan cap field `maxDatabases` or `MAX_DBCREATE` ≥ number of companies you will register
- Menu flags if you use them (`maxDatabasesShowSynchronizationUi`, etc.)

`sqlApi` block in the tenant item can stay; SqlAccountSync SDK mode **ignores** SQL API keys.

## Register companies in SqlAccountSync UI

For each company: Code / Name / Role / Firebird host+path (for reads).  
SDK login for that Code must exist in the secret `companies[]` array.

## Rollback

```powershell
cd C:\Users\sqlsupport\SqlAccountSync
git checkout before-sdk-conversion
# or: git reset --hard before-sdk-conversion
```

Redeploy with the original `DeployCommands\SqlAccountSync\deploy-SqlAccountSync-github.cmd` (Windows service + API mode).

## CLI helpers on this machine

```powershell
dotnet run --project src\SqlAccountSync -- --sdk-smoke --db=UAMAT
dotnet run --project src\SqlAccountSync -- --sdk-discover --db=UAMAT
```
