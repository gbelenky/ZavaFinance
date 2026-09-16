# Resolver catalogue publication

Fabric owns the terminology. Azure AI Search is a rebuildable retrieval projection, not the
authority for organization membership, KPI execution or user access. The resolver runs inside
the existing hosted agent; there is no separate resolver Function.

## Reporting model

The additive organization tree is **Company > Region > Department group > Department**.
Global group and department nodes provide cross-region scopes. These are reporting views of
the existing fact keys, not invented legal ownership. Their scopes overlap deliberately.
Each query uses `EXISTS` against distinct `(region_code, department_code)` pairs; it must not join
overlapping nodes into facts and multiply financial amounts.

For the current demo, the export contains 90 organization nodes, 18 executable KPIs and six
non-executable KPI families: 114 entities and 336 node-to-fact-key mappings. The original nine
Delta tables, 18 KPI codes, financial formulas and facts are unchanged.

Aliases and explanatory definitions in [the publisher](../scripts/fabric/resolver_catalog.py)
are independently curated Zava metadata. Names, existing codes, groups, units, aggregation
labels and fact-key coverage are read from the Fabric dimensions/facts, not another deployment.
The current generator deliberately fails if the executable KPI set changes. Review the
calculator and business definitions before publishing a new measure.

`margin` matches three measures. Repeated department labels (for example `IT`) match regional
paths and their global scope. Ambiguous aliases are retained rather than assigned to the first
match. Region-qualified names and codes are additional aliases.

## Additive Delta tables

| Table | Key / contents |
|---|---|
| `resolver_catalog` | `(catalog_version, entity_id)`; kind, canonical name, aliases JSON, definition, parent, full path, KPI code or organization attributes, reportable flag |
| `resolver_scope` | `(catalog_version, node_id, region_code, department_code)`; distinct fact coverage |
| `resolver_calendar` | Versioned calendar conventions: January fiscal start, `[start,end)`, no inferred close from future synthetic rows |
| `resolver_release` | Exactly one active version, its Search index and embedding deployment/dimensions; empty before initial activation |

IDs are based on existing codes, never aliases. Department group/family IDs use normalized
group names because those source dimensions have no group key; a group rename therefore
requires a reviewed new catalogue version and invalidates old pending selections.
Catalogue versions are immutable. Retrying a stage is allowed only if every stored row
matches. Older versions remain available for audit; they must not silently execute stale cards.

## Prerequisites

- PowerShell 7 and Azure CLI signed into the intended tenant and subscription.
- Existing project dependencies built in `Release`:
  `dotnet build .\tests\ZavaFinance.Tests\ZavaFinance.Tests.csproj -c Release`.
  The publication scripts reuse its existing SQL client; no separate SQL module is installed.
- Fabric contributor rights to create/run the version-specific publication notebook, active
  capacity, and permission to read/write the lakehouse. SQL SELECT permissions alone do not
  authorize Spark/OneLake exports.
- The publisher has **Search Service Contributor** and **Search Index Data Contributor**
  on the dedicated Search service, and **Cognitive Services OpenAI User** on the embedding
  account. Runtime needs Search read permission, not these publisher write roles.
- The Search service has semantic ranker enabled and a network path for the publisher/runtime.
  The embedding deployment must match the runtime vector dimensions and data-residency policy.
- Run from a network allowed to reach SQL, Search, model and Fabric endpoints.
  Do not open firewalls or replace delegated financial access with a publisher identity.

## Stage, verify, index, activate

Use IDs/endpoints for the **same environment** throughout. The names below are PowerShell
variables supplied by the administrator, not committed credentials.

```powershell
.\scripts\Publish-ResolverData.ps1 `
  -WorkspaceId $workspaceId -LakehouseId $lakehouseId -CatalogVersion $version

.\scripts\Test-ResolverData.ps1 `
  -SqlServer $sqlServer -SqlDatabase $database -CatalogVersion $version `
  -VerifyDemoReferenceValues

.\scripts\Publish-ResolverSearch.ps1 `
  -SqlServer $sqlServer -SqlDatabase $database -CatalogVersion $version `
  -SearchEndpoint $searchEndpoint -SearchIndex $versionedIndex `
  -EmbeddingEndpoint $modelEndpoint -EmbeddingDeployment text-embedding-3-large `
  -EmbeddingDimensions 1536

.\scripts\Publish-ResolverData.ps1 `
  -WorkspaceId $workspaceId -LakehouseId $lakehouseId -CatalogVersion $version `
  -SearchIndex $versionedIndex -EmbeddingDeployment text-embedding-3-large `
  -EmbeddingDimensions 1536 -ActivateRelease
```

The Search index name must end with the lower-case version, with dots replaced by hyphens.
For example, version `2026-09-16-v1` uses `zava-resolver-2026-09-16-v1`.

1. The stage notebook reads source Delta snapshots, validates parent links, cycles, aliases,
   measure mappings and scope coverage, then appends only the four resolver tables.
2. Original source Delta versions are checked again before publication completes. A concurrent
   source change is an explicit failure, not a successful partial release.
3. Wait for the Fabric SQL endpoint to synchronize the new tables. A completed Spark job is
   not proof that SQL is ready. The SQL test checks exact counts, scope integrity and optional
   existing Zava November 2025 reference figures. Its expected counts describe this demo;
   update the assertions deliberately when changing the reporting model.
4. The Search publisher creates a version-specific index, generates vectors using Entra auth,
   checks every document upload result and exact IDs/count/version, and probes actual keyword
   + vector + semantic reranking. The gross-margin paraphrase must retrieve the expected KPI
   among the top three candidates; rank one is not treated as proof of identity. Every fuzzy
   suggestion still requires confirmation in the resolver. Failed publication leaves the old
   release active.
5. Only after verification, activate the singleton release row atomically in Delta. Keep the
   prior index and catalogue until no old requests require them. There is no automatic
   cleanup/delete operation in these scripts.

Each stage/activation retains its own version-specific notebook in the Fabric workspace.
A timeout reports the operation URL: inspect the existing job rather than starting another
blindly. The scripts never print tokens or write them to a notebook.

## Authorization and operations

Search data is curated metadata, but it can still be sensitive. The Search managed identity
does **not** inherit a user's Fabric SQL security. The agent must filter/revalidate candidate IDs
using delegated SQL visibility before exposing paths/descriptions or asking a model to choose.
The final fact queries and clarification submissions must revalidate the active catalogue
version and permitted organization scope. Do not grant all users broad SQL table permissions
as a substitute for this check.

Treat the publication account as privileged data engineering automation. Use separate
identities and resources for dev, staging and prod, review metadata visibility explicitly, and
use workload identity/certificates for unattended publication rather than saved user tokens.
The [IT-admin catalogue](it-admin-catalogue.md) covers the infrastructure and consent gates.

Offline generator checks:

```powershell
python -B -m unittest discover -s .\tools -p test_resolver_catalog.py -v
```

No lifecycle inference is made from the maximum fact date. The demo includes future synthetic
months. Calendar interpretation must remain deterministic and reject unsupported phrases.
