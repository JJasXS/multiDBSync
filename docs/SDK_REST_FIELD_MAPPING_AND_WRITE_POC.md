# REST ↔ SDK field mapping + controlled Write POC plan

**Status:** Mapping prepared from confirmed COM DataSets + website REST payloads + official wiki/CHM samples.  
**Safety:** Mapping/doc only — no Create/Update/Delete/Save/PostData executed by following this document. Production defaults remain `Provider=Rest`, `AllowLiveCom=false`.  
**Companies:** MIRACLE / ONYAT are POC only. Production remains dynamic via `SYNC_DATABASE.FbPath`.

Evidence tags used below:

| Tag | Meaning |
|-----|---------|
| **COM-DS** | Live inspect confirmed BizObject + DataSet names |
| **COM-FLD** | Live COM `Fields` / `XMLFieldDefs` (run `--export-field-map`; not yet locked in-repo) |
| **WIKI** | Official SDK Live wiki sample for that BizObject |
| **CHM** | Local `SQLSDK.chm` generic BizObject lifecycle |
| **REST** | Website mapper / sql-api-samples |
| **FB-RO** | Firebird read path columns used by website (name candidates only — not COM proof) |
| **TI** | Text Import name only (not SDK Live PostData) |

Re-export COM fields anytime (metadata only):

```powershell
cd "D:\sql account sync\SqlAccountSync"
$env:SQLACC_SDK_PASSWORD = '***'
dotnet run --project tools/SqlAccSdkReadPoc -- `
  --live --inspect-targets-only --company miracle --user ADMIN `
  --authorize-lan `
  --authorized-target "\\192.168.100.201\Share\SQLACC.DCF|ACC-MIRACLE.FDB;\\192.168.100.201\Share\SQLACC.DCF|ACC-ONYAT.FDB" `
  --export-field-map "docs/_generated/SDK_FIELD_MAP_MIRACLE.md"
```

After export, tick **COM-FLD** rows by exact FieldName match (case-insensitive). Do **not** implement production adapter mappings until CUD samples are verified.

---

## 1. Exact REST ↔ SDK field mapping tables

### A. GL Account — REST `/account` ↔ `GL_Acc.MainDataSet`

**BizObject:** `GL_Acc` (**COM-DS**: MainDataSet). Wiki uses `GL_Acc` (mixed case).  
**REST Create** (`GlAccountMapper.ToApiPayload`): dockey=0, parent, code, description, description2, acctype, specialacctype, tax, cashflowtype, sic.  
**REST Update** (`ToUpdatePayload`): same + dest dockey.  
**REST Delete:** `DELETE /account/{code}` (adapter).  
**Website behavior to preserve:** Account Auto Push, Master→Sub parent-before-child order (`OrderCodesParentsFirst`), dest parent resolved by **parent code** → Sub dockey.

| REST JSON | Candidate SDK FieldName | Required? | Evidence | Notes |
|-----------|-------------------------|-----------|----------|-------|
| `dockey` | `DocKey` / `DOCKEY` | Create: send 0 / omit; Update: dest key | **WIKI** Select lists `Dockey`; **REST**; **FB-RO** | Update open key: DocKey vs Code — **eStream** |
| `parent` | `Parent` | Required for hierarchy (0 = root) | **WIKI** Select filter `Parent`; **REST**; **FB-RO** | Sub must use **dest** parent dockey, not Master dockey |
| `code` | `Code` / `CODE` | **Required** | **WIKI** Select; **REST**; **FB-RO** | |
| `description` | `Description` / `DESCRIPTION` | **Required** (website falls back to code) | **WIKI**; **REST**; **FB-RO** | |
| `description2` | `Description2` / `DESCRIPTION2` | Optional (empty string, never JSON null) | **REST**; **FB-RO** | Confirm **COM-FLD** |
| `acctype` | `AccType` / `ACCTYPE` | Required for chart classification in practice | **REST**; **FB-RO** | Confirm **COM-FLD** |
| `specialacctype` | `SpecialAccType` / `SPECIALACCTYPE` | Optional | **WIKI** Select filter; **REST**; **FB-RO** | DR/CR used in list scripts |
| `tax` | `Tax` / `TAX` | Optional | **REST**; **FB-RO** | Confirm **COM-FLD** |
| `cashflowtype` | `CashFlowType` / `CASHFLOWTYPE` | Optional (int) | **REST**; **FB-RO** | Confirm **COM-FLD** |
| `sic` | `SIC` / `Sic` | Optional | **REST**; **FB-RO** | Confirm **COM-FLD** |
| *(sync only)* `parentcode` | — | N/A on REST body | MasterRecord helper | Resolve → `parent` dockey on dest |
| *(read only)* `level`, `indent` | — | Not posted | Chart UI / FB-derived | Do not invent SDK writes |

**Create vs Update vs Delete differences**

| Op | REST | Candidate SDK pattern | Officially verified for `GL_Acc`? |
|----|------|----------------------|-----------------------------------|
| Create | POST `/account`, dockey=0 | Generic: `New` → set fields → `Save` → `Close` (**CHM** Agent/Terms) | **No** — wiki only **Select** |
| Update | PUT `/account/{code}` with dest dockey | Generic: `Params`+`Open`+`Edit`+`Save`+`Close` | **No** |
| Delete | DELETE `/account/{code}` | Generic: `Open`+`Delete`+`Close` | **No**; UI also blocks if children/`GL_TRANS` (**FB-RO** activity check) |

**Compatibility gaps:** No official New/Edit/Save/Delete sample for `GL_Acc`. Full **COM-FLD** list not locked in-repo until `--export-field-map`. Hierarchy must preserve parent-by-code remapping.

---

### B. Goods Received — REST `/goodsreceived` ↔ `PH_GR`

**BizObject:** `PH_GR` (**COM-DS**: MainDataSet, cdsDocDetail, cdsSerialNumber).  
**Official samples:** **TI** name only; Linking table omits PH_GR PostData. **Do not assume PH_PI fields.**  
**Analog (pattern only):** wiki **PH_PI** uses MainDataSet + cdsDocDetail + `Save` (not a separate `PostData` method name in C# — sample function is named PostData).

#### Header (`MainDataSet`) — REST → candidate

| REST JSON | Candidate SDK Field | Required for create? | Evidence | Gap |
|-----------|---------------------|----------------------|----------|-----|
| `docno` | `DocNo` | Often auto / `--SYNC--` style | **REST** template; wiki docs use DocNo | Allocation rules for GR unknown |
| `docdate` | `DocDate` | **Likely required** | **REST**; wiki PH_PI | Confirm **COM-FLD** |
| `postdate` | `PostDate` | **Likely required** | **REST**; wiki PH_PI | |
| `taxdate` | `TaxDate` | Optional / SST | **REST** | Confirm on PH_GR |
| `code` | `Code` | **Required** (supplier) | **REST**; wiki PH_PI | |
| `companyname` | `CompanyName` | Optional | **REST**; wiki PH_PI | |
| `description` | `Description` | Optional (default “Goods received”) | **REST** | |
| `area`/`agent`/`project`/`terms` | `Area`/`Agent`/`Project`/`Terms` | Soft-required (website defaults) | **REST** | |
| `currencycode`/`currencyrate` | `CurrencyCode`/`CurrencyRate` | Soft-required | **REST** | |
| `docamt`/`localdocamt` | `DocAmt`/`LocalDocAmt` | Often calculated | **REST** | May be computed on Save |
| `cancelled`/`status` | `Cancelled`/`Status` | Soft | **REST** | |
| address / delivery / e-Invoice fields | Same PascalCase names | Optional | **REST** CopyIfPresent | Confirm each via **COM-FLD** |
| `udf_txnid` / note stamp | `Note` or UDF | Sync stamp | **REST** | UDF may need XMLFieldDefs |
| `sdsdocdetail` | → `cdsDocDetail` rows | **Required** (≥1 line typical) | **REST**; **COM-DS** | |
| `sdsserialnumber` | → `cdsSerialNumber` | Optional | **REST**; **COM-DS** | |

#### Detail (`cdsDocDetail`) — REST → candidate

| REST JSON (`sdsdocdetail[]`) | Candidate SDK Field | Evidence | Gap |
|------------------------------|---------------------|----------|-----|
| `seq` | `Seq` | **REST**; wiki PH_PI | |
| `itemcode` | `ItemCode` | **REST**; wiki PH_PI | |
| `location` | `Location` | **REST** (default `----`) | |
| `batch`/`project` | `Batch`/`Project` | **REST** | |
| `description`/`description2` | `Description`/`Description2` | **REST** | |
| `receiveqty` | `ReceiveQty`? | **REST** GR-specific | **Not** in wiki PH_PI (uses `Qty` only) — **must confirm on PH_GR** |
| `qty` | `Qty` | **REST** + wiki PH_PI | Website sets both = same qty |
| `returnqty` | `ReturnQty`? | **REST** (“0”) | Confirm **COM-FLD** |
| `uom` | `UOM` | **REST**; wiki PH_PI | |
| `rate`/`sqty`/`suomqty` | `Rate`/`SQty`/`SUOMQty`? | **REST** | Confirm |
| `unitprice`/`amount`/`disc`/`tax` | `UnitPrice`/`Amount`/`DISC`/`Tax` | **REST**; wiki PH_PI | |
| `taxinclusive`/`taxamt` | `TaxInclusive`/`TaxAmt` | **REST**; wiki PH_PI | |
| transfer/from keys | `FromDocType`/`FromDocKey`/`FromDtlKey`? | DO→GR business need | **Unverified** — critical for transfer parity |

**DO → GR:** Website posts a translated GR document via `TransactionEngine` → `/goodsreceived`. Official wiki has **DO → SL_IV** transfer samples, **not** DO → PH_GR. Transfer link fields remain an **eStream** blocker.

---

### C. Stock Group — REST `/stockgroup` ↔ `ST_GROUP.MainDataSet`

**BizObject:** `ST_GROUP` (**COM-DS**: MainDataSet). Linking/Text docs: Maintain Stock Group.  
**`ST_CATEGORY`:** separate object (**COM-DS**). **Not** the `/stockgroup` replacement.  
**Website:** `ItemPrerequisiteService` POST `/stockgroup` when group missing before item push — must keep behavior (via SDK later, or pre-seed groups).

| REST JSON | Candidate SDK Field | Required? | Evidence |
|-----------|---------------------|-----------|----------|
| `code` | `Code` / `CODE` | **Required** | **REST**; **FB-RO** `ST_GROUP` |
| `description` | `Description` | **Required** (fallback to code) | **REST**; **FB-RO** |
| `sales` | `Sales` | Soft / may be required by validation | **REST**; **FB-RO** |
| `salesreturned` | `SalesReturned` | Soft | **REST**; **FB-RO** |
| `cashsales` | `CashSales` | Soft | **REST**; **FB-RO** |
| `purchase` | `Purchase` | Soft | **REST**; **FB-RO** |
| `cashpurchase` | `CashPurchase` | Soft | **REST**; **FB-RO** |
| `purchasereturned` | `PurchaseReturned` | Soft | **REST**; **FB-RO** |
| `balancestock` | `BalanceStock` | Soft | **REST**; **FB-RO** |
| `costingmethod` | `CostingMethod` | Soft (default 1) | **REST**; **FB-RO** |
| `isactive` | `IsActive` | Soft (default true / `T`) | **REST**; **FB-RO**; wiki Terms uses `T`/`F` |
| `lastmodified` | `LastModified` | REST sends 0 | Epoch per Linking docs — usually system-owned |

**CUD for ST_GROUP:** **Not** officially sampled (Agent/Terms pattern is generic only).

---

### D. UOM — REST `/uom` ↔ `ST_ITEM.cdsUOM` (and optional standalone)

| Path | Role | Evidence |
|------|------|----------|
| REST `GET/POST /uom` (also `/stockuom`, `/unitofmeasure`) | Standalone UOM master | **REST** `ItemPrerequisiteService` — often HTML / unavailable |
| `ST_ITEM` + `cdsUOM` | Item-level UOM rows | **COM-DS**; **WIKI** ST_ITEM Add/Edit |
| Existing `SqlAccBizObjectWriter.UpsertStockItem` | Writes `UOM`, `Rate`, `RefCost`, `RefPrice`, `ISBASE` on cdsUOM | **WIKI**-aligned implementation |

| REST / item payload | SDK cdsUOM Field | Evidence |
|---------------------|------------------|----------|
| UOM `code` / item `uom` | `UOM` | **WIKI**; writer |
| rate | `Rate` | **WIKI**; writer |
| refcost / refprice | `RefCost` / `RefPrice` | **WIKI**; writer |
| isbase | `ISBASE` | **WIKI**; writer (edit path) |
| standalone `description`/`isactive` | N/A on cdsUOM | Only if standalone UOM BizObject exists (**COM** UOM candidate from inspect) |

**When standalone UOM still needed:** Only if eStream validates UOM master before accepting cdsUOM codes that are new company-wide. Current REST already degrades when `/uom` is HTML and relies on item POST. **Do not remove** REST UOM attempts until SDK lab proves cdsUOM-only parity.

**Stock group prerequisite remains:** wiki ST_ITEM sets `STOCKGROUP` (e.g. `DEFAULT`) — group must exist (`ST_GROUP` CUD or pre-seed).

---

## 2. Confirmed required vs optional (practical)

| Operation | Must have before Save | Soft / defaults | Unknown until vendor/COM-FLD |
|-----------|----------------------|-----------------|------------------------------|
| GL create | Code, Description, Parent (dest dockey) | description2="", tax="", sic=""; acctype often set | AccType/SpecialAccType/CashFlowType validation rules |
| GL update | Dest DocKey/Code + same fields | | Params key name |
| GL delete | Code with no children / no activity | | Delete method support |
| GR create | Supplier Code, DocDate/PostDate, ≥1 detail ItemCode+Qty/UOM | Area/Agent/Project defaults | ReceiveQty vs Qty; DO link fields; DocNo rules |
| Stock group | Code, Description | Account links + costingmethod=1 | Which GL links mandatory |
| Item+UOM | Code, Description, STOCKGROUP, ≥1 cdsUOM | STOCKCONTROL/ISACTIVE `T` | Standalone UOM master |

---

## 3. Field compatibility gaps (blockers)

1. **GL_Acc CUD** — Select-only official sample; no New/Edit/Save/Delete sample.  
2. **PH_GR create / DO→GR** — Object+DataSets exist; no official PostData; ReceiveQty and transfer keys unverified; **PH_PI ≠ PH_GR**.  
3. **ST_GROUP CUD** — Object exists; FB-RO names align with REST; no New/Save sample.  
4. **ST_CATEGORY ≠ stock group** — do not map `/stockgroup` to ST_CATEGORY.  
5. **UOM master** — cdsUOM likely covers item push; standalone `/uom` parity unproven.  
6. **COM-FLD export** — run `--export-field-map` to lock exact FieldName/DataType before coding adapters.

---

## 4. BizObject write-method evidence

| Method | Official evidence | GL_Acc | PH_GR | ST_GROUP | ST_ITEM |
|--------|-------------------|--------|-------|----------|---------|
| `BizObjects.Find` | **CHM** / **WIKI** / **COM-DS** | Verified (inspect) | Verified | Verified | Verified (**WIKI**) |
| `DataSets.Find` / `Fields` / `XMLFieldDefs` | **CHM** application_objects4 | Verified metadata | Verified | Verified | Verified |
| `Select(...)` | **WIKI** GL_Acc list | **Officially verified** (read) | — | — | — |
| `New` / `Edit` / `Save` / `Close` | **CHM** Agent, SL_IV, GL_CB, AR_PM; **WIKI** Agent, Terms, AR_CUSTOMER, ST_ITEM, PH_PI… | **Generic COM only** | **Generic COM only** | **Generic COM only** | **Officially verified** |
| `Delete` | **WIKI** Agent / ST_ITEM / Customer | Unverified | Unverified | Unverified | **Officially verified** |
| Detail `Append`/`Edit`/`Post` | **WIKI** PH_PI, ST_ITEM cdsUOM | N/A | Shape matches; **unverified** | N/A | **Officially verified** (cdsUOM) |
| `FindKeyByRef` | **WIKI** many | Unverified for GL | Unverified (DocNo? DocNo;Code?) | Unverified (CODE) | **Verified** (CODE) |
| Document “PostData” | Wiki sample **function name**; actual API is New+Save | — | **Unverified** | — | — |

---

## 5. Controlled SDK Write POC plan (DO NOT EXECUTE yet)

### Prerequisites (approval gate)

- [ ] Explicit human approval for live CUD on **MIRACLE** (then optional ONYAT).  
- [ ] Restorable `.fbk`/`.zip` backup path exists (`--backup-path`); currently **NOT FOUND** on Share per migration doc.  
- [ ] `SQLACC_SDK_PASSWORD` available to the operator (not committed).  
- [ ] SQL Accounting UI available for visual verify.  
- [ ] Firebird RO verify queries allowed (SELECT only).  
- [ ] Production remains `Provider=Rest`, `AllowLiveCom=false`.  
- [ ] Unique POC codes only: prefix `SDKPOC` / DocNo `--SDKPOC-GR--`. Never overwrite live masters.  
- [ ] Cross-company check: after MIRACLE write, confirm ONYAT lacks the POC code (and vice versa).

### Isolation

| Item | Value |
|------|-------|
| POC companies | MIRACLE (`ACC-MIRACLE.FDB`), ONYAT (`ACC-ONYAT.FDB`) |
| DCF | `\\192.168.100.201\Share\SQLACC.DCF` |
| Production | All `SYNC_DATABASE` rows — **out of scope** for POC writes |
| Tooling | Extend `SqlAccSdkWritePoc` **after** vendor answers / COM-FLD export; keep Agent POC pattern (backup gate, no FB DML) |

### Test 1 — GL Account Create / Update / Delete

| Step | Action | Expected | Verify |
|------|--------|----------|--------|
| 1a | `Find("GL_Acc")` → `New` → set Parent=known dest parent dockey, Code=`SDKPOC-GL-01`, Description, AccType… → `Save` | Success or clear COM error | FB-RO `SELECT CODE,PARENT,ACCTYPE FROM GL_ACC WHERE CODE='SDKPOC-GL-01'`; UI Maintain Account |
| 1b | Open by Params → `Edit` → change Description → `Save` | Row updated | FB-RO description; UI |
| 1c | `Delete` unused POC account | Removed | FB-RO count=0; UI |
| Cleanup | Delete if orphaned; restore backup if Save corrupts chart | | Cross-check ONYAT has no `SDKPOC-GL-01` |

**Ready for lab?** **Conditional** — only after eStream confirms CUD **or** approved exploratory lab accepting rollback risk. Mapping candidates ready; method **unverified**.

### Test 2 — Goods Received Create

| Step | Action | Expected | Verify |
|------|--------|----------|--------|
| 2a | Ensure supplier + item + UOM exist on MIRACLE | Preconditions | FB-RO / UI |
| 2b | `Find("PH_GR")` → `New` → header + `cdsDocDetail.Append/Post` → `Save` | New GR or clear error | `PH_GR`/`PH_GRDTL` SELECT; UI GR list |
| 2c | (Separate) Attempt DO→GR transfer fields if vendor documents them | Linked outstanding qty | Transfer inquiry |
| Cleanup | Cancel/delete POC GR in UI if supported; else restore backup | | ONYAT isolation |

**Ready for lab?** **No** until ReceiveQty/transfer questions answered **or** explicit exploratory approval with backup.

### Test 3 — Stock Group Create

| Step | Action | Expected | Verify |
|------|--------|----------|--------|
| 3a | Copy account links from an existing group via FB-RO | Payload complete | |
| 3b | `Find("ST_GROUP")` → `New` → Code=`SDKPOC-GRP-01` → `Save` | Created | `SELECT * FROM ST_GROUP WHERE CODE='SDKPOC-GRP-01'`; UI |
| Cleanup | Delete if unused; else backup restore | | ONYAT isolation |

**Ready for lab?** **Conditional** — object+REST/FB name alignment strong; CUD sample missing.

### Test 4 — Item create with UOM prerequisites

| Step | Action | Expected | Verify |
|------|--------|----------|--------|
| 4a | Ensure `STOCKGROUP` exists (Test 3 or `DEFAULT`) | | |
| 4b | **Do not** create standalone UOM first | | |
| 4c | Use existing writer pattern: `ST_ITEM` + `cdsUOM` (`SDKPOC-ITM-01`, UOM=`SDKPOC`) | Item+UOM saved **or** error demanding UOM master | FB-RO `ST_ITEM` / `ST_ITEM_UOM`; UI |
| 4d | If 4c fails for missing UOM master, record error; ask eStream; only then probe standalone UOM BizObject | | |
| Cleanup | Delete item if unused | | ONYAT isolation |

**Ready for lab?** **Yes (narrow)** — ST_ITEM+cdsUOM officially sampled; group must exist; still needs backup + approval. Does **not** by itself retire `/stockgroup` REST.

### Rollback / recoverability

1. Prefer COM Delete of POC codes.  
2. If Delete fails → mark OrphanPossible; manual UI cleanup.  
3. If chart/docs inconsistent → restore from `--backup-path` before any further lab.  
4. Never run POC against MAINTEST / production FDBs.

---

## 6. Exact unresolved questions for eStream

See also `RestDependencyCatalog.VendorSupportQuestions` (kept in sync).

1. GL_Acc New/Edit/Save/Delete sample with Parent/Code/Description/AccType/SpecialAccType/Tax/CashFlowType/Sic?  
2. GL_Acc update Params key (DocKey vs Code) and delete rules with children/activity?  
3. PH_GR MainDataSet+cdsDocDetail create sample; Qty vs ReceiveQty; DocNo allocation?  
4. DO→GR transfer: supported SDK API and FROM* link fields? (PH_PI must not be reused blindly.)  
5. ST_GROUP New/Save sample including which GL account fields are mandatory? Confirm ST_CATEGORY ≠ stock group.  
6. Is ST_ITEM.cdsUOM enough for new UOM codes, or is Maintain UOM mandatory first?  
7. Windows Service identity Login/DCOM/UNC/TCP requirements for production host?

---

## 7. Lab-readiness verdict

| Operation | Object ready | Field map ready | Write method verified | Controlled lab testing |
|-----------|--------------|-----------------|----------------------|------------------------|
| GL Account CUD | Yes (`GL_Acc`) | Candidates + Select fields; await **COM-FLD** export | **No** | **Not ready** (exploratory only with backup + approval) |
| Goods Received create | Yes (`PH_GR` + detail sets) | REST mapped; ReceiveQty/transfer **gaps** | **No** | **Not ready** |
| Stock Group create | Yes (`ST_GROUP`) | REST↔FB-RO aligned; await **COM-FLD** | **No** | **Conditional** (backup + approval) |
| Item + cdsUOM | Yes (`ST_ITEM`) | **Yes** (wiki + writer) | **Yes** (wiki) | **Ready** for approved MIRACLE lab (group must exist) |
| Standalone UOM | Unknown / optional | REST only | **No** | **Not ready** — prove via Test 4 failure mode |
| Production Provider=Sdk | — | — | — | **Blocked** until above CUD labs pass |

**Bottom line:** Existence of `GL_Acc` / `PH_GR` / `ST_GROUP` removes “missing object” blockers. **API replacement remains blocked on official CUD samples** (GL, GR, Stock Group) and DO→GR transfer semantics. Next safe step: export **COM-FLD** map, obtain eStream answers, then run MIRACLE Write POC under backup gate — still without flipping production Provider.
