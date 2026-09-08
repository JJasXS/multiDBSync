# DO → GR API smoke test
Run: 2026-08-28 09:37 UTC

## S1
- GET /goodsreceived => 200
  {"pagination":{"offset":0,"limit":50,"count":2},"data":[{"dockey":1,"docno":"GR-00001","docnoex":null,"docdate":"2026-08-28","postdate":"2026-08-28","taxdate":"2026-08-28","code":"400-H0001","companyname":"400-H0001","address1":null,"address2":null,"address3":null,"address4":null,"postcode":null,"city":null,"state":null,"country":"MY","phone1":null,"mobile":null,"fax1":null,"attention":null,"area":"----","agent":"----","project":"----","terms":"30 Days","currencycode":"----","currencyrate":"1","shipper":"----","description":"SYNC-GR-S1-092827","cancelled":false,"status":0,"docamt":"0","localdo...
- First supplier: (none)
- First item: Cats
- POST /goodsreceived: SKIPPED (need supplier + item on sub)

## S2
- GET /goodsreceived => 200
  {"pagination":{"offset":0,"limit":50,"count":2},"data":[{"dockey":1,"docno":"GR-00001","docnoex":null,"docdate":"2026-08-28","postdate":"2026-08-28","taxdate":"2026-08-28","code":"400-H0001","companyname":"400-H0001","address1":null,"address2":null,"address3":null,"address4":null,"postcode":null,"city":null,"state":null,"country":"MY","phone1":null,"mobile":null,"fax1":null,"attention":null,"area":"----","agent":"----","project":"----","terms":"30 Days","currencycode":"----","currencyrate":"1","shipper":"----","description":"SYNC-GR-S2-092833","cancelled":false,"status":0,"docamt":"0","localdo...
- First supplier: 400-H0001
- First item: Dogs
- POST /goodsreceived (SYNC-GR-S2-093734) => 200
  {"dockey":7,"docno":"GR-00003","docnoex":null,"docdate":"2026-08-28","postdate":"2026-08-28","taxdate":"2026-08-28","code":"400-H0001","companyname":"400-H0001","address1":null,"address2":null,"address3":null,"address4":null,"postcode":null,"city":null,"state":null,"country":"MY","phone1":null,"mobile":null,"fax1":null,"attention":null,"area":"----","agent":"----","project":"----","terms":"30 Days","currencycode":"----","currencyrate":"1","shipper":"----","description":"SYNC-GR-S2-093734","cancelled":false,"status":0,"docamt":"0","localdocamt":"0","landingcost1":"0","landingcost2":"0","localto...
