# Operations guide

## Queue

Jobs live in `SYNC_JOB`. The in-process worker claims a row only when `STATUS` is PENDING or RETRYING and `NEXTRUNAT` is due. Claim uses `rows affected = 1` so two workers cannot process the same job.

Statuses: PENDING, PROCESSING, COMPLETED, FAILED, RETRYING, CANCELLED, PARTIALLY_COMPLETED, WAITING_FOR_MAPPING, CONFLICT.

WAITING_FOR_MAPPING and CONFLICT stay parked until an administrator maps or resolves, then Retry.

## Retry

Retry never inserts blindly. Order:

1. If `SYNC_TXN` already has a destination `DOCKEY`, mark COMPLETED.
2. Read the destination database (read-only) for a document stamped with the TXN id.
3. Read destination `ST_XTRANS` for a transfer from the source document.
4. Only then call the SQL Accounting API.

Failed jobs use exponential backoff with jitter. After `MAXATTEMPTS`, status becomes FAILED for human review.

## Dry run

Dry run uses `DryRunWriteAdapter`. It runs validation and mapping with no HTTP POST to `api.sql.my` and no Firebird writes.

## Conflicts

`SYNC_MAPPING` unique keys:

- one Master code per Sub + entity
- one Sub code per Master + entity

A unique violation becomes CONFLICT. Administrators resolve it in Mapping; the system never overwrites.

## Credentials

Store `env:VAR`, `sm:secret-id`, or `dpapi:filename` in `FBCREDREF` / `APICREDREF`. Passwords never go to the browser, audit JSON, or application logs (connection strings are redacted).
