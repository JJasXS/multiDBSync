# SqlAccountSync — system design checklist review

Filled against [happysnaker/system-design-checklist](https://github.com/happysnaker/system-design-checklist) for architecture review and production readiness. Scope: this control plane only (not SQL Account itself).

## 1. Problem framing

| Prompt | Answer |
|--------|--------|
| Primary outcome | Operators sync master data and transfer documents between Master/Sub SQL Account company databases safely and with an audit trail. |
| In scope | Register DBs/links, map codes, enqueue master pushes and document transfers, dry-run, OTP auth, central `SYNC_*` state. |
| Out of scope | Replacing SQL Account UI, multi-region HA, public multi-tenant SaaS API, arbitrary Firebird writes into company files. |
| Latency / throughput / durability | Low interactive QPS; jobs are durable in central Firebird; eventual completion via worker. |
| Shape | Hybrid: online Razor UI + in-process async job worker. |
| Assumptions that can break | Single Windows host; SQL Accounting API available; central FDB reachable; one active worker process. |

## 2. Requirements and constraints

- **Functional:** Master→Sub account/item push; SI/DO/ST/CN/DN/PR-style transfers; mappings; logs/retry.
- **Non-functional:** No silent duplicates; company DBs read-only from this app; secrets not logged.
- **Availability:** Best-effort single host; ops restart service after crash.
- **Data-loss tolerance:** Central job/txn rows are source of truth for sync state; company docs are authoritative accounting data.
- **Audit / privacy:** `SYNC_AUDIT` / `SYNC_ERROR`; OTP email; role policies.
- **Cost / ownership:** Single-team Windows service + Firebird central DB.

## 3. Scale estimation

- Interactive RPS ≪ 10; worker polls every few seconds.
- Read-heavy browse of company DBs; writes are few API POSTs.
- Peak: batch enqueue of many codes/docs (still sequential claim).
- Central DB growth: jobs/txns/audit over months — retain via backup policy.
- No cross-region requirement today.

## 4. API and boundary design

- Boundaries: Razor UI → services → CentralDb / company Firebird (SELECT) / SQL Accounting REST.
- Idempotency: unique `IDEMKEY` on `SYNC_JOB` and `SYNC_TXN`; reopen stale COMPLETED when dest missing.
- Errors: user-facing Flash toasts + Logs; FirebirdErrorMapper for SQL errors.
- Ownership: company accounting data owned by SQL Account; sync orchestration owned by this app.

## 5. Data model and storage

- Central entities: `SYNC_DATABASE`, `SYNC_DBLINK`, `SYNC_JOB`, `SYNC_TXN`, mappings, rules, audit/errors.
- Access: list/filter by status; claim by priority + due time.
- Relational Firebird fits; schema via `SchemaBootstrap` / `SYNC_SCHEMA_VER`.

## 6. Read path design

- UI lists and document browse → `FirebirdReadAdapter` (read-only txn + `SqlGuard`).
- No CDN/app cache for accounting rows (correctness over cache).
- Degraded read: empty lists / connection status OFFLINE on registry.

## 7. Write path design

- Creates go through SQL Accounting API only (`SqlAccountApiWriteAdapter`); dry-run uses `DryRunWriteAdapter`.
- UI confirmation required for live document creates.
- Queue buffers work; retries with exponential backoff; max attempts → FAILED.
- Partial success: reconcile before retry; never blind re-insert when dest exists.

## 8. Async workflows, queues, and events

- In-process `SyncWorker` claims `SYNC_JOB`.
- Ordering: priority then job id; no cross-job ordering guarantee.
- Retry + jitter; terminal FAILED for human review (no separate DLQ table).
- **Lease reclaim:** stale `PROCESSING` (expired `LOCKEDAT`) → `RETRYING` so crash recovery works.
- Operators: Retry txn on Logs; Cancel/Unlock jobs on Push Jobs tab.

## 9. Consistency and correctness

- Strong for “do not double-create”: IDEMKEY + dest existence checks + reconcile.
- Mapping uniqueness → CONFLICT (admin resolve).
- Stale COMPLETED with missing dest is reopened rather than treated as success forever.

## 10. Reliability and resilience

| Failure | Behavior |
|---------|----------|
| API / network | Job RETRYING with backoff → FAILED |
| Process crash mid-job | Lease reclaim returns job to RETRYING |
| Central DB down | Worker idle; UI bootstrap warning |
| Company DB offline | Connection status + failed reads |

Deferred: circuit breakers, multi-instance workers, canary deploy.

## 11. Observability and operations

- Request id middleware; audit/error pages; dashboard counts.
- `GET /health` — queue pending/processing/failed + oldest processing age.
- No Prometheus/OTel yet (deferred).
- Runbook: [OPERATIONS.md](OPERATIONS.md), [BACKUP_AND_RECOVERY.md](BACKUP_AND_RECOVERY.md).

## 12. Security and abuse resistance

- Cookie auth + OTP; roles ADMIN→VIEWER; folder policies.
- Secrets: `env:` / `sm:` / `dpapi:`; credential redaction.
- Production connect gated by `AllowProductionConnections`.
- Rate limiting / abuse: not required at current private-LAN scale.

## 13. Delivery and rollout

- `dotnet run` / Windows service (`UseWindowsService`).
- Schema bootstrap on startup (forward-compatible DDL).
- Rollback: redeploy previous binaries; DB restore is separate (see backup doc).
- Validate: dry-run, transfertest harnesses, `dotnet test`.

## 14. Sign-off / deferred

- Simplest useful system: already this monolith.
- Most likely failure first: stuck jobs before lease reclaim; API/auth outages.
- Over-engineering avoided: no message bus, no multi-AZ.
- Painful in six months without attention: `FirebirdReadAdapter` / `SyncOrchestrator` size; missing SLOs.
- **Deferred:** god-file split, OTel/SLOs/alerts, canary scripts, multi-instance OTP/store, separate DLQ table.
