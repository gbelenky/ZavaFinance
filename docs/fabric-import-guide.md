# Reproduce ZavaFinance: Fabric configuration and data import

**Recipient guide, edition 1.0 - 1 October 2026**

**Audience:** the Fabric administrator, data engineer and application owner installing ZavaFinance in another environment. This guide starts with an existing approved export; the recipient does not need access to the source tenant.

**Outcome:** restore the captured finance data and item definitions, configure the target Fabric environment, and produce the identifiers and acceptance evidence needed by the application team. This is an **existing-estate restore**, not a demonstration-data generator.

**Verification boundary:** the reference source export and offline package verification succeeded. Target import, full target readback, security acceptance and application cutover have **not** been performed. Do not treat this document as evidence of a successful target rehearsal.

## At a glance

| Phase | Responsible owner | Completion gate |
| --- | --- | --- |
| 1. Receive the handover | Source release/data owner and recipient | Approved code, complete export, trusted checksum, dependency pack and configuration/access records received |
| 2. Prepare Fabric | Target Fabric/Azure administrators | Capacity active, tenant settings approved, isolated workspace and empty schema-disabled Lakehouse ready |
| 3. Prepare the workstation and map | Migration operator | Required tools/dependencies available; target identifiers and definition bindings reviewed |
| 4. Verify and import | Migration operator | `packageValidated`, then `planned`, then `imported` |
| 5. Verify the restored estate | Data engineer and finance owner | Full readback returns `dataAndDefinitionsValidated`; finance reconciliation accepted |
| 6. Configure and release | Security, Data Agent and application owners | Target access, Search/release, published agent and real-user tests accepted |

**Do not run the imported stage/activate notebooks, publish the agent, or connect the application before the pre-release checks.** Import creates items and copies data; it does not grant access or make the solution ready for users.

Companion documents: [technical migration runbook](fabric-estate-migration.md), [complete application installation](customer-it-installation-guide.md), [resolver/Search publication](resolver-data.md), [operations and acceptance](operations.md).

## 1. Receive a complete handover

The sender must provide the following through approved channels. **The Git repository alone does not contain the exported financial data.** A source checkout also is not proof that the reviewed migration scripts and their latest fixes are present: bind the delivered code snapshot to a version/hash record.

| Delivery | Required contents |
| --- | --- |
| Reviewed code release | The import/test entrypoints, all five `Fabric-Estate*.ps1` helpers, `Resolver-Sql.ps1`, four migration test suites, and this guide/runbook. Preserve the repository-relative structure. Include the resolver publication scripts and `scripts\fabric\resolver_catalog.py` for the separate release phase. Prefer the complete approved source snapshot. |
| Immutable export directory | `manifest.json` and the entire `payload` tree, including hidden files, empty directories, Delta logs/checkpoints and all definition parts. Do not send selected Parquet files or just the manifest. |
| Independent approval record | Manifest SHA-256, source inventory, export/freeze interval, operator, data classification, permitted recipient and successful export/package-validation evidence. Deliver the approved checksum through an independently trusted channel. |
| SQL-client dependency folder | Approved public Microsoft.Data.SqlClient **6.1.4** and its full compatible dependencies for this release's .NET 10/Windows loader. Include provenance and file hashes; not just one DLL or an unreviewed application build directory. Required even for a Python-only application. |
| Fabric configuration supplement | Source settings and approved target decisions from Section 3: capacity/region, schema mode, tenant/capacity switches, notebook runtime/environment dependencies, Data Agent runtime/source/table selection/instructions/examples, publication and external references. |
| Security and governance supplement | Source-to-target user/group/application mapping, workspace roles, item sharing, OneLake access, SQL grants/policies, labels, connection ownership, network/data-residency approvals and allowed/denied-user test cases. Exported security inventories are incomplete and do not replace this supplement. |
| Finance/application acceptance record | Approved data contract and expected questions/results, active catalogue/release tuple, target Search/embedding plan, application release and rollback owner. Transfer secrets only through the target secret-management process. |

**Missing configuration is a handover blocker, not a value for the recipient to guess.** The captured Data Agent definition is available, but the source owner must still attest its intended runtime, source selection and successful behavior. Tenant settings, item ACLs and user licenses cannot be reconstructed from the package alone.

The older [resolver-seeding ZIP](zavafinance-fabric-seeding.zip) is **not** this handover: it does not contain the original finance dataset or the estate-migration toolkit.

### Reference package for this handover

These identifiers describe the verified export, not resources to reuse as destinations:

| Property | Reference value |
| --- | --- |
| Export directory name | `export-20261001-004016` |
| Source workspace | `Zava-Finance` |
| Source Lakehouse | `lh_zava_finance` |
| Capture interval, UTC | 30 September 2026, **22:40:26 to 22:49:20** |
| Source consistency | Operator-attested freeze plus successful before/after checks; not an atomic multi-item snapshot |
| Included items | One Lakehouse, two notebooks, one Data Agent; the SQL analytics endpoint is a generated dependency |
| Physical OneLake data | **50 files, 30 directories, 37,934,147 bytes**; `Files` contains no data files |
| SQL baseline | **13 tables, 92 columns, 74,688 financial reconciliation groups** |
| Package payload inventory | **97 entries = 55 files + 42 directories**, not 97 data files |
| Export manifest SHA-256 | `2fc20a79a8a3181e9be3a34ea4d7d4d2cf2bb5764af3c838af2b9975992fa561` |

The hash above identifies this exact package; the recipient must still obtain its independent approval from the sender. A different approved export needs its own hash, scope and baseline. Never compute a new hash to make an altered or failed package pass.

Source activity after the capture interval is **not included**. There is no incremental/CDC catch-up. This package can reproduce the captured environment while the source continues running; migrating a later business state requires a new approved export.

## 2. Understand what will be restored

| Component | Restored automatically | Recipient action |
| --- | --- | --- |
| Lakehouse data | Complete physical `Tables`/`Files` trees, retained Delta logs/checkpoints and empty directories | Precreate an empty Lakehouse with the correct schema mode; validate the copied data |
| SQL-visible schema | Delta table metadata/data are copied; the target SQL endpoint synchronizes them | Wait for synchronization; compare full schemas and counts rather than manually typing DDL |
| Notebooks | API-returned definition parts, including stored outputs when present; reviewed reference rebinding | Confirm target attachments, runtime/libraries/environments and external dependencies; do not execute during restore |
| Data Agent | API-returned definition parts with reviewed target bindings | Review actual configuration, runtime, source selection, publication and delegated behavior |
| Resolver tables | Actual catalogue, scope, calendar and release rows, including retained history | Rebuild target Search from imported content; approve target release bindings separately |
| Capacity, workspace, tenant switches | **Not provisioned/restored** | Configure in Azure/Fabric before import |
| Access, consent, labels, credentials | **Not restored** | Apply approved target-specific identity/governance configuration and test effective access |
| Search, Foundry, Entra apps, Bots, KPIpedia and channels | **Outside this export** | Use the application installation guide; Fabric import alone does not reproduce the entire application |

The core **Fabric Data Agent service and Fabric CLI are GA**. This toolkit's Data Agent definition/ALM route and workspace-folder inventory API are **Preview**, requiring explicit approval. The import commands below opt in to the Data Agent route.

The reference export has no workspace folders, shortcuts, schedules, custom SQL objects or enabled SQL security policies. The toolkit blocks unsupported estate contents rather than silently omitting them. This does not prove that source item sharing or OneLake permissions were absent.

### Data baseline

All tables below are SQL-visible under `dbo`. Counts are specific to this package, not universal acceptance values for a different customer dataset.

| Table | Captured rows |
| --- | ---: |
| `dim_account` | 16 |
| `dim_date` | 1,461 |
| `dim_department` | 14 |
| `dim_kpi` | 18 |
| `dim_region` | 4 |
| `fact_finance_monthly` | 25,728 |
| `fact_gl_transaction` | 1,492,224 |
| `fact_headcount_monthly` | 2,688 |
| `fact_kpi_monthly` | 38,592 |
| `resolver_calendar` | 1 |
| `resolver_catalog` | 114 |
| `resolver_release` | 1 |
| `resolver_scope` | 336 |

The **complete captured column/type/precision/scale/nullability contract, counts, release tuple and financial checks** are in each Lakehouse item's `lakehouse.sqlSnapshot` in the approved manifest. Treat that record as sensitive evidence. Do not infer full schemas from the shortened application-contract table in the installation guide.

Keep the actual numeric types, dates and codes. Some source measures are `float`; the migration validator compares exact totals of the stored binary values without rounding them to invented currency precision. Do not convert them during import.

## 3. Configure the target Fabric environment

### 3.1 Tenant, subscription, capacity and region

1. Select the **target Entra tenant** used by the solution's users and finance application registrations. This application's baseline is single-tenant; moving only Fabric to another tenant does not automatically make the existing application work cross-tenant.
2. Create or reuse an approved **paid F2-or-higher capacity**, or an eligible existing P1-or-higher capacity with Fabric enabled. F2 is a feature minimum, **not** a production-sizing recommendation. Trial/PPU access is not a substitute for the required capacity.
3. For an Azure F capacity, use a subscription associated with the **target tenant**. It may be a different subscription from Foundry/Search/Bot resources. It need not be in the same resource group. Record the Azure resource ID **and** the separate Fabric capacity GUID.
4. Choose a currently supported region for the required Data Agent/runtime features. Keep the Data Agent and its source workspace in the same supported region; this guide places them in one workspace. Review cross-service geography separately.
5. Verify the capacity is active, quotas/budget are approved, and an accountable capacity administrator is assigned. The migration scripts neither provision nor resume capacity.

The original workspace uses an active **F2 in West US 3**. That is reference evidence, not a requirement to reuse the sender's subscription, resource names or region.

### 3.2 Tenant and capacity settings

The target Fabric administrator must review the current [Data Agent tenant settings](https://learn.microsoft.com/fabric/data-science/data-agent-tenant-settings). Record actual values and their scoped groups/capacity overrides, not just screenshots of switches without scope.

| Setting/control | Target action |
| --- | --- |
| Microsoft Fabric availability | Enable the required Fabric workloads for the approved users/capacity |
| **Users can use Copilot and other features powered by Azure OpenAI** | Enable for the approved Data Agent users; check both tenant and capacity scope |
| **Capacities can be designated as Fabric Copilot capacities** | Review/configure as required by current Data Agent guidance and the capacity design |
| Azure OpenAI data processing outside the capacity geography/compliance boundary | Required by current guidance for capacities outside the EU data boundary and US; obtain explicit approval before enabling |
| Azure OpenAI data storage outside the capacity geography/compliance boundary | Review separately from processing; current guidance requires it for the corresponding Data Agent scenario outside the EU data boundary and US |
| Data Agent consumption through MCP/non-Fabric services | Approve the intended Foundry/application use and applicable tenant controls; data returned outside Fabric is subject to the consuming service's boundary/policies |
| Governance/network controls | Apply approved labels, access restrictions and connection rules. Workspace outbound access protection for Data Agent is **Preview** where selected; do not enable it or other Preview features implicitly |

Allow for setting propagation (Microsoft documents up to one hour). Do not enable tenant-wide AI/cross-geography access just to clear a test failure. This does not provision a customer Azure OpenAI key for Fabric: the in-product Data Agent's managed model service is separate from ZavaFinance's Foundry routing/embedding deployments.

### 3.3 Workspace, Lakehouse and generated SQL endpoint

1. In Fabric, create a **dedicated destination workspace**, for example `Zava-Finance-Dev`. Assign it to the approved capacity and verify the assignment completed.
2. Restrict access to the authorized migration/administration identities. No ordinary users, application traffic, ingestion, notebooks, maintenance jobs, schedules or automatic writers may touch the target during import and baseline validation.
3. Create **one empty Lakehouse**, preferably named `lh_zava_finance`, with **Lakehouse schemas disabled**. Verify this in the actual creation/settings experience.
4. Required physical layout: `Tables/<table_name>`, with SQL names `dbo.<table_name>`. A schema-enabled layout such as `Tables/dbo/<table_name>` is incompatible with this release. If the schema option is unavailable or already wrong, stop and resolve it; do not turn off `requireZavaContract`.
5. Leave both `Tables` and `Files` empty, including child directories. Do not upload CSV/Parquet files, create sample tables or run the resolver seeding pack.
6. Wait until Fabric has provisioned the generated SQL analytics endpoint successfully. Copy its **hostname only**, its endpoint GUID, and the Lakehouse name/database from Fabric. Do not use the source SQL host or a full connection string.
7. Do **not** precreate the two notebooks, the Data Agent, a separate SQL endpoint, shortcuts or schedules. The importer creates definition items; the Lakehouse generates its endpoint.

Portal **Load to tables** is not this restore: loading every Parquet file ignores the retained Delta log and can ingest obsolete data files. Fabric Git/deployment pipelines move Lakehouse metadata, not the physical table data.

### 3.4 Permissions and configuration worksheet

Azure subscription IAM is **not** Fabric workspace/item/SQL authorization.

- Give the migration operator a time-bounded **Member** role on the dedicated destination workspace, plus any separately required data access. The toolkit inventories workspace role assignments; that API requires **Member or higher**, so Contributor alone is insufficient for this workflow. Do not grant tenant administration or leave ordinary users in this privileged role.
- The validation identity needs SQL `SELECT` on every captured table and database `VIEW DEFINITION`, with unfiltered visibility. RLS-filtered results are not a full migration audit.
- Use separately controlled ordinary/restricted user identities for acceptance. Do not keep employees or the application's runtime identity in the migration operator's privileged group.
- SQL policies do not by themselves protect Spark/OneLake paths. Review item sharing, direct OneLake access and SQL access together.
- Recreate target groups/principals and approved grants explicitly; source object IDs and consents are not portable to another tenant.

Complete this worksheet in the **restricted handover record**, not in Git:

| Record | Required target value/evidence |
| --- | --- |
| Identity | Tenant ID, migration operator, admin/data/security owners, approved pilot and denied-test users/groups |
| Capacity | Subscription ID, resource group, Azure resource ID, Fabric capacity GUID, SKU, region, active state |
| Workspace | Name, GUID, assigned capacity GUID and isolated access list |
| Lakehouse | Name, GUID, schemas disabled, empty Tables/Files verification |
| SQL endpoint | Generated endpoint GUID, actual hostname, database, successful provisioning and access checks |
| Tenant/capacity policies | Each switch above, scopes/overrides, propagation and data-residency approvals |
| Notebook configuration | Approved Spark runtime, environments/libraries if any, Lakehouse attachments, connections and external dependencies; explicit "none" where applicable |
| Data Agent configuration | Intended runtime, selected sources/tables, instructions/examples, connection requirements and source owner's expected behavior |
| Security/governance | Workspace roles, item sharing, OneLake/SQL matrix, labels, consent and network restrictions |
| Application handover | New item IDs from receipt, published MCP endpoint, approved Search/model/release bindings and acceptance results |

## 4. Prepare a clean Windows workstation

Run in **PowerShell 7.6**, not Windows PowerShell 5.1. Use standalone Windows Terminal for interactive login.

| Requirement | Installation/verification |
| --- | --- |
| PowerShell 7.6 / .NET 10 | Install through approved Microsoft channels; verify `$PSVersionTable.PSVersion` and the approved SQL dependency layout |
| Azure CLI | Install using [Microsoft instructions](https://learn.microsoft.com/cli/azure/install-azure-cli-windows); `az version` |
| Python for Fabric CLI | Supported Python 3.10-3.13; the example below uses Python 3.13 |
| Public Fabric CLI | Install **`ms-fabric-cli==1.7.0`**. The adapter rejects every other version until a reviewed compatibility update |
| AzCopy | Use the approved Microsoft **10.32.7** Windows binary to match the successful source export; preserve its directory on PATH and record provenance/version |
| SQL client | Approved public 6.1.4 dependency folder containing `runtimes\win\lib\net9.0\Microsoft.Data.SqlClient.dll` and its dependency DLLs. Pass its root as `-SqlClientDirectory` |
| Network | Entra/Fabric/OneLake HTTPS 443; target SQL TCP 1433; later Search/embedding HTTPS access. Honor proxy, Conditional Access and data-egress policies; never disable TLS validation |
| Storage/custody | An approved encrypted transfer volume **outside every Git working tree**, restricted ACLs, controlled retention and sufficient room for the export, logs and a full readback |

The source-tested tool combination is not yet a target-import certification. Use the approved dependency pack for SQL checks; a clean build of the entire .NET agent is not a prerequisite when that pack is supplied. If choosing to build the delivered .NET solution instead, first resolve its documented dependency-delivery gate. Do not obtain internal-only libraries or copy an arbitrary developer cache into a customer deployment.

Example isolated Fabric CLI installation, after Python 3.13 and approved tool directories are available:

```powershell
$ErrorActionPreference = 'Stop'
$toolRoot = 'C:\FinanceTools'
py -3.13 -m venv "$toolRoot\fabric-cli"
if ($LASTEXITCODE -ne 0) { throw 'Fabric CLI virtual environment creation failed.' }
& "$toolRoot\fabric-cli\Scripts\python.exe" -m pip install 'ms-fabric-cli==1.7.0'
if ($LASTEXITCODE -ne 0) { throw 'Fabric CLI installation failed.' }

# Extract the approved AzCopy binary into this directory separately.
$env:PATH = "$toolRoot\fabric-cli\Scripts;$toolRoot\azcopy;" + $env:PATH
fab --version
if ($LASTEXITCODE -ne 0) { throw 'Fabric CLI is unavailable.' }
azcopy --version
if ($LASTEXITCODE -ne 0) { throw 'AzCopy is unavailable.' }
```

Use the [official Fabric CLI installation guidance](https://microsoft.github.io/fabric-cli/#install-the-cli) and [AzCopy distribution guidance](https://learn.microsoft.com/azure/storage/common/storage-use-azcopy-v10), including your organization's approved package feeds. Do not silently substitute an unreviewed latest release.

### Paths for the remaining procedure

The paths below are **recipient examples**, not directories on the sender's machine. First arrange approved ACLs/encryption for the transfer root and deliver the code, export and SQL-client dependency folder. Preserve empty directories when extracting/transferring the package.

Use one PowerShell session for the remaining steps. In a new terminal, restore these variables and the tools' PATH.

```powershell
$ErrorActionPreference = 'Stop'
$releaseRoot = 'C:\ApprovedFinanceRelease\ZavaFinance'
$transferRoot = 'C:\ApprovedFinanceTransfer'
$packagePath = Join-Path $transferRoot 'export-20261001-004016'
$sqlClientDirectory = 'C:\ApprovedFinanceRelease\sql-client'
$approvedManifestSha256 = '2fc20a79a8a3181e9be3a34ea4d7d4d2cf2bb5764af3c838af2b9975992fa561'

foreach ($required in @(
    $releaseRoot, $transferRoot, (Join-Path $packagePath 'manifest.json'),
    (Join-Path $sqlClientDirectory 'runtimes\win\lib\net9.0\Microsoft.Data.SqlClient.dll')
)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required delivery path missing: $required" }
}
Set-Location -LiteralPath $releaseRoot
$runRoot = Join-Path $transferRoot ('import-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$targetMap = Join-Path $runRoot 'target.json'
$receiptPath = Join-Path $runRoot 'receipt.json'
$readbackPath = Join-Path $runRoot 'readback'
$env:AZCOPY_LOG_LOCATION = $runRoot
$env:AZCOPY_JOB_PLAN_LOCATION = $runRoot
```

The new run directory inherits the approved parent ACLs; these commands do **not** establish encryption or restrict permissions. Keep receipts, staged definitions, logs and readbacks outside the immutable package. Do not create `$readbackPath` yourself: the validator requires it not to exist.

Run the shipped offline contract suites and stop on any failure:

```powershell
.\tools\test-fabric-estate.ps1
.\tools\test-fabric-estate-fabric.ps1
.\tools\test-fabric-estate-workflow.ps1
.\tools\test-fabric-estate-sql.ps1
```

## 5. Verify the received package, then create the target map

First verify the immutable package against the independently approved hash:

```powershell
.\scripts\Test-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -PackageOnly |
    Tee-Object -FilePath (Join-Path $runRoot 'package-validation.json')
```

Required result: **`status: packageValidated`**. This is offline integrity validation, not target access or release approval.

Create `target.json` at the path in `$targetMap`. For the reference export, use this **complete four-item map** and replace the four `REPLACE_...` values. The source GUIDs are intentional identifiers from the approved package; do not replace them with target IDs.

```json
{
  "version": 1,
  "tenantId": "REPLACE_TARGET_TENANT_GUID",
  "workspaces": [
    {
      "sourceId": "9e24269a-a425-404d-8211-49e939782405",
      "targetId": "REPLACE_TARGET_WORKSPACE_GUID"
    }
  ],
  "items": [
    {
      "sourceId": "df05cc15-1182-43f7-84fc-adb26b83e142",
      "targetId": "REPLACE_TARGET_LAKEHOUSE_GUID",
      "displayName": "lh_zava_finance",
      "sqlServer": "REPLACE_TARGET_SQL_HOSTNAME",
      "sqlDatabase": "lh_zava_finance",
      "requireZavaContract": true
    },
    {
      "sourceId": "2abe237d-bf50-48ca-8f56-1c3b91d3e416",
      "displayName": "Zava resolver 2026-09-16-v1 stage"
    },
    {
      "sourceId": "4a3efa5b-1d59-4a4b-90a6-3aaf69e51991",
      "displayName": "Zava resolver 2026-09-16-v1 activate"
    },
    {
      "sourceId": "6437efd8-ea33-4924-a115-3a970a10facf",
      "displayName": "Zava-Finance-Agent"
    }
  ],
  "bindings": []
}
```

Mapping rules:

- Keep the target Lakehouse name/database consistent. If a different name is approved, change both to the actual target values and review every name-based definition reference. The toolkit also supports the generated SQL endpoint GUID as database.
- Only the precreated Lakehouse has an item `targetId`. The importer creates the notebooks/Data Agent and records their IDs. Do not add a separate SQLEndpoint entry.
- Known workspace/item IDs and SQL endpoint IDs/hostnames are rebound. Other external/opaque references require specific reviewed `{"source":"...","target":"..."}` entries in `bindings`.
- Review cross-workspace references, connections, libraries, literal database names and constructed strings. Automatic remapping is not a universal dependency parser. An empty `bindings` array is approved only after this review.
- Do not use broad replacements of names like `dbo` or `Finance`, override automatic ID mappings, omit an item, or point a target at the source workspace/Lakehouse.
- Freeze the approved map before apply. The receipt is bound to its file hash; even later formatting changes alter that hash.

The [generic target example](../scripts/fabric/estate-target.example.json) contains only one Lakehouse entry and is **not sufficient unchanged** for this four-item package.

## 6. Authenticate to the target and run preflight

Use the same Windows user profile and Fabric CLI installation for login and migration. Signing into Fabric in Edge does not sign in the CLI.

```powershell
$target = Get-Content -LiteralPath $targetMap -Raw |
    ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
$targetTenantId = $target.tenantId
$targetSubscriptionId = Read-Host 'Approved Azure subscription ID in the target tenant'

fab auth login --tenant $targetTenantId
if ($LASTEXITCODE -ne 0) { throw 'Fabric CLI target login failed.' }
az login --tenant $targetTenantId
if ($LASTEXITCODE -ne 0) { throw 'Azure CLI target login failed.' }
az account set --subscription $targetSubscriptionId
if ($LASTEXITCODE -ne 0) { throw 'Target subscription selection failed.' }
fab auth status
if ($LASTEXITCODE -ne 0) { throw 'Fabric CLI authentication check failed.' }
az account show --query '{tenant:tenantId,subscription:id,name:name}' --output table
if ($LASTEXITCODE -ne 0) { throw 'Azure CLI context check failed.' }
```

For Fabric CLI, select **Interactive with a web browser**, press Enter and complete the account picker/MFA. If VS Code shows no login UI, use standalone Windows Terminal from the Start menu. Do not inject tokens, copy browser caches, grant broad application permissions or weaken Conditional Access.

Both CLI contexts must show the approved **target tenant**. Azure CLI supplies SQL/AzCopy authentication; Fabric CLI manages its own tokens. A same-tenant import still requires both sign-ins. No recipient source-tenant login or direct cross-tenant OneLake copy is required.

Run **online read-only preflight**:

```powershell
.\scripts\Import-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -TargetMap $targetMap -ReceiptPath $receiptPath `
  -AllowPreviewDataAgent |
    Tee-Object -FilePath (Join-Path $runRoot 'preflight.json')
```

Required result: **`status: planned`**. Review dependency order, names, tenant and target bindings. Without `-Apply`, the command does not write remote data/items or create the initial receipt. It verifies the actual destination, not just JSON syntax.

Before the data copy, exercise the provided SQL-client folder and SQL engine with the shipped synthetic tests:

```powershell
$lakehouseMap = @($target.items | Where-Object { $_.ContainsKey('targetId') })[0]
.\tools\test-fabric-estate-sql.ps1 `
  -LiveServer $lakehouseMap.sqlServer -LiveDatabase $lakehouseMap.sqlDatabase `
  -LiveSqlClientDirectory $sqlClientDirectory
```

This reference map contains exactly one Lakehouse. The live tests execute synthetic `VALUES` queries, not finance-table mutations. Passing them verifies connectivity and the arithmetic contract, not full table visibility or imported data.

**Stop at this gate** if Preview use is not approved, the Lakehouse is not empty, its schema mode/endpoint is wrong, a name collides, dependencies are unresolved, or the tools/permissions fail. Do not disable guards to proceed.

## 7. Apply the import

Obtain the change owner's approval of the plan and confirm the target is still isolated:

```powershell
.\scripts\Import-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -TargetMap $targetMap -ReceiptPath $receiptPath `
  -Apply -TargetIsolated -AllowPreviewDataAgent |
    Tee-Object -FilePath (Join-Path $runRoot 'import-result.json')
```

Required result: **`status: imported`** and a completed receipt. `-TargetIsolated` is your attestation, not a command that changes permissions.

The importer copies physical data and creates supported definitions in dependency order. It does **not** execute notebooks, publish the agent, apply user permissions, change `resolver_release` to target values, or switch the application. `Import-FabricEstate.ps1` has **no** `-SqlClientDirectory` parameter; that parameter belongs to validation.

Keep the receipt and all run evidence. If the command fails or disconnects, follow Section 10 before retrying. A timeout does not prove the remote operation stopped.

## 8. Validate the complete restore before changing it

Keep all target writers and application traffic off. Wait for the generated SQL endpoint to expose all 13 tables. Use the SQL analytics endpoint for read checks; do not attempt to repair Lakehouse Delta data with SQL `INSERT`/`UPDATE`/`DELETE`.

```powershell
.\scripts\Test-FabricEstate.ps1 `
  -PackagePath $packagePath -ManifestSha256 $approvedManifestSha256 `
  -TargetMap $targetMap -ReceiptPath $receiptPath `
  -ReadbackPath $readbackPath -SqlClientDirectory $sqlClientDirectory |
    Tee-Object -FilePath (Join-Path $runRoot 'validation-result.json')
```

Required result: **`status: dataAndDefinitionsValidated`**. Preserve the returned JSON, receipt, readback `validation.json`, SQL comparison and definition evidence.

The validator:

1. Downloads the entire target Tables/Files content and compares paths, file/directory kinds, sizes and file SHA-256, including retained Delta history and empty directories.
2. Compares all captured SQL columns/types and table row counts after synchronization.
3. Compares the captured financial aggregates by period and organization/finance keys, including headcount and GL. Counts alone are not sufficient.
4. Reads back the created definitions and checks approved rebinding.

The report deliberately remains **`cutoverApproved: false`**. Target principals/permissions/policies are not compared as source-identical objects: target identities differ and require separate effective-access acceptance. No public wrapper accepts `-CompareSecurity`.

Do this **before** editing definitions, running activation notebooks or changing the release row. Such changes create a different baseline. Later release tests are separate evidence; they must not erase or replace the pre-activation restore results.

## 9. Complete Fabric configuration and application handover

### 9.1 Rebuild Search from the restored catalogue

This is required for ZavaFinance statement resolution, although Azure AI Search is outside Fabric and outside the export.

The captured active release is:

| Field | Source value |
| --- | --- |
| `catalog_version` | `2026-09-16-v1` |
| `search_index` | `zava-resolver-2026-09-16-v1` |
| `embedding_deployment` | `text-embedding-3-large` |
| `embedding_dimensions` | `1536` |

The copied tuple does **not** prove a target index/model exists. After full restore validation:

1. Provision or select the approved target Search service and embedding deployment. The reference uses **text-embedding-3-large with requested dimensions 1536**. A different model is a separate release change, even if its vector length matches. Enable semantic ranker and approved Entra/RBAC/network access.
2. Give the publisher **Search Service Contributor** and **Search Index Data Contributor** on that Search service, and **Cognitive Services OpenAI User** on the embedding account. Give the runtime only its required read/model roles. These Azure permissions do not grant employee finance access.
3. Use the actual imported catalogue; do **not** run the stage generator to replace it with demo metadata.

```powershell
$importedCatalogVersion = '2026-09-16-v1'
$targetSearchEndpoint = Read-Host 'Target Search HTTPS endpoint'
$targetSearchIndex = Read-Host 'Approved target index name ending in 2026-09-16-v1'
$targetModelEndpoint = Read-Host 'Target embedding HTTPS endpoint (openai.azure.com or cognitiveservices.azure.com)'
$embeddingDeployment = Read-Host 'Approved embedding deployment name'

.\scripts\Publish-ResolverSearch.ps1 `
  -SqlServer $lakehouseMap.sqlServer -SqlDatabase $lakehouseMap.sqlDatabase `
  -CatalogVersion $importedCatalogVersion `
  -SearchEndpoint $targetSearchEndpoint -SearchIndex $targetSearchIndex `
  -EmbeddingEndpoint $targetModelEndpoint -EmbeddingDeployment $embeddingDeployment `
  -EmbeddingDimensions 1536 -SqlClientDirectory $sqlClientDirectory
```

Require correct IDs/count/version, 1536-dimensional vectors and `semanticSearchVerified=true`. Its Zava-specific retrieval probe can reject a changed catalogue; review the release contract rather than suppressing the error.

### 9.2 Approve the target resolver release

After Search verification and controlled allowed/denied-user security tests, have the data-release owner approve the target release tuple.

- If the **same tuple** is intentionally retained, verify that it now resolves to the verified resources through the **target application's** Search/model endpoints. This can avoid changing the copied release row, but still requires explicit owner acceptance.
- If a value must change, use a reviewed version-specific activation process that preserves the imported immutable catalogue and retained history.
- The existing `Publish-ResolverData.ps1 -ActivateRelease` regenerates/revalidates expected rows and can reject curated imported content. It is **not an unconditional migration step**. Do not run either imported notebook blindly, regenerate source metadata, or force-edit the release pointer to clear a failure.

The supported publication sequence and this limitation are documented in [resolver publication](resolver-data.md#publication-after-an-estate-import). If no approved compatible activation procedure exists for the imported catalogue, release is blocked; the data restore is not a workaround.

After any approved activation and SQL synchronization, use the target SQL query editor:

```sql
SELECT catalog_version, search_index, embedding_deployment, embedding_dimensions
FROM dbo.resolver_release;
```

Require exactly one row matching the verified target tuple. Retain the previous tuple and its resources for rollback.

### 9.3 Review and publish the imported Data Agent

Open the imported `Zava-Finance-Agent`, identified by its **new ID in the receipt**, not a similarly named source item.

| Review | Required acceptance |
| --- | --- |
| Instructions and examples | Compare actual imported content with the approved source configuration; preserve business definitions and finance safety rules |
| Sources and selected tables | All intended references resolve to the target Lakehouse/SQL endpoint; table selections match the approved record, not automatically every available table |
| Runtime | Record the exact runtime and feature status; use the approved configuration, not an assumed default or an unapproved Preview runtime |
| External dependencies | Recreate approved connections/credentials/environment dependencies, if any; verify no unintended source-tenant dependency remains |
| Sharing and governance | Apply approved target item/source grants and labels; test SQL and OneLake paths as appropriate |
| Publication | Publish through the supported Fabric experience only after review; record the published workspace/item IDs and actual MCP endpoint |
| Behavior | Test approved finance questions and actual delegated MCP consumption; test both allowed and denied users, not only an administrator in portal chat |

Importing a definition is not proof that the source agent was correct or that the target behaves identically. Do not invent a replacement agent prompt if the approved instructions/runtime evidence is missing. Service-principal support for ALM is not authorization to replace the solution's employee-delegated finance access.

### 9.4 Deliver target bindings to the application team

Extract the imported Data Agent ID from the receipt:

```powershell
$receipt = Get-Content -LiteralPath $receiptPath -Raw |
    ConvertFrom-Json -AsHashtable -Depth 100 -DateKind String
$dataAgentRecord = $receipt.items['6437efd8-ea33-4924-a115-3a970a10facf']
[ordered]@{
    FABRIC_WORKSPACE_ID = $dataAgentRecord.workspaceId
    FABRIC_DATA_AGENT_ID = $dataAgentRecord.targetId
    FABRIC_SQL_ENDPOINT = $lakehouseMap.sqlServer
    FABRIC_DATABASE = $lakehouseMap.sqlDatabase
} | ConvertTo-Json |
    Tee-Object -FilePath (Join-Path $runRoot 'application-fabric-bindings.json')
```

These four keys are shared **deployment inputs** and Python runtime settings. The .NET runtime uses:

| Deployment/Python setting | .NET runtime setting |
| --- | --- |
| `FABRIC_WORKSPACE_ID` | `Fabric__WorkspaceId` |
| `FABRIC_DATA_AGENT_ID` | `Fabric__DataAgentId` |
| `FABRIC_SQL_ENDPOINT` | `Fabric__SqlEndpoint` |
| `FABRIC_DATABASE` | `Fabric__Database` |

Also hand over the target Search/embedding endpoints and approved release tuple. For a tenant change, the identity/application team must update the finance Entra registration, OBO tenant/client/audience, downstream delegated consent, credentials and channel identity configuration; changing only four Fabric inputs is insufficient.

Follow [the Entra procedure and customer-admin handback checklist](customer-it-installation-guide.md#8-microsoft-entra-app-registration-and-consent), its [portal screenshot walkthrough](customer-it-installation-guide.md#22-entra-administrator-screenshot-reference), and [runtime configuration mappings](customer-it-installation-guide.md#shared-deployment-inputs-and-runtime-mappings). The application's SQL token resource is `https://database.windows.net/.default`; Data Agent access uses `https://api.fabric.microsoft.com/.default`. Consent and effective user data grants are separate. Do not grant the runtime broad finance access instead of passing the employee's delegated identity.

KPIpedia, Foundry deployment, Bots and Teams/Microsoft 365 packages remain separate installation deliverables. The existing R1-R4 handover gates still apply to the selected scope; a successful Fabric restore is evidence toward them, not automatic closure.

## 10. Failure handling, acceptance and rollback

| Situation | Required action |
| --- | --- |
| Package/hash/path failure | Stop. Check custody/extraction and contact the sender. Do not edit the package or approve a replacement hash locally |
| Wrong schema, endpoint, tenant or nonempty target | Correct the configuration or create a new compatible empty target; rerun read-only preflight before apply |
| Interrupted/in-progress import | Inspect the remote job and partial target first. Do not reset/edit the receipt, blindly rerun create/copy, or resume AzCopy to bypass the guard. A fresh attempt uses new empty targets, an updated map and a new receipt |
| SQL tables/types/counts not yet synchronized | Keep writers off, wait/check the target endpoint and permissions, then use a **new nonexistent readback path** for another validation; do not rerun import over the target |
| Hash or financial/definition mismatch | Preserve evidence and investigate the real difference; no rounding, reseeding or weakened comparator |
| AzCopy reports skipped transfers | Only the narrowly checked pre-existing destination-root property skip can be accepted; skipped files or ambiguous evidence block acceptance. See the [technical rules](fabric-estate-migration.md#azcopy-completion-content-not-directory-properties-or-acls) |
| Search, publication or user access test fails | Keep application traffic off; route the failure to the appropriate owner. `imported` is not a release approval |

Before users are admitted, collect:

- [ ] Approved handover inventory, code/dependency versions/hashes, source capture interval and trusted manifest checksum.
- [ ] Target tenant/subscription/capacity/workspace/Lakehouse/SQL worksheet and approved tenant/network/data-residency settings.
- [ ] Frozen approved map, successful preflight, completed receipt and full readback/SQL/definition evidence.
- [ ] Independent finance reconciliation and approval of the captured business state.
- [ ] Target workspace/item/OneLake/SQL/label review and both allowed/denied-user tests; remove time-bounded migration privileges when no longer needed.
- [ ] Validated Search/embedding projection and approved resolver release; no unintended source endpoints.
- [ ] Reviewed and published Data Agent, runtime/configuration record and delegated MCP test results.
- [ ] Application settings/consent/channel acceptance and named release/rollback owners.

Authorize application cutover through the change process; **do not rewrite `cutoverApproved=false` in the migration report**. Keep pre-activation restore evidence and later release approval as separate records.

Rollback normally returns application configuration/routing to the previous approved environment. It does not overwrite restored data or delete the source. Reconcile any target writes with finance before rollback; no reverse synchronization is supplied. Package retention and deletion of staging/failed resources are separately approved operations.

## Official references

- [Buy Fabric capacity and tenant/subscription prerequisites](https://learn.microsoft.com/fabric/enterprise/buy-capacity)
- [Data Agent tenant settings](https://learn.microsoft.com/fabric/data-science/data-agent-tenant-settings)
- [Create/configure a Data Agent](https://learn.microsoft.com/fabric/data-science/how-to-create-data-agent)
- [Workspace role-inventory API permissions](https://learn.microsoft.com/rest/api/fabric/core/workspaces/list-workspace-role-assignments)
- [Data Agent ALM/source control - Preview](https://learn.microsoft.com/fabric/data-science/data-agent-source-control)
- [Lakehouse Git/deployment scope: metadata, not data](https://learn.microsoft.com/fabric/data-engineering/lakehouse-git-deployment-pipelines)
- [OneLake and AzCopy](https://learn.microsoft.com/fabric/onelake/onelake-azcopy)
