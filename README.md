# SQL Account Sync

Orchestration layer for Master/Sub SQL Account databases.

## Run

```powershell
cd C:\Users\sqlsupport\SqlAccountSync
copy src\SqlAccountSync\.env.example src\SqlAccountSync\.env
# Edit .env: set CENTRAL_FB_PASSWORD to the Firebird SYSDBA password (often masterkey)
dotnet run --project src\SqlAccountSync --launch-profile http
```

Sign in with the bootstrap admin email (`Bootstrap:AdminEmail`, default `admin@localhost`). In Development the OTP is written to the console when SMTP is not configured.

## Test

```powershell
dotnet test tests\SqlAccountSync.Tests\SqlAccountSync.Tests.csproj
```

## Docs

- [Backup and recovery](docs/BACKUP_AND_RECOVERY.md) — required before enabling writes
- [Operations](docs/OPERATIONS.md)
- [Enterprise](docs/ENTERPRISE.md)
