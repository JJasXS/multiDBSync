# Setup

## Requirements

- .NET 8 SDK
- Empty central Firebird database for `SYNC_*` tables
- Company `.FDB` files only after backup/recovery is in place

## Local run

```powershell
copy src\SqlAccountSync\.env.example src\SqlAccountSync\.env
```

Set `CENTRAL_FB_PASSWORD` and `CentralDb` path. Then:

```powershell
dotnet run --project src\SqlAccountSync --launch-profile http
```

Sign in with `Bootstrap:AdminEmail` (default `admin@localhost`). In Development, OTP is printed to the console when SMTP is unset.

Do not enable `ApiCredRef` or `Sync:AllowProductionConnections` until [BACKUP_AND_RECOVERY.md](BACKUP_AND_RECOVERY.md) is complete.

## Tests

```powershell
dotnet test tests\SqlAccountSync.Tests\SqlAccountSync.Tests.csproj
```
