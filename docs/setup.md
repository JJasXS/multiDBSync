# Setup

## Requirements

- .NET 8 SDK
- Empty central Firebird database for `SYNC_*` tables
- Company `.FDB` files only after backup/recovery is in place

## Local run

```powershell
copy src\SqlAccountSync\.env.example src\SqlAccountSync\.env
```

Do not commit `.env`. Enable the local secret filename hook once per clone:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-git-hooks.ps1
```

Details and limitations: [SECURITY_PROTECTIONS.md](SECURITY_PROTECTIONS.md).

Set `CENTRAL_FB_MASTERKEY` and `CentralDb` path. Then:

```powershell
dotnet run --project src\SqlAccountSync --launch-profile http
```

Sign in with a user email registered in `SYNC_USER` (optional `Bootstrap:AdminEmail` seed/hint only — no default). In Development, OTP may print to the console when SMTP is unset.

Do not enable `ApiCredRef` or `Sync:AllowProductionConnections` until [BACKUP_AND_RECOVERY.md](BACKUP_AND_RECOVERY.md) is complete.

## Tests

```powershell
dotnet test tests\SqlAccountSync.Tests\SqlAccountSync.Tests.csproj
```
