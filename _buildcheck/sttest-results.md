# Stock transfer S1 → S2
Run: 2026-09-10 02:08 UTC

Source: Sub Database 1 (id=7, code=S1)
Dest:   Sub 2 (id=8, code=S2)

- Location on S1 (SYNC-FROM): exists
- Location on S1 (SYNC-TO): exists
- Location on S2 (SYNC-FROM): exists
- Location on S2 (SYNC-TO): exists
S1 locations: ----, SYNC-FROM, SYNC-TO
S2 locations: ----, SYNC-FROM, SYNC-TO
Using locations: SYNC-FROM → SYNC-TO
- Warehouse map SYNC-FROM→SYNC-FROM: already OK
- Warehouse map SYNC-TO→SYNC-TO: already OK
- Item map Dogs→Dogs: already OK

- GET /stocktransfer => 200
- Using existing ST on S1: XF-00001
## Dry run XF-00001
- OK Source database: OK
- OK Destination database: OK
- OK Source document: ST XF-00001
- OK Mappings and fields: OK
- OK Item Dogs: → Dogs qty 2 (SYNC-FROM → SYNC-TO)
Summary: Would create ST on Sub 2 from XF-00001. No data was modified.

- Qty override seq 1000: 2 → 1
## Transfer
- Enqueue: txnPk=11 txnId=TXN-20260910-000009 — Transaction queued for execution.
- Status: COMPLETED
- Dest doc: XF-00001 (1)
- Error: 
  · Started: SUCCESS Execute
  · Validation: SUCCESS Passed
  · Mapping: SUCCESS All required mappings found.
  · Started: SUCCESS Execute
  · Validation: SUCCESS Passed
  · Mapping: SUCCESS All required mappings found.
  · Created destination: SUCCESS ST XF-00001 (1)
  · Completed: SUCCESS 