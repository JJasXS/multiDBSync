# Backup and recovery

This application orchestrates SQL Account company databases. Application-level rollback does **not** replace database backups.

Complete these steps before enabling Phase 4 or Phase 5 write operations (`Sync:AllowProductionConnections` and SQL API credentials).

## 1. SQL Account backup

For every Master and Sub database:

1. Take a full Firebird backup (`.fbk`) using `gbak` or the SQL Account backup utility.
2. Store backups off the application server.
3. Record the backup timestamp, database path, and company name.

Example (run on the Firebird host):

```text
gbak -b -user SYSDBA -password ***** host:C:/Data/CompanyA.FDB C:/Backup/CompanyA-YYYYMMDD.fbk
```

## 2. Backup verification

Restore each backup to a **test** file (never over the live database) and open it in SQL Account:

```text
gbak -c -user SYSDBA -password ***** C:/Backup/CompanyA-YYYYMMDD.fbk host:C:/Data/CompanyA-RESTORE.FDB
```

Confirm customers, items, and a recent sales invoice are present.

## 3. Recovery procedure

1. Stop this application and any other writers to the affected company database.
2. Restore the last known-good `.fbk` to a new file first, then swap after verification.
3. In SQL Account Sync, review `SYNC_TXN` and `SYNC_JOB` for in-flight work. Do not retry blindly.
4. Reconcile destination documents before retrying (the Retry action already checks `SYNC_TXN`, TXN stamp, and `ST_XTRANS`).

## 4. Test vs production

- Register each company database with `ENVIRONMENT` = DEVELOPMENT, TESTING, or PRODUCTION.
- The application refuses PRODUCTION connections unless `Sync:AllowProductionConnections` is true.
- Cross-environment connections are refused unless `Sync:AllowCrossEnvironment` is true.
- Point Development at copies, never at live accounting files.

## 5. Central control database

Back up the central Firebird file (`CentralDb:Path`) on the same schedule. It holds mappings, jobs, transaction links, and audit history. Soft-deleted records must remain restorable.
