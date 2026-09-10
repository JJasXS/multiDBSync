# CN ↔ DN transfer S1 → S2
Run: 2026-09-10 02:18 UTC

Source: Sub Database 1 (id=7, code=S1)
Dest:   Sub 2 (id=8, code=S2)

- Type CN_TO_DN: srcDoc=CN dstDoc=DN dstPath=/salesdebitnote active=True
- Type DN_TO_CN: srcDoc=DN dstDoc=CN dstPath=/salescreditnote active=True

Using customer: 300-C0001
- Customer map 300-C0001→300-C0001: OK Mapping confirmed.
- Item map Dogs→Dogs: already OK

- GET /salescreditnote => 400
  {"error":{"message":"No record found."}}
- POST /salescreditnote (300-C0001/Dogs) => 200
- Created: CN-00001
- GET /salesdebitnote => 400
  {"error":{"message":"No record found."}}
- POST /salesdebitnote (300-C0001/Dogs) => 200
- Created: DN-00001
## CN_TO_DN dry run CN-00001
- OK Source database: OK
- OK Destination database: OK
- OK Source document: CN CN-00001
- OK Mappings and fields: OK
- OK Item Dogs: → Dogs qty 1
Summary: Would create DN on Sub 2 from CN-00001. No data was modified.
- Firebird read CN CN-00001: OK party=300-C0001 lines=1
## CN_TO_DN transfer
- Enqueue: txnPk=12 txnId=TXN-20260910-000010 — Transaction queued for execution.
- Status: COMPLETED
- Dest doc: DN-00001 (1)
- Error: 
  · Started: SUCCESS Execute
  · Validation: SUCCESS Passed
  · Mapping: SUCCESS All required mappings found.
  · Started: SUCCESS Execute
  · Validation: SUCCESS Passed
  · Mapping: SUCCESS All required mappings found.
  · Created destination: SUCCESS DN DN-00001 (1)
  · Completed: SUCCESS 

## DN_TO_CN dry run DN-00001
- OK Source database: OK
- OK Destination database: OK
- OK Source document: DN DN-00001
- OK Mappings and fields: OK
- OK Item Dogs: → Dogs qty 1
Summary: Would create CN on Sub 2 from DN-00001. No data was modified.
- Firebird read DN DN-00001: OK party=300-C0001 lines=1
## DN_TO_CN transfer
- Enqueue: txnPk=13 txnId=TXN-20260910-000011 — Transaction queued for execution.
- Status: COMPLETED
- Dest doc: CN-00001 (1)
- Error: 
  · Started: SUCCESS Execute
  · Validation: SUCCESS Passed
  · Mapping: SUCCESS All required mappings found.
  · Started: SUCCESS Execute
  · Validation: SUCCESS Passed
  · Mapping: SUCCESS All required mappings found.
  · Created destination: SUCCESS CN CN-00001 (1)
  · Completed: SUCCESS 