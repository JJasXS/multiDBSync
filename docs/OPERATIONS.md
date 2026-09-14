# Operations guide

## Queue

Jobs live in `SYNC_JOB`. The in-process worker claims a row only when `STATUS` is PENDING or RETRYING and `NEXTRUNAT` is due. Claim uses `rows affected = 1` so two workers cannot process the same job.

Statuses: PENDING, PROCESSING, COMPLETED, FAILED, RETRYING, CANCELLED, PARTIALLY_COMPLETED, WAITING_FOR_MAPPING, CONFLICT.

WAITING_FOR_MAPPING and CONFLICT stay parked until an administrator maps or resolves, then Retry.

### Processing lease (crash recovery)

While a job runs it is `PROCESSING` with `LOCKEDBY` / `LOCKEDAT`. If the process is killed mid-job, that row would otherwise stay PROCESSING forever.

- Config: `Sync:ProcessingLeaseSeconds` (default **900**).
- Each worker poll runs reclaim: `PROCESSING` rows with `LOCKEDAT` older than the lease become `RETRYING` with `NEXTRUNAT=now` and error text `Reclaimed: processing lease expired`.
- After reclaim, the next claim can pick the job up again. Document transfers still reconcile before creating duplicates.

**Symptoms of stuck PROCESSING:** Push → Jobs shows PROCESSING with rising age; dashboard Processing count never drops; `/health` `oldestProcessingAgeSec` grows past the lease.

**Manual unlock:** On Push → Jobs, use **Unlock** (PROCESSING → RETRYING) or **Cancel** (PROCESSING → CANCELLED). Prefer Unlock when you want the worker to retry; use Cancel to abandon.

**After Windows service restart:** Pending/Retrying jobs resume on the next poll. Stale PROCESSING jobs resume after lease expiry (or immediately via Unlock).

## Health check

`GET /health` (anonymous) returns:

```json
{
  "status": "ok",
  "queue": {
    "pending": 0,
    "processing": 0,
    "failed": 0,
    "oldestProcessingAgeSec": null
  }
}
```

`status` is `degraded` when the oldest PROCESSING lock is older than twice the configured lease (ops signal that reclaim may be failing or a job is extremely long-running).

## Retry

Retry never inserts blindly. Order:

1. If `SYNC_TXN` already has a destination `DOCKEY`, mark COMPLETED.
2. Read the destination database (read-only) for a document stamped with the TXN id.
3. Read destination `ST_XTRANS` for a transfer from the source document.
4. Only then call the SQL Accounting API.

Failed jobs use exponential backoff with jitter. After `MAXATTEMPTS`, status becomes FAILED for human review.

Transaction Retry on Logs reconciles first; stale COMPLETED rows (dest missing) are cleared and re-queued.

## Dry run

Dry run uses `DryRunWriteAdapter`. It runs validation and mapping with no HTTP POST to `api.sql.my` and no Firebird writes.

## Conflicts

`SYNC_MAPPING` unique keys:

- one Master code per Sub + entity
- one Sub code per Master + entity

A unique violation becomes CONFLICT. Administrators resolve it in Mapping; the system never overwrites.

## Credentials

Store `env:VAR`, `sm:secret-id`, or `dpapi:filename` in `FBCREDREF` / `APICREDREF`. Passwords never go to the browser, audit JSON, or application logs (connection strings are redacted).

## Rollback

Redeploying a previous app binary does **not** roll back company documents already created via the API. Central DB restore and company backups are separate — see [BACKUP_AND_RECOVERY.md](BACKUP_AND_RECOVERY.md).

Design tradeoffs and deferred work: [SYSTEM_DESIGN_CHECKLIST.md](SYSTEM_DESIGN_CHECKLIST.md).
