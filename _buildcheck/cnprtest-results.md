# CN → PR transfer S1 → S2
Run: 2026-09-10 02:31 UTC

Source: Sub Database 1 (id=7, code=S1)
Dest:   Sub 2 (id=8, code=S2)

- Type CN_TO_PR: srcDoc=CN dstDoc=PR dstPath=/purchasereturned active=True
- Type SR_TO_PR still active: False

Using customer: 300-C0001
Using dest supplier: 400-H0001
- Customer→Supplier map 300-C0001→400-H0001: already OK
- Item map Dogs→Dogs: already OK

- GET /salescreditnote => 200
- POST /salescreditnote (300-C0001/Dogs) => 200
- Created CN: CN-00003
## Dry run CN-00003
- OK Source database: OK
- OK Destination database: OK
- OK Source document: CN CN-00003
- OK Mappings and fields: OK
- OK Item Dogs: → Dogs qty 1
Summary: Would create PR on Sub 2 from CN-00003. No data was modified.

- Firebird read CN CN-00003: OK party=300-C0001 lines=1
## Transfer
- Enqueue: txnPk=16 txnId=TXN-20260910-000014 — Transaction queued for execution.
- Status: COMPLETED
- Dest doc: SC-00004 (8)
- Error: 
  · Created: SUCCESS Queued from CN CN-00003
  · Started: SUCCESS Execute
  · Validation: SUCCESS Passed
  · Mapping: SUCCESS All required mappings found.
  · Created destination: SUCCESS PR SC-00004 (8)
  · Completed: SUCCESS 