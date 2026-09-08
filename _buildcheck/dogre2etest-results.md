# DO → GR E2E (HQ → Sub)
Run: 2026-08-28 09:36 UTC

## HQ delivery order
- GET /deliveryorder => 200
- Using existing DO: DO-00001

## S1 supplier SYNC-GR-SUP: POST => 200 {"code":"SYNC-GR-SU","controlaccount":"400-000","companyname":"GR test supplier (S1)","companyname2":null,"companycategory":"----","area":"----","agent":"----","biznature":null,"creditterm":"30 Days","creditlimit":"30000","overduelimit":"0","statementtype":"O","currencycode":"----","outstanding":"0","allowexceedcreditlimit":true,"addpdctocrlimit":true,"agingon":"I","pricetag":null,"creationdate":"...
## S2 supplier SYNC-GR-SUP: POST => 200 {"code":"SYNC-GR-SU","controlaccount":"400-000","companyname":"GR test supplier (S2)","companyname2":null,"companycategory":"----","area":"----","agent":"----","biznature":null,"creditterm":"30 Days","creditlimit":"30000","overduelimit":"0","statementtype":"O","currencycode":"----","outstanding":"0","allowexceedcreditlimit":true,"addpdctocrlimit":true,"agingon":"I","pricetag":null,"creationdate":"...
## Supplier mapping 300-C0001->SYNC-GR-SUP on dest 7: OK Mapping confirmed.
## Supplier mapping 300-C0001->SYNC-GR-SUP on dest 8: OK Mapping confirmed.
## S1 dry-run (DO-00001)
- OK Source database: OK
- OK Destination database: OK
- OK Source document: DO DO-00001
- OK Mappings and fields: OK
- OK Item Dogs: → Cats qty 1
Summary: Would create GR on Sub Database 1 from DO-00001. No data was modified.

## S1 transfer
- Retry: txnPk=5 txnId=TXN-20260828-000003
- Status: COMPLETED dest=GR-00002 err=SQL Accounting API returned 500: {"error":{"message":"Invalid Company Code \"300-C0001\"."}}

## S2 dry-run (DO-00001)
- OK Source database: OK
- OK Destination database: OK
- OK Source document: DO DO-00001
- OK Mappings and fields: OK
- OK Item Dogs: → Dogs qty 1
Summary: Would create GR on Sub 2 from DO-00001. No data was modified.

## S2 transfer
- Retry: txnPk=6 txnId=TXN-20260828-000004
- Status: COMPLETED dest=GR-00002 err=SQL Accounting API returned 500: {"error":{"message":"Invalid Company Code \"300-C0001\"."}}
