# Fabric estate migration: customer rehearsal runbook

**Status: DRAFT - IT readiness review. Edition 1.3, 1 October 2026.** This is a controlled export, import and validation procedure, **not deployment or cutover approval**. The toolkit does not close the installation guide's **R1-R4** delivery gaps. The actual working Data Agent configuration, finance data release, selected runtime dependencies and KPIpedia handover still require their owners' evidence.

Initial implementation validation used mocked/offline tool contracts. A **read-only source preflight on 30 September 2026** verified native Fabric CLI **1.7.0** authentication and workspace inventory, AzCopy **10.32.7** OneLake enumeration, and SQL metadata access. A **full source export and package-only integrity verification completed on 1 October 2026**: one Lakehouse with 13 tables, two notebooks, one Data Agent using its Preview definition route, and 50 physical OneLake files (37,934,147 bytes). Source-before/source-after checks passed, including 74,688 financial groups. The restricted export receipt retains the manifest hash, tool-script hashes and verification evidence.

**Target import, target readback, finance/security acceptance, publication and cutover have not been performed.** Complete the full export/import rehearsal, record the actual versions used and obtain the required acceptance before use. The export does not change source data or grant permissions.

**Receiving an existing export to reproduce the solution?** Start with the [recipient Fabric import guide](fabric-import-guide.md). It includes the complete handover checklist, target tenant/capacity/security configuration, portable workstation setup, the four-item reference map, import/readback commands and manual publication/application gates.

Related: [customer installation guide](customer-it-installation-guide.md#6-configure-fabric-and-install-the-data-catalogue), [handbook PDF](customer-it-installation-guide.pdf), [resolver/Search publication](resolver-data.md), [operations](operations.md).

## 1. What is and is not restored

The source configuration selects **workspaces, not a convenient subset of their items**. Inventory covers **all items** in each selected workspace. Every discovered Lakehouse needs an explicit SQL endpoint entry; listing one Lakehouse does not exclude its neighbours.

| Object or concern | Automatic scope and boundary |
| --- | --- |
| Lakehouse | Copy the full physical **Tables** and **Files** trees, preserving relative paths, **empty directories**, data files and all **retained Delta transaction logs/checkpoints**. This is not SQL row export, table reseeding or a copy of only the current Parquet files. Already vacuumed/deleted history cannot be recovered. |
| Notebook | Capture the original API-returned definition parts and create a new target item with reviewed rebinding. The wrapper does **not** use native CLI export or its notebook-output stripping. Preserve any stored cell outputs returned in the definition; this is not a backup of live sessions or job history. Import does not execute the notebook. |
| DataAgent | The core Data Agent service is **GA**; its source-control/definition-migration feature is a separate **Preview/experimental gate**, requiring explicit `-AllowPreviewDataAgent`. Use the typed public definition APIs through Fabric CLI, not native item export/import support that the reviewed CLI does not provide for DataAgent. Review actual instructions, examples, sources, runtime and publication separately. |
| Lakehouse-owned SQLEndpoint | Inventory as a generated dependency. The target Lakehouse generates its own SQL analytics endpoint; do **not** create an independent endpoint mapping or attempt to restore it as a standalone item. |
| Permissions and principals | Fabric security inventory covers **workspace role assignments only**; SQL snapshots separately inventory visible principals, permissions and policies. Item ACLs/sharing and OneLake security are **not fully inventoried**. This is not a complete security backup. Access is **never automatically granted**; target mapping and effective-access tests remain manual. |
| Sensitivity labels / governance | Not automatically restored. The security owner must separately capture required classification and verify target labels/governance before release. Definition/data restoration is not a backup of every tenant governance setting. |
| Unsupported item types | Block a full export under **this toolkit's bounded coverage policy**, not a claim that Microsoft has no migration support for them. Pipelines, Warehouses, semantic models, reports or other unsupported types need a reviewed type-specific extension/process before claiming complete estate migration. |
| Custom SQL objects | Inventory but block full export rather than omit DDL, dependencies or security behavior. Do not call a physical Delta copy a restore of SQL views/procedures/policies. |
| Custom workspace folders | Inventory through the **Preview List Folders API**. Any workspace folder, **including an empty folder**, blocks full export: this release cannot preserve the workspace's folder organization. Do not flatten it silently or confuse this limitation with physical OneLake directories, which are preserved. |
| Shortcuts and schedules | Shortcuts, Notebook `RunNotebook` schedules and Lakehouse **`TableMaintenance` schedules** block full export. A shortcut is not independently backed-up data; schedule ownership, time zones, destinations and re-enablement need review. Pausing execution does not resolve a schedule's migration requirement. |
| Out-of-Fabric dependencies | Azure AI Search, Foundry applications/models, Bots, Entra consent, KPIpedia and channel packages are **not** exported by these scripts. Rebuild/reconfigure them separately. |

An inventory with blockers is useful assessment evidence, not a partial backup suitable for import. Resolve blockers with the owners and rerun inventory into a new directory. Do not delete needed items, omit a workspace, edit a manifest or weaken checks merely to obtain a successful status.

### Zava table and layout contract

For each Zava Lakehouse, keep `requireZavaContract: true` in both configurations. The accepted migration baseline includes **13 actual, nonempty Delta tables**, not nine regenerated tables:

- Nine base tables: `dim_region`, `dim_department`, `dim_kpi`, `dim_date`, `dim_account`, `fact_finance_monthly`, `fact_kpi_monthly`, `fact_headcount_monthly`, `fact_gl_transaction`.
- Four resolver tables: `resolver_catalog`, `resolver_scope`, `resolver_calendar`, `resolver_release`.

Preserve **actual** catalogue versions, curated rows, release metadata and retained table history. Zava uses a **schema-disabled Lakehouse**, with direct OneLake paths `Tables/<table_name>` and SQL names such as `dbo.resolver_catalog`. Precreate the target with the same schema mode; `Tables/dbo/<table_name>` is not interchangeable. **`requireZavaContract: true` explicitly rejects schema-enabled source or target Lakehouses**, because the current publisher requires direct `Tables/<name>` paths. Do not disable this contract to bypass the check; a schema-enabled Zava estate needs a reviewed application/publisher migration first. An unactivated, empty `resolver_release` can be valid during seeding, but it is not an accepted Zava migration baseline.

The table names discovered from physical **`_delta_log` directories must exactly match the SQL-visible table set**, accounting for the Lakehouse schema mode. Missing physical logs or SQL tables block export. Wait for SQL synchronization and verify complete, unfiltered metadata visibility; matching a few expected counts does not resolve a missing table.

The toolkit also copies additional physical files/tables in the selected Lakehouses; thirteen is a required Zava contract, not an export filter. Demonstration counts and November 2025 figures are **not universal migration tests**. Reconcile against the approved source snapshot and actual source data contract.

## 2. Owners, tools and prerequisites

Assign a migration operator, source data owner, target Fabric administrator, finance approver, identity/security approver and application-release owner. Grant time-bounded permissions at the narrowest scopes that expose the **complete** selected estate.

| Prerequisite | Required evidence |
| --- | --- |
| Approved release | Reviewed toolkit sources, example configurations and existing `Resolver-Sql.ps1` dependency. Record release revision and hashes. The older `zavafinance-fabric-seeding.zip` is a resolver-seeding pack, **not** the estate migration toolkit or a source-data backup. |
| PowerShell / SQL client | PowerShell **7.6** and this release's **.NET 10** Microsoft.Data.SqlClient dependency layout. Use the existing approved Release build output or `-SqlClientDirectory`; no separate SQL module or private replacement SDK is required. Applies to migration for either application language. |
| Microsoft Fabric CLI | Install the public **`ms-fabric-cli`** distribution (`fab`) using the [official installation instructions](https://microsoft.github.io/fabric-cli/#install-the-cli). The reviewed contract is **v1.7.0**, with Python 3.10-3.13 in PATH. **The adapter accepts only v1.7.0 and rejects other versions**, including other 1.7.x patches, until a reviewed/tested contract update. Record `fab --version`; source review is not live certification. |
| AzCopy | Install the public utility from [Get started with AzCopy](https://learn.microsoft.com/azure/storage/common/storage-use-azcopy-v10). Review [OneLake-specific AzCopy guidance](https://learn.microsoft.com/fabric/onelake/onelake-azcopy). Record the selected binary version and provenance during rehearsal, not an invented tested pin. |
| User authentication | Sign in separately with **`fab auth login --tenant ...`** and **`az login --tenant ...`**. The wrapper checks **both** tenant contexts; Azure CLI supplies the existing SQL helper's delegated token. Each CLI handles its own native token refresh. This is **not an Azure CLI-to-Fabric token bridge**. |
| Source visibility | Complete selected workspace/item and OneLake visibility; SQL `SELECT` on all captured tables and database `VIEW DEFINITION`. RLS-filtered totals or hidden objects are not a complete baseline. An administrator must independently attest effective access, including item/OneLake security that the workspace-role inventory does not cover. |
| Target resources | Precreated workspaces and **empty** Lakehouses, compatible capacity/region and matching schema mode. No source workspace/Lakehouse as a destination; workspace and Lakehouse GUID mappings are one-to-one. |
| SQL endpoint binding | For **both source and target**, validate `sqlServer` against the provisioned endpoint hostname returned by the typed Lakehouse API. `sqlDatabase` must be that Lakehouse's actual **display name or SQL endpoint ID**, as checked against the API properties. A plausible hostname/database string is insufficient. Source/target schema mode and default schema must match. |
| Network / capacity | Operator access to Fabric control plane, OneLake HTTPS data plane, Entra and SQL TCP 1433; enough encrypted local capacity for a full export **and** full target readback, including retained historical data. Capacity may substantially exceed current table sizes. |
| Isolation | Target inaccessible to application traffic, ordinary users, writers, refreshes, schedules and other automations until the appropriate manual gates pass. `-TargetIsolated` acknowledges this operator control; it does not configure security. |

Record PowerShell, .NET/SqlClient, Azure CLI, `fab` and AzCopy versions in the restricted rehearsal evidence. Recheck current public documentation for Preview eligibility, definition formats, authentication and API support. A missing tool, unsupported response or unsupported feature is a stop condition, not permission to improvise an unreviewed raw API substitute.

### Windows sign-in troubleshooting

If `fab auth login` waits without a visible sign-in prompt in VS Code or a headless tool console:

1. Cancel older waiting login commands with **Ctrl+C**; keep only one sign-in attempt active.
2. Open **standalone Windows Terminal or PowerShell from the Start menu**, outside VS Code.
3. Run `fab auth login --tenant "<tenant-id>"`, select **Interactive with a web browser**, and press **Enter**. Use the same CLI installation and Windows user profile as the automation; invoke the full path to `fab.exe` if it is not on PATH.
4. Complete the account selection and any required MFA/consent. Depending on the authentication environment, the UI may be a browser or a Windows account picker.
5. Run `fab auth status` and verify **Logged in** and the expected tenant. Then repeat that check from the terminal or automation environment that will run the migration.

This resolved the missing sign-in UI during the read-only source preflight; the saved login subsequently passed the toolkit's noninteractive authentication check and a native Fabric API read. Signing into the Fabric website, including through the correct Edge profile, does **not** authenticate Fabric CLI. Do not copy browser credentials or inject access tokens to bypass the native login.

### Why all metadata and definitions use `fab api`

The CLI is **GA** ([v1.5 announcement](https://community.fabric.microsoft.com/blog/fbc_fabricupdatesblogs/fabric-cli-v1-5-is-here-generally-available/5172135)). Its public [v1.7.0 source baseline](https://github.com/microsoft/fabric-cli/tree/23a184482f740fccb6ef93315fefe0a8b9d20f1f) exposes a coverage gap for this migration: native export changes Notebook content by removing outputs; native import is name-addressed and can upsert/overwrite; and native item transfer lacks DataAgent support. Those semantics cannot satisfy an ID-controlled, fail-closed restore.

The reviewed SDK/CLI coverage choice is therefore the **public Fabric CLI `fab api` route for all ID-addressed metadata and definition operations**, including inventory and Notebook, Lakehouse and DataAgent definition requests. It calls documented Fabric APIs through the installed CLI, **not a handwritten HTTP transport or a private SDK**. AzCopy remains responsible for physical OneLake data; the existing public SqlClient helper remains responsible for SQL checks. Do not substitute native `fab export`/`fab import` or GUID-looking typed names.

- The wrapper explicitly validates process/CLI status and the documented **`result.data[0]`** API envelope containing `status_code`, `headers` and decoded `text`. It handles continuation pagination, HTTP **429** throttling and **202** long-running operations; a message-only success or incomplete list is not migration evidence. `fab ls` is not a complete item inventory.
- Original API-returned **`definition.parts`** are stored in each item's **`definition.json`**, retaining the opaque **`InlineBase64`** payloads and returned part paths rather than flattening them into native CLI export folders or assuming one filename. Text parts are strictly decoded as UTF-8 for secret detection and reviewed rebinding. Binary/non-text or unsupported payload parts **fail closed and require type-specific support**.
- For **`.json` and `.ipynb` parts**, canonical JSON normalization exposes Unicode-escaped IDs before reference discovery/remapping **without numeric precision loss**. Remapped JSON is validated during read-only preflight **before the first target write**, and again when staging the actual target definitions. This does not discover every external dependency or replace the owner's content/dependency approval.
- The source package remains immutable. Import stages rebound text separately and lets Fabric establish the new item's identity; source-control identity metadata is not a target runtime binding. Readback compares the reviewed target definition, not source and target identity fields blindly. The wrapper does not deliberately strip Notebook outputs returned by the API.
- The **Preview List Folders API** is used to detect all workspace-folder blockers, **including empty folders**, not to recreate folders. The [Data Agent source-control/ALM route](https://learn.microsoft.com/fabric/data-science/data-agent-source-control) has its own **Preview** approval gate although the core Data Agent service is GA.
- Use separate native user logins to Fabric CLI and Azure CLI; do not inject `FAB_TOKEN*` values as an Azure CLI authentication bridge. Each CLI manages its own session and refresh. Never log tokens. An authentication failure or interrupted mutation still requires remote-state inspection and the fail-closed retry procedure.

The adapter's exact version boundary is **v1.7.0**, originally selected by source review and subsequently exercised for read-only source inventory. That preflight does not certify definition transfer, import or complete data restoration. The adapter rejects other versions; use v1.7.0 or obtain a reviewed/tested adapter-contract update, never disable the guard. The full customer rehearsal must exercise API failure paths as well as a complete data transfer.

### Offline contract checks

Run **all four** suites from the reviewed release root and retain each result:

```powershell
.\tools\test-fabric-estate.ps1
.\tools\test-fabric-estate-fabric.ps1
.\tools\test-fabric-estate-workflow.ps1
.\tools\test-fabric-estate-sql.ps1
```

These exercise offline/mocked contracts. Passing them does not establish that Fabric CLI or AzCopy is installed, that the selected live endpoints are compatible, or that a real migration, permission grant, publication or activation succeeded.

### Data custody and trust

Use a customer-approved **encrypted transfer volume outside every Git working tree**, with restricted ACLs, controlled backup/retention and sufficient disk space. The package, inventory, notebook/Data Agent definitions, receipts, tool logs and readback contain financial data or sensitive metadata. Review definitions for embedded secrets, source endpoints and proprietary instructions before export. Do not put them in a repository, ordinary CI artifact store, chat transcript or public support case.

The toolkit rejects export packages, receipts and readbacks inside **any Git repository**, not just this checkout. **Receipts and readbacks must also be outside the immutable export package**; use separate approved locations such as the sibling paths below.

Keep the export package immutable. Record its manifest SHA-256 through an **independently approved channel** and supply that approved value to import/validation. A hash downloaded beside an untrusted package does not establish its authenticity: hashes detect corruption only relative to a trusted baseline. Bind the approval to the source inventory, freeze window, operator, release and data classification. Changes to the package invalidate approval; do not recompute and bless a new hash to bypass a failure.

## 3. Inventory and freeze the source

Copy and populate [estate-source.example.json](../scripts/fabric/estate-source.example.json) through the approved configuration process:

- `version: 1`, actual source `tenantId`, and each selected workspace's `id`.
- Under each workspace, one `lakehouses` entry per discovered Lakehouse: `id`, actual `sqlServer`, `sqlDatabase`, and `requireZavaContract`.
- Use the actual SQL endpoint hostname and the Lakehouse display name or SQL endpoint ID as the database. The wrapper validates these values against the typed Lakehouse API properties and endpoint provisioning status; do not guess them from the workspace name.

The examples below are run from the approved repository/release root. The `D:` paths are illustrative **customer-approved locations**, not paths created by this guide. Use distinct, non-existing output directories. `$sqlClientDirectory` refers to the approved existing SQL-client build/dependency folder.

```powershell
$sourceConfig = 'D:\ApprovedFinanceTransfer\source.json'
$packagePath = 'D:\ApprovedFinanceTransfer\export-20260930'
$targetMap = 'D:\ApprovedFinanceTransfer\target.json'
$receiptPath = 'D:\ApprovedFinanceTransfer\import-20260930\receipt.json'
$readbackPath = 'D:\ApprovedFinanceTransfer\readback-20260930'
$sqlClientDirectory = 'D:\ApprovedFinanceRelease\sql-client'
```

The following export/import examples **explicitly opt in to the Preview DataAgent definition route** for an estate containing the working Zava Data Agent. Only use this switch with the owner's Preview approval. Omit it for an estate without DataAgents; do not silently exclude an existing DataAgent when approval is unavailable.

Sign in separately to the **source tenant** from the approved configuration. Both logins are required; neither changes the other tool's context:

```powershell
$sourceTenantId = (Get-Content -LiteralPath $sourceConfig -Raw | ConvertFrom-Json).tenantId
fab auth login --tenant $sourceTenantId
az login --tenant $sourceTenantId
```

Check that both commands succeed, then inventory. The wrapper verifies both tenant contexts before online operations:

```powershell
.\scripts\Export-FabricEstate.ps1 `
  -SourceConfig $sourceConfig `
  -OutputPath 'D:\ApprovedFinanceTransfer\inventory-20260930' `
  -InventoryOnly -AllowPreviewDataAgent
```

Inventory is an online read operation, not an offline dry run. Review its JSON/manifest and all blockers, including missing Lakehouse SQL entries. `InventoryOnly` does not produce an importable payload or prove that the later SQL/definition/data checks will pass.

Before full export:

1. Finance/data owners freeze ingestion, manual edits, notebook jobs, pipelines, refreshes, schedules and **VACUUM**, and wait for in-flight operations to finish. Also freeze definition, item, permission and configuration changes.
2. Resolve custom workspace-folder, Notebook/Lakehouse maintenance schedule and shortcut blockers with an approved migration design. Merely pausing a schedule does not make automatic schedule restoration supported.
3. Wait for SQL analytics metadata/data synchronization; reconcile the starting inventory and actual financial periods. Ensure the audit identity is not seeing filtered results.
4. Obtain explicit **content and dependency approval**, including cross-workspace or external source references and their target access/rebinding plan. Automatic mappings cover only known exported IDs, SQL endpoint IDs/hostnames and explicit bindings; this is **not a universal dependency parser**. A secret scan alone cannot attest that all sensitive content or source dependencies are approved.
5. Record the freeze start, responsible owners and restoration plan. Maintain the freeze **through the entire export and its final checks**.

`-SourceQuiesced` and `-DefinitionContentApproved` are explicit attestations, not mechanisms that stop source jobs or sanitize definitions:

```powershell
.\scripts\Export-FabricEstate.ps1 `
  -SourceConfig $sourceConfig -OutputPath $packagePath `
  -SourceQuiesced -DefinitionContentApproved -AllowPreviewDataAgent `
  -SqlClientDirectory $sqlClientDirectory
```

The export compares before/after metadata, OneLake file/directory listings, definitions and SQL reconciliation. Workspace inventory comparisons use canonical JSON rather than raw property ordering. Its internal SQL comparison uses **`-CompareSecurity`** for source-before/source-after principals, permissions and policies. This is not a public wrapper switch or a complete Fabric security audit. These are independent reads, **not an atomic cross-table/workspace estate snapshot guarantee**. Any detected change fails the export. A verified package is usable only when these checks pass **and** the owner can attest to the freeze. A failed/incomplete export is not importable; inspect it and start a new export directory after correcting the cause.

If the source resumes writes after capture, record the resulting divergence. The toolkit has no incremental catch-up/CDC stage; a later cutover may require another approved freeze and full migration.

## 4. Approve the package and target mapping

After independent manifest approval, set the approved digest without deriving it from the package being checked:

```powershell
$approvedManifestSha256 = 'PASTE_THE_INDEPENDENTLY_APPROVED_64_HEX_SHA256'

.\scripts\Test-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -PackageOnly
```

Replace the explicit approval placeholder before execution. `-PackageOnly` is **offline**: it verifies the approved manifest/package entry set, paths, kinds, sizes and file SHA-256 values, **including empty directories**. Directory entries have `kind: directory`, zero bytes and a null content hash; their path/type/presence are covered by manifest approval and entry validation, not an invented hash of directory bytes. It does not read Fabric, verify target access or approve cutover.

Populate [estate-target.example.json](../scripts/fabric/estate-target.example.json):

| Field | Mapping rule |
| --- | --- |
| `version`, `tenantId` | Version 1 and actual target tenant. Same-tenant or cross-tenant relocation is possible only within the supported, rehearsed contracts. |
| `workspaces` | Exactly one `sourceId` to precreated `targetId` per source workspace. No reused target workspace GUID or source destination. |
| Lakehouse entry in `items` | `sourceId`, precreated empty Lakehouse `targetId`, exact target `displayName`, actual `sqlServer`/`sqlDatabase`, and the unchanged `requireZavaContract` requirement. |
| Notebook / DataAgent entry in `items` | `sourceId` and new target `displayName`; **no `targetId`**. The wrapper calls the public create API through `fab api`, and the receipt captures the returned target ID. Names must not collide with existing target items; this is not native import/upsert. |
| `bindings` | Explicit reviewed `source`/`target` string pairs for other opaque endpoint/resource references that cannot be derived from workspace/Lakehouse/item mappings. Do not override automatic item/workspace ID mappings. |

The map must cover **every supported manifest item exactly once**, with no unknown entries. The generated SQLEndpoint is not a separate item mapping. Automatic rebinding covers known exported workspace/item IDs, their SQL endpoint IDs/hostnames and explicit `bindings`; it is **not a universal dependency parser**. Obtain manual content/dependency approval for **cross-workspace and external source references**, including connections, database names and dynamically constructed references. Rebind specific identifiers/endpoints, not blanket replacements of generic names such as `Finance`, `dbo` or `Sales`. Unresolved references block owner approval; do not assume they were discovered or made safe by a successful definition comparison.

Target workspace creation does not recreate source users, groups, consent, RLS or item grants. Build a separate reviewed source-to-target principal/access matrix, including tenant changes. Never paste source grants into the new tenant or copy publisher privileges to employees.

## 5. Import into isolated targets

**Cross-tenant transfer is staged:** finish the source-to-local download first; preserve the immutable package; then change the required tool logins to the **target tenant** and upload from the local package. Verify tenant context before each phase. Do **not** run direct Fabric-to-Fabric cross-tenant AzCopy, and do not grant one identity broad access to both tenants merely to emulate it.

Before target preflight/apply/readback, sign in to both CLIs using the approved target map, even if only one tool's context appeared wrong:

```powershell
$targetTenantId = (Get-Content -LiteralPath $targetMap -Raw | ConvertFrom-Json).tenantId
fab auth login --tenant $targetTenantId
az login --tenant $targetTenantId
```

Check both results; native CLI refresh does not remove the need for matching tenant contexts.

Import without `-Apply` is an **online, read-only preflight**, including actual target existence/emptiness and naming checks. It is not an offline simulation:

```powershell
.\scripts\Import-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -TargetMap $targetMap -ReceiptPath $receiptPath `
  -AllowPreviewDataAgent
```

Review the JSON plan, dependency order, target IDs and all warnings/failures. Only after confirming the target isolation and change approval:

```powershell
.\scripts\Import-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -TargetMap $targetMap -ReceiptPath $receiptPath `
  -Apply -TargetIsolated -AllowPreviewDataAgent
```

The toolkit copies Lakehouse content and creates supported definition items in dependency order. It records source-to-target IDs and operation state in the receipt. It does **not** execute notebooks, publish DataAgents, grant permissions, activate the target resolver release or change the application.

**The copied `resolver_release` is still source metadata.** Its source Search/embedding binding may not exist or be authorized in the target. Copying it does not approve it for use; keep the target isolated until the separate target release procedure below is complete.

### AzCopy completion: content, not directory properties or ACLs

With **`--overwrite=false`**, AzCopy can legitimately skip transferring the **pre-existing destination root's folder properties**, while copying all files and new child directories. Content-complete output can therefore report **`CompletedWithSkipped`**, `TransfersFailed="0"`, `TransfersSkipped="1"` and `FoldersSkipped="1"`, with **no skipped files**. Success is not universally `Completed` with zero total skips.

This is **not blanket acceptance of `CompletedWithSkipped`**. If a skip is reported, allow **exactly one**, identified as the existing **ROOT directory's properties**, only after verifying that the destination has **no descendants: no files or child directories**. Keep the target isolated and inspect the job evidence. Verify the resulting destination inventory, then complete the full readback below. **Skipped files, multiple or unexpected skipped directories, failed transfers or ambiguous evidence block acceptance.** Do not relax the checks to make a job pass. The existing root's directory properties were **not overwritten**: content restoration does not preserve all metadata, ownership or ACLs.

Directory listings and job summaries need their documented interpretation:

- A listed folder's `Path` ends in `/`; the selected root can appear as `/`. Normalize directory paths and exclude **only that selected-root marker**, never its descendants. Empty child directories remain required manifest/readback entries.
- Directory modification timestamps are diagnostic evidence, **not content-equality criteria**. Retain them in restricted diagnostic evidence when collected; a synthetic root timestamp may represent enumeration time rather than a source change. Do not use it to declare either a changed estate or a faithful metadata restore.
- Recursive Local-to-BlobFS copy transfers empty child directories without a special empty-directory flag when no file-only filters are applied. **`--as-subdir=false` does not suppress root-folder-property transfer**. Do not introduce filters that silently drop directories.
- The outer JSON record has `MessageType="EndOfJob"`; `MessageContent` is a **serialized JSON string**, parsed a second time. Totals are numeric **strings**. `TransfersFailed`/`TransfersSkipped` already include folder failures/skips; do not add the folder counters again.

**Source-based illustration only, not captured OneLake output:** one new file, one new empty child directory and an existing destination root can produce inner-summary values `TotalTransfers="3"`, `FileTransfers="1"`, `FolderPropertyTransfers="2"`, `TransfersCompleted="2"`, `FoldersCompleted="1"`, `TransfersFailed="0"`, `FoldersFailed="0"`, `TransfersSkipped="1"`, `FoldersSkipped="1"` and `JobStatus="CompletedWithSkipped"`.

These findings come from the pinned public [AzCopy source revision](https://github.com/Azure/azure-storage-azcopy/tree/35dc27add1e640506b1221172f6c0f3d317dff65), including folder-property transfer and job-summary contracts. This is **main-branch source evidence**, not validation of an installed/released binary or a version-wide guarantee. Record the actual AzCopy binary version and verify the behavior during the customer rehearsal.

### Failure and retry rules

- An interrupted/in-progress import **fails closed**. Inspect the remote long-running operation/job and partial target state before doing anything else; a client timeout does not prove the remote operation stopped.
- Do not edit/reset a receipt, overwrite tables, rerun an unknown create operation or resume an AzCopy job to bypass this rule. Use **new empty targets, an updated map and a new receipt** for a fresh attempt after the remote situation is understood.
- A **completed** import may be repeated idempotently with its original approved package, unchanged map and completed receipt. This is not a repair mechanism for later target edits; rerun full validation before considering the target usable.
- Retain failure evidence under the restricted transfer boundary. Cleanup/deletion of failed targets and transfer files is a separate customer-approved operation, never automatic source destruction.

## 6. Validate the actual restored data and definitions

Keep the target frozen and isolated. Wait for the SQL analytics endpoint to expose the restored tables and correct types, then run:

```powershell
.\scripts\Test-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -TargetMap $targetMap -ReceiptPath $receiptPath `
  -ReadbackPath $readbackPath -SqlClientDirectory $sqlClientDirectory
```

This is a **full readback download**, not a metadata-only smoke test. Use a new readback directory on the approved encrypted volume for each validation. Complete it **before Search publication/release activation or any target data changes**; the imported source release metadata is part of this pre-activation baseline.

| Check | What the evidence establishes |
| --- | --- |
| Physical content | Download target Tables/Files; compare exact file/directory entries, kinds, paths, sizes and file SHA-256 against the approved package. Retained Delta history **and empty directories** are included. Exclude only the selected-root listing marker; directory timestamps are diagnostic, not content equality. Listings/counts alone do not establish byte identity or ACL/metadata preservation. |
| SQL schema and counts | Compare the full captured column contract, types/precision/scale/nullability and row counts per table after SQL synchronization. A successful AzCopy upload is not SQL readiness. |
| Financial reconciliation | Compare exact aggregates by the captured period, region, department and finance/KPI keys, including headcount and the supported GL contract. Decimal measures preserve their declared scale. For `float`/`real`, SQL groups and sums integer significands by binary exponent, then .NET `BigInteger` produces the exact decimal total of the stored values. This avoids order-dependent floating-point SUM and invented currency rounding; the source types and data are unchanged. Do not replace actual totals with demo reference values. |
| Definition rebinding | Read back imported Notebook/DataAgent definition parts via ID-addressed `fab api` requests and compare reviewed rebound content. Check for stale source identifiers/endpoints. This does not execute notebooks or prove Data Agent answers. |
| Security and release | Target SQL comparison deliberately **excludes the principals, permissions and policies sections**, whose identities/access require manual mapping. Workspace-role and SQL-security inventories are evidence, not complete item ACL/OneLake security backups. No successful data test proves security equivalence or authorizes publication. |

**`-CompareSecurity` is used only for source-before/source-after SQL checks**, not for source-to-target equality. Do not pass it to the public migration wrappers or bypass target identity differences by copying grants. Security owners must separately inspect item sharing/ACLs, OneLake access, SQL policies, labels and real allowed/denied-user behavior before activation.

The SQL reconciler deliberately supports a bounded contract. Generic tables receive schema/count checks plus physical-file verification, not domain-specific financial assertions. For Zava GL reconciliation the expected fields are `account_code`, `amount_usd` and one or more date fields from `month_start`, `transaction_date` or `posting_date`, plus region/department keys. Multiple date fields must agree on financial month and nullness for every row; `month_start` is preferred when present. Disagreement or other shapes require a reviewed period mapping/extension. Unsupported types, precision loss, visibility failures, size limits or schema/aggregate mismatches are blockers, not silently skipped comparisons. The approved full source schemas and independent finance acceptance are still required.

Retain the returned JSON, completed receipt and target `validation.json` with the independently approved manifest record. The report must retain **`cutoverApproved=false`** and its remaining manual gates even when data/definition checks succeed. Do not rewrite it to manufacture approval.

## 7. Rebuild out-of-Fabric services and release manually

Successful **full-data/definition validation in Section 6 precedes Search publication and release activation**. Activation changes the imported release baseline and is a separate phase, not a way to make migration validation pass. Finance, security and release owners must coordinate the following work:

1. **Rebuild Azure AI Search from the imported actual catalogue.** Use the existing `Publish-ResolverSearch.ps1` with the target SQL endpoint, the approved imported catalogue version and a target versioned index. Preserve all imported catalogue/history rows; do **not** regenerate demo metadata with `Publish-ResolverData.ps1` as a migration shortcut.
2. Validate target Search IDs/count/version, embeddings/dimensions and hybrid semantic retrieval as described in [resolver publication](resolver-data.md). Its current Zava-specific probe/assumptions may reject a legitimately curated imported catalogue; review the release-specific publisher/tests rather than weakening finance or integrity gates.
3. Independently inspect and approve target principals, item ACLs/sharing, OneLake/SQL access and policies, sensitivity labels and tenant consent. Workspace-role inventory alone does not cover these controls. In a controlled pilot boundary, test real allowed **and denied** users against target SQL, resolver visibility and Data Agent sources before releasing traffic. Search does not inherit Fabric user security.
4. **Bind/activate the target resolver release only after target Search and real-user permission checks pass.** Review a version-specific publication approach that preserves imported immutable content and history. The current `Publish-ResolverData.ps1 -ActivateRelease` revalidates generated rows and may reject a curated imported catalogue. Do not bypass that validation, regenerate the imported catalogue, or manually edit the release pointer merely to force success.
5. Have the Data Agent owner review the imported instructions, examples, sources and runtime, publish through the supported target experience, and verify real delegated MCP requests. Definition transfer alone does not close **R1**.
6. Prepare application settings **`FABRIC_WORKSPACE_ID`**, **`FABRIC_DATA_AGENT_ID`**, **`FABRIC_SQL_ENDPOINT`** and **`FABRIC_DATABASE`** using the actual target identifiers. Verify Search/model release bindings and all other environment-specific dependencies. If the tenant changes, update the approved finance OAuth/Entra consent, principals and channel identity configuration as well; four Fabric variables alone are insufficient.
7. Obtain finance/security acceptance and the application/channel release approval. Rehearse the actual runtime and two-user negative tests, then authorize a controlled application/configuration cutover. The migration scripts perform **no automatic cutover, grants or publication**.

Example Search rebuild, **not release activation**:

```powershell
.\scripts\Publish-ResolverSearch.ps1 `
  -SqlServer $targetSqlServer -SqlDatabase $targetDatabase `
  -CatalogVersion $importedCatalogVersion `
  -SearchEndpoint $targetSearchEndpoint -SearchIndex $targetVersionedIndex `
  -EmbeddingEndpoint $targetModelEndpoint -EmbeddingDeployment $embeddingDeployment `
  -EmbeddingDimensions 1536 -SqlClientDirectory $sqlClientDirectory
```

Supply the reviewed values from the target release record. The baseline application's vector size is 1536; do not silently change it. Retain the old environment, publication and configuration for rollback. Rollback routes the application back to the approved old environment; it is **not** an instruction to overwrite restored data or destroy either side. If users or jobs wrote after cutover, reconcile that divergence with finance before rollback. No automatic reverse synchronization is provided.

## 8. Customer acceptance record

Record the selected source/target tenant/workspace/Lakehouse IDs, both CLI tenant contexts, scope/blocker resolutions, full source visibility, tool/dependency versions, freeze interval, package custody, independent manifest approval, map hash, receipt, complete file/directory readback, pre-activation SQL/definition results, independent item/OneLake/SQL security and label review, actual finance acceptance, Search/release readback, Data Agent publication/delegated tests and tested rollback.

The handover remains **DRAFT** until the applicable [R1-R4 delivery and clean-workstation gates](customer-it-installation-guide.md#19-installation-handover-and-sign-off) close. A successful source export, offline package verification or PDF rebuild does not close those gates. Target import/readback and customer finance/security acceptance are still outstanding.

## 9. Toolkit regression checks

Run the offline suites before using a modified toolkit:

```powershell
.\tools\test-fabric-estate.ps1
.\tools\test-fabric-estate-fabric.ps1
.\tools\test-fabric-estate-workflow.ps1
.\tools\test-fabric-estate-sql.ps1
```

The workflow suite uses the **real SQL comparator** with mocked database reads, covering empty differences and genuine drift. Export retains the source-after snapshot and flat differences in `readback-<Lakehouse ID>-sql.json` outside the immutable payload. These diagnostic files are not part of the payload file inventory; retain their hashes with the trusted run evidence when needed.

For SQL-engine compatibility checks, [the SQL suite](../tools/test-fabric-estate-sql.ps1) also accepts `-LiveServer`, `-LiveDatabase` and optional `-LiveSqlClientDirectory`. Supply an authorized Fabric SQL endpoint and database with the existing SqlClient dependencies and Azure CLI login. This opt-in executes synthetic `VALUES` queries only; it does not read or modify customer tables. It checks exact float/real aggregation, cancellation, nulls, and case/trailing-space grouping. Passing synthetic tests does not replace full source/target reconciliation.
