# Document transfer test suite
Run: 2026-09-28 09:29 UTC

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
| 2 | HQ→S1 | DO_TO_GR | DO-00016 | GR-00014 | OK | COMPLETED | OK | **PASS** |
| 3 | HQ→S1 | ST_TO_ST | XF-00021 | XF-00020 | OK | COMPLETED | OK | **PASS** |
| 4 | HQ→S1 | CN_TO_PR | CN-00029 | SC-00015 | OK | COMPLETED | OK | **PASS** |
| 5 | HQ→S1 | CN_TO_DN | CN-00030 | DN-00022 | OK | COMPLETED | OK | **PASS** |
| 6 | HQ→S1 | DN_TO_CN | DN-00015 | CN-00026 | OK | COMPLETED | OK | **PASS** |
| 7 | HQ→S2 | SI_TO_PI | IV-00001 | PI-00005 | OK | COMPLETED | OK | **PASS** |
| 8 | HQ→S2 | DO_TO_GR | DO-00017 | GR-00024 | OK | COMPLETED | OK | **PASS** |
| 9 | HQ→S2 | ST_TO_ST | XF-00022 | XF-00023 | OK | COMPLETED | OK | **PASS** |
| 10 | HQ→S2 | CN_TO_PR | CN-00031 | SC-00027 | OK | COMPLETED | OK | **PASS** |
| 11 | HQ→S2 | CN_TO_DN | CN-00032 | DN-00021 | OK | COMPLETED | OK | **PASS** |
| 12 | HQ→S2 | DN_TO_CN | DN-00016 | CN-00026 | OK | COMPLETED | OK | **PASS** |
| 13 | S1→S2 | SI_TO_PI | IV-00001 | PI-00006 | OK | COMPLETED | OK | **PASS** |
| 14 | S1→S2 | DO_TO_GR | DO-00011 | GR-00025 | OK | COMPLETED | OK | **PASS** |
| 15 | S1→S2 | ST_TO_ST | XF-00022 | XF-00025 | OK | COMPLETED | OK | **PASS** |
| 16 | S1→S2 | CN_TO_PR | CN-00028 | SC-00028 | OK | COMPLETED | OK | **PASS** |
| 17 | S1→S2 | CN_TO_DN | CN-00029 | DN-00022 | OK | COMPLETED | OK | **PASS** |
| 18 | S1→S2 | DN_TO_CN | DN-00023 | CN-00027 | OK | COMPLETED | OK | **PASS** |

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
- Source doc: DO-00016
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DO DO-00016
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260928-000128 — Transaction queued for execution.
- Transfer: COMPLETED dest=GR-00014 (29) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Cats qty=1

### HQ→S1 · ST_TO_ST
- Create ST => 200
- Source doc: XF-00021
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: ST XF-00021
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 10 (SYNC-FROM → SYNC-TO)
- Enqueue: TXN-20260928-000129 — Transaction queued for execution.
- Transfer: COMPLETED dest=XF-00020 (40) err=
- **PASS** re-read OK — party= lines=1 item=Cats qty=10

### HQ→S1 · CN_TO_PR
- Create CN via /salescreditnote => 200
- Source doc: CN-00029
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00029
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260928-000130 — Transaction queued for execution.
- Transfer: COMPLETED dest=SC-00015 (30) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Cats qty=1

### HQ→S1 · CN_TO_DN
- Create CN via /salescreditnote => 200
- Source doc: CN-00030
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00030
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260928-000131 — Transaction queued for execution.
- Transfer: COMPLETED dest=DN-00022 (44) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Cats qty=1

### HQ→S1 · DN_TO_CN
- Create DN via /salesdebitnote => 200
- Source doc: DN-00015
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DN DN-00015
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Cats qty 1
- Enqueue: TXN-20260928-000132 — Transaction queued for execution.
- Transfer: COMPLETED dest=CN-00026 (52) err=
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
- Source doc: DO-00017
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DO DO-00017
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000133 — Transaction queued for execution.
- Transfer: COMPLETED dest=GR-00024 (49) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Dogs qty=1

### HQ→S2 · ST_TO_ST
- Create ST => 200
- Source doc: XF-00022
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: ST XF-00022
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 10 (SYNC-FROM → SYNC-TO)
- Enqueue: TXN-20260928-000134 — Transaction queued for execution.
- Transfer: COMPLETED dest=XF-00023 (46) err=
- **PASS** re-read OK — party= lines=1 item=Dogs qty=10

### HQ→S2 · CN_TO_PR
- Create CN via /salescreditnote => 200
- Source doc: CN-00031
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00031
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000135 — Transaction queued for execution.
- Transfer: COMPLETED dest=SC-00027 (54) err=
- **PASS** re-read OK — party=SYNC-GR-SU lines=1 item=Dogs qty=1

### HQ→S2 · CN_TO_DN
- Create CN via /salescreditnote => 200
- Source doc: CN-00032
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00032
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000136 — Transaction queued for execution.
- Transfer: COMPLETED dest=DN-00021 (42) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Dogs qty=1

### HQ→S2 · DN_TO_CN
- Create DN via /salesdebitnote => 200
- Source doc: DN-00016
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DN DN-00016
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000137 — Transaction queued for execution.
- Transfer: COMPLETED dest=CN-00026 (52) err=
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
- Source doc: DO-00011
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DO DO-00011
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000138 — Transaction queued for execution.
- Transfer: COMPLETED dest=GR-00025 (51) err=
- **PASS** re-read OK — party=400-H0001 lines=1 item=Dogs qty=1

### S1→S2 · ST_TO_ST
- Create ST => 200
- Source doc: XF-00022
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: ST XF-00022
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 10 (SYNC-FROM → SYNC-TO)
- Enqueue: TXN-20260928-000139 — Transaction queued for execution.
- Transfer: COMPLETED dest=XF-00025 (50) err=
- **PASS** re-read OK — party= lines=1 item=Dogs qty=10

### S1→S2 · CN_TO_PR
- Create CN via /salescreditnote => 200
- Source doc: CN-00028
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00028
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000140 — Transaction queued for execution.
- Transfer: COMPLETED dest=SC-00028 (56) err=
- **PASS** re-read OK — party=400-H0001 lines=1 item=Dogs qty=1

### S1→S2 · CN_TO_DN
- Create CN via /salescreditnote => 200
- Source doc: CN-00029
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: CN CN-00029
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000141 — Transaction queued for execution.
- Transfer: COMPLETED dest=DN-00022 (44) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Dogs qty=1

### S1→S2 · DN_TO_CN
- Create DN via /salesdebitnote => 200
- Source doc: DN-00023
- Dry OK Source database: OK
- Dry OK Destination database: OK
- Dry OK Source document: DN DN-00023
- Dry OK Mappings and fields: OK
- Dry OK Item Dogs: → Dogs qty 1
- Enqueue: TXN-20260928-000142 — Transaction queued for execution.
- Transfer: COMPLETED dest=CN-00027 (54) err=
- **PASS** re-read OK — party=300-C0001 lines=1 item=Dogs qty=1
