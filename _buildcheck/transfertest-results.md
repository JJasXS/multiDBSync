# Document transfer test suite
Run: 2026-09-10 03:06 UTC

Covers every Type dropdown option on **Master → Sub** (HQ→S1, HQ→S2) and **Sub → Sub** (S1→S2).
Each case: create source → dry-run → transfer → re-read destination from Firebird.


| DB | Id | Code | Role |
|----|----|------|------|
| Headquarter MY | 6 | HQ | MASTER |
| Sub Database 1 | 7 | S1 | SUB |
| Sub 2 | 8 | S2 | SUB |

- Supplier on S1 (SYNC-XFER-SU): POST => 500
- Supplier on S2 (SYNC-XFER-SU): POST => 500

## Summary

| # | Link | Type | Source | Dest | Dry | Transfer | Re-read | Result |
|---|------|------|--------|------|-----|----------|---------|--------|
| 1 | HQ→S1 | SI_TO_PI | IV-00001 | PI-00002 | OK | COMPLETED | OK | **PASS** |
| 2 | HQ→S1 | DO_TO_GR | DO-00014 | GR-00013 | OK | COMPLETED | OK | **PASS** |
| 3 | HQ→S1 | ST_TO_ST | XF-00013 | XF-00017 | OK | COMPLETED | OK | **PASS** |
| 4 | HQ→S1 | CN_TO_PR | CN-00025 | SC-00011 | OK | COMPLETED | OK | **PASS** |
| 5 | HQ→S1 | CN_TO_DN | CN-00026 | DN-00018 | OK | COMPLETED | OK | **PASS** |
| 6 | HQ→S1 | DN_TO_CN | DN-00013 | CN-00023 | OK | COMPLETED | OK | **PASS** |
| 7 | HQ→S2 | SI_TO_PI | IV-00001 | PI-00005 | OK | COMPLETED | OK | **PASS** |
| 8 | HQ→S2 | DO_TO_GR | DO-00015 | GR-00021 | OK | COMPLETED | OK | **PASS** |
| 9 | HQ→S2 | ST_TO_ST | XF-00014 | XF-00020 | OK | COMPLETED | OK | **PASS** |
| 10 | HQ→S2 | CN_TO_PR | CN-00027 | SC-00023 | OK | COMPLETED | OK | **PASS** |
| 11 | HQ→S2 | CN_TO_DN | CN-00028 | DN-00018 | OK | COMPLETED | OK | **PASS** |
| 12 | HQ→S2 | DN_TO_CN | DN-00014 | CN-00023 | OK | COMPLETED | OK | **PASS** |
| 13 | S1→S2 | SI_TO_PI | IV-00001 | PI-00006 | OK | COMPLETED | OK | **PASS** |
| 14 | S1→S2 | DO_TO_GR | DO-00010 | GR-00023 | OK | COMPLETED | OK | **PASS** |
| 15 | S1→S2 | ST_TO_ST | XF-00019 | XF-00021 | OK | COMPLETED | OK | **PASS** |
| 16 | S1→S2 | CN_TO_PR | CN-00024 | SC-00025 | OK | COMPLETED | OK | **PASS** |
| 17 | S1→S2 | CN_TO_DN | CN-00025 | DN-00019 | OK | COMPLETED | OK | **PASS** |
| 18 | S1→S2 | DN_TO_CN | DN-00019 | CN-00025 | OK | COMPLETED | OK | **PASS** |

**Totals: 18/18 passed**

## Link HQ→S1

- Map CUSTOMER→SUPPLIER 300-C0001→SYNC-XFER-SU: FAIL This Sub record is already mapped to another Master record, or this Master record is already mapped. Administrator resolution is required.
- Map CUSTOMER→CUSTOMER 300-C0001→300-C0001: OK
- Map CUSTOMER→SUPPLIER 300-H0001→SYNC-GR-SU: OK
- Map CUSTOMER→CUSTOMER 300-H0001→300-H0001: OK
- Map CUSTOMER→SUPPLIER 300-W0001→SYNC-GR-SU: FAIL This Sub record is already mapped to another Master record, or this Master record is already mapped. Administrator resolution is required.
- Map CUSTOMER→CUSTOMER 300-W0001→300-W0001: OK
- Item map Dogs→Cats: OK

### HQ→S1 · SI_TO_PI
- Using Firebird-visible IV: IV-00001 party=300-H0001
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: IV IV-00001
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260910-000060 — This document was already synchronized. No duplicate will be created.
- Transfer: COMPLETED dest=PI-00002 (4) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Cats qty=1

### HQ→S1 · DO_TO_GR
- Create DO via /deliveryorder => 200
- Source doc: DO-00014
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DO DO-00014
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260910-000107 — Transaction queued for execution.
- Transfer: COMPLETED dest=GR-00013 (27) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Cats qty=1

### HQ→S1 · ST_TO_ST
- Create ST => 200
- Source doc: XF-00013
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: ST XF-00013
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1 (SYNC-FROM → SYNC-TO)
- Enqueue: TXN-20260910-000108 — Transaction queued for execution.
- Transfer: COMPLETED dest=XF-00017 (34) err=
- **PASS** re-read OK — party= lines=1 item=Cats qty=1

### HQ→S1 · CN_TO_PR
- Create CN via /salescreditnote => 200
- Source doc: CN-00025
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00025
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260910-000109 — Transaction queued for execution.
- Transfer: COMPLETED dest=SC-00011 (22) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Cats qty=1

### HQ→S1 · CN_TO_DN
- Create CN via /salescreditnote => 200
- Source doc: CN-00026
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00026
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260910-000110 — Transaction queued for execution.
- Transfer: COMPLETED dest=DN-00018 (36) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Cats qty=1

### HQ→S1 · DN_TO_CN
- Create DN via /salesdebitnote => 200
- Source doc: DN-00013
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DN DN-00013
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260910-000111 — Transaction queued for execution.
- Transfer: COMPLETED dest=CN-00023 (46) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Cats qty=1

## Link HQ→S2

- Map CUSTOMER→SUPPLIER 300-C0001→SYNC-XFER-SU: FAIL This Sub record is already mapped to another Master record, or this Master record is already mapped. Administrator resolution is required.
- Map CUSTOMER→CUSTOMER 300-C0001→300-C0001: OK
- Map CUSTOMER→SUPPLIER 300-H0001→SYNC-GR-SU: OK
- Map CUSTOMER→CUSTOMER 300-H0001→300-H0001: OK
- Map CUSTOMER→SUPPLIER 300-W0001→SYNC-GR-SU: FAIL This Sub record is already mapped to another Master record, or this Master record is already mapped. Administrator resolution is required.
- Map CUSTOMER→CUSTOMER 300-W0001→300-W0001: OK
- Item map Dogs→Dogs: OK

### HQ→S2 · SI_TO_PI
- Using Firebird-visible IV: IV-00001 party=300-H0001
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: IV IV-00001
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000066 — This document was already synchronized. No duplicate will be created.
- Transfer: COMPLETED dest=PI-00005 (10) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Dogs qty=1

### HQ→S2 · DO_TO_GR
- Create DO via /deliveryorder => 200
- Source doc: DO-00015
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DO DO-00015
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000112 — Transaction queued for execution.
- Transfer: COMPLETED dest=GR-00021 (43) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Dogs qty=1

### HQ→S2 · ST_TO_ST
- Create ST => 200
- Source doc: XF-00014
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: ST XF-00014
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1 (SYNC-FROM → SYNC-TO)
- Enqueue: TXN-20260910-000113 — Transaction queued for execution.
- Transfer: COMPLETED dest=XF-00020 (40) err=
- **PASS** re-read OK — party= lines=1 item=Dogs qty=1

### HQ→S2 · CN_TO_PR
- Create CN via /salescreditnote => 200
- Source doc: CN-00027
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00027
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000114 — Transaction queued for execution.
- Transfer: COMPLETED dest=SC-00023 (46) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Dogs qty=1

### HQ→S2 · CN_TO_DN
- Create CN via /salescreditnote => 200
- Source doc: CN-00028
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00028
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000115 — Transaction queued for execution.
- Transfer: COMPLETED dest=DN-00018 (36) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Dogs qty=1

### HQ→S2 · DN_TO_CN
- Create DN via /salesdebitnote => 200
- Source doc: DN-00014
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DN DN-00014
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000116 — Transaction queued for execution.
- Transfer: COMPLETED dest=CN-00023 (46) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Dogs qty=1

## Link S1→S2

- Map CUSTOMER→SUPPLIER 300-C0001→SYNC-XFER-SU: FAIL This Sub record is already mapped to another Master record, or this Master record is already mapped. Administrator resolution is required.
- Map CUSTOMER→CUSTOMER 300-C0001→300-C0001: OK
- Map CUSTOMER→SUPPLIER 300-H0001→SYNC-GR-SU: FAIL This Sub record is already mapped to another Master record, or this Master record is already mapped. Administrator resolution is required.
- Map CUSTOMER→CUSTOMER 300-H0001→300-H0001: OK
- Map CUSTOMER→SUPPLIER 300-W0001→SYNC-GR-SU: OK
- Map CUSTOMER→CUSTOMER 300-W0001→300-W0001: OK
- Item map Dogs→Dogs: OK

### S1→S2 · SI_TO_PI
- Using Firebird-visible IV: IV-00001 party=300-W0001
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: IV IV-00001
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260821-000001 — This document was already synchronized. No duplicate will be created.
- Transfer: COMPLETED dest=PI-00006 (12) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Dogs qty=1

### S1→S2 · DO_TO_GR
- Create DO via /deliveryorder => 200
- Source doc: DO-00010
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DO DO-00010
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000117 — Transaction queued for execution.
- Transfer: COMPLETED dest=GR-00023 (47) err=
- **PASS** re-read OK — party=400-H0001 lines=1 item=Dogs qty=1

### S1→S2 · ST_TO_ST
- Create ST => 200
- Source doc: XF-00019
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: ST XF-00019
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1 (SYNC-FROM → SYNC-TO)
- Enqueue: TXN-20260910-000118 — Transaction queued for execution.
- Transfer: COMPLETED dest=XF-00021 (42) err=
- **PASS** re-read OK — party= lines=1 item=Dogs qty=1

### S1→S2 · CN_TO_PR
- Create CN via /salescreditnote => 200
- Source doc: CN-00024
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00024
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000119 — Transaction queued for execution.
- Transfer: COMPLETED dest=SC-00025 (50) err=
- **PASS** re-read OK — party=400-H0001 lines=1 item=Dogs qty=1

### S1→S2 · CN_TO_DN
- Create CN via /salescreditnote => 200
- Source doc: CN-00025
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00025
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000120 — Transaction queued for execution.
- Transfer: COMPLETED dest=DN-00019 (38) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Dogs qty=1

### S1→S2 · DN_TO_CN
- Create DN via /salesdebitnote => 200
- Source doc: DN-00019
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DN DN-00019
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260910-000121 — Transaction queued for execution.
- Transfer: COMPLETED dest=CN-00025 (50) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Dogs qty=1
