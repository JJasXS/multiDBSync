# SQL Account Sync — enterprise documentation

## Purpose

Central orchestration layer for Master/Sub SQL Account databases. It is not a copy of SQL Account. Company Firebird files remain the source of accounting data.

## Stack

- ASP.NET Core 8 Razor Pages
- FirebirdSql.Data.FirebirdClient 10.3.4
- Central control database: Firebird (`SYNC_*` tables)
- Reads: Firebird read-only transactions
- Writes: official SQL Accounting REST API (SigV4), never raw INSERT into company DBs

## Safety

1. Company reads use `FbTransactionBehavior.Read`.
2. `SqlGuard` allows only SELECT/WITH.
3. Idempotency keys unique on `SYNC_JOB` and `SYNC_TXN`.
4. Mapping uniqueness prevents silent conflicts.
5. Dry run has no write adapter path to the API.

## Roles

ADMIN, MANAGER, OPERATOR, VIEWER (see product spec §30).

## Schema

See `Data/SchemaBootstrap.cs`. Version tracked in `SYNC_SCHEMA_VER`.

## How to run

1. Copy `src/SqlAccountSync/.env.example` to `.env`.
2. Create an empty central Firebird database and set `CentralDb__Path` / `CENTRAL_FB_MASTERKEY`.
3. `dotnet run --project src/SqlAccountSync`
4. Sign in with the bootstrap admin email (OTP; console in Development).

## How to test

```text
dotnet test tests/SqlAccountSync.Tests/SqlAccountSync.Tests.csproj
```

Live SQL Account checks are opt-in via the Browse and Test Connection screens. Automated tests use fakes and never touch a company database.

## Phased writes

Do not configure `ApiCredRef` or `Sync:AllowProductionConnections` until backup/recovery in `docs/BACKUP_AND_RECOVERY.md` is complete and schema discovery on a real Sub database has verified required tables.
