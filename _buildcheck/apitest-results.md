# S2 SQL API diagnosis (TNT10006)

**Tenant:** TNT10006  
**API base:** `https://api.sql.my`  
**Credentials:** `proacc/tenant/TNT10006/sqlApiCredentials/S2`  
**Run date:** 2026-08-28  

## GET endpoints

| Endpoint | Status | Body type | Notes |
|----------|--------|-----------|-------|
| `/supplier?offset=0&limit=3` | 200 | JSON | 8 suppliers |
| `/stockitem?offset=0&limit=3` | 200 | JSON | 1 item (`Dogs`) |
| `/stockgroup?offset=0&limit=20` | 200 | JSON | `DEFAULT` group present |
| `/uom?offset=0&limit=20` | 200 | **HTML** | IntraWeb shell, not a JSON API |

Additional UOM candidate paths (`/stockuom`, `/unitofmeasure`) behave the same as `/uom` (HTML).

## POST /stockitem

| Test | Code | Status | Error |
|------|------|--------|-------|
| Minimal new item (`UNIT` UOM) | `SYNC-TEST-001` | 500 | `No corresponding master record found.` |
| Clone of existing `Dogs` with new code | `SYNC-CLONE-001` | 500 | `No corresponding master record found.` |
| Retry after stockgroup POST | `SYNC-TEST-001-B` | 500 | `No corresponding master record found.` |

Clone used the same `stockgroup`, `irbm_classification`, and `UNIT` base UOM shape as the only item already on S2.

## POST /stockgroup

| Test | Status | Error |
|------|--------|-------|
| `DEFAULT` (already exists) | 500 | PRIMARY/UNIQUE KEY on `ST_GROUP.CODE` |

## Conclusions

1. **Stock group is not the blocker** — `DEFAULT` is already on S2 via API and Firebird.
2. **UOM master API is unavailable** — no JSON list/create endpoint on this tenant.
3. **`POST /stockitem` create fails even when cloning an item that already exists on S2**, so the failure is not limited to HQ payload shape.
4. **Link-only sync works** — when the same item code already exists on S2, mapping can be confirmed without `POST /stockitem` (verified: Dogs job #93 COMPLETED).

## Firebird scan (HQ vs S2)

| Database | `ST_UOM` / `UOM` | `ST_ITEM_UOM` | `ST_GROUP` | Test items on `ST_ITEM` |
|----------|------------------|---------------|------------|-------------------------|
| HQ (`MASTER.FDB`) | **not present** | 7 rows (Chipmunks, Dogs, Duckies, Lions, Penguins, Rats, Turtle — all `UNIT`) | `DEFAULT` | Duckies, Turtle, Dogs exist |
| S2 (`sub2.fdb`) | **not present** | 1 row (`Dogs` / `UNIT`) | `DEFAULT` | Dogs only; Duckies/Turtle missing |

UOMs are per-item in `ST_ITEM_UOM`, not a global `ST_UOM` master table on either database.


- Enable JSON UOM API on tenant S2, or document how to satisfy UOM prerequisites for `POST /stockitem`.
- Investigate why `POST /stockitem` returns `No corresponding master record found` for minimal/clone payloads when `DEFAULT` stock group and `Dogs` reference data exist.
