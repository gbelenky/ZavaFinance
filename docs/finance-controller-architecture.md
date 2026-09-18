# Finance ownership and financial controls

For finance analysts and controllers who own ZavaFinance's behavior and code.
Choose [.NET](../src/dotnet/README.md) or [Python](../src/python/README.md) for
development; both implement the same [architecture](architecture-diagram.md).
The [service guide](azure-services-for-controllers.md) explains the platforms,
and the [IT catalogue](it-admin-catalogue.md) defines administrative responsibilities.

## What the agent does

ZavaFinance is a controlled interface to finance systems, not a model replacing a
calculator. It interprets the question, selects at most one tool, and delivers that
tool's answer without a final rewrite.

| Question | Tool | How finance should assess the answer |
| --- | --- | --- |
| “What does gross margin mean?” | `get_kpi_info` — Copilot Studio KPIpedia | Check cited definitions against approved finance policy |
| “What was gross margin for EMEA in November 2025?” | `get_statement` — metadata resolver and Fabric SQL | Reconcile KPI, scope, period, formula and values against source facts |
| “Why did margin fall across regions?” | `explore_finance` — Fabric Data Agent MCP | Review the analytical narrative and independently check its evidence and arithmetic |

The outer model cannot generate statement SQL or calculate statement figures.
Fabric analysis and KPIpedia can use their own models; returning their answer
verbatim does not certify that an analytical narrative is financially correct.

## Follow a statement

For “margin for EMEA in November 2025”:

1. The agent passes the user's KPI, organization and period terms to the statement
   tool under the signed-in person's identity.
2. The resolver reads Fabric's active terminology release. “Margin” can identify
   several measures, so the user receives authorized, numbered choices.
3. The user clicks a row or types its number. The application validates ownership,
   expiry, request and catalogue binding; a display label alone cannot execute.
4. The selected reporting branch expands to distinct region/department fact keys.
   Fixed, parameterized SQL reads the authorized source facts.
5. Reviewed `decimal`/`Decimal` calculations format the result, comparison,
   units, formula and provenance. The answer is not sent to the outer model for
   rewriting.

Search helps find terms but is not the source of financial facts or permission
decisions. Similarity does not mean equivalence: fuzzy candidates require confirmation.

## Financial controls to preserve

| Control | Meaning |
| --- | --- |
| Business definitions | Fabric metadata, calculator code and KPIpedia must agree |
| Explicit scope | Company, regional and global branches resolve to distinct fact-key sets; overlapping nodes must not duplicate facts |
| Ratios | Aggregate the reviewed numerator and denominator at the requested scope, then calculate; do not average displayed percentages |
| Decimal arithmetic | Perform financial calculations in application code with explicit rounding and units |
| Missing data | Distinguish missing/unsupported data and zero denominators from genuine zero values |
| Periods | Use deterministic `[start,end)` boundaries and approved comparison rules; future synthetic rows do not imply a closed reporting period |
| Follow-ups | Explicit periods win; yearless periods use the relevant conversational year or the current UTC year; unsupported qualifiers prompt clarification |
| Provenance | Keep the selected measure, scope, period, formula and source clear to the user |
| Release binding | Catalogue version, Search index, embedding deployment and dimensions form one coherent release |
| Authorization | Validate metadata visibility before disclosure and financial scope again at execution |

Decimal conversion cannot recover precision already lost in source FLOAT values.
Source types, ingestion quality and reconciliation remain finance/data responsibilities.

## Identity and privacy in plain language

The application has two distinct identities:

- Its **managed identity** accesses model deployments, embeddings and the metadata
  Search index.
- The **employee's delegated identity** authorizes Fabric and KPIpedia requests.

A card selection, username in a prompt, or successful Bot connection is not finance
authorization. Search does not inherit Fabric's per-user restrictions; candidate
visibility must be rechecked through delegated Fabric access.

HMAC-derived caller keys isolate conversation state. Persisted history contains
allowed intent and continuation metadata, not user tokens or permissioned tool-result
bodies. Questions and approved terminology can still reach configured AI services,
and delivered answers exist in the Teams/M365 transcript. Apply retention, residency
and development-tool policies to those surfaces as well.

Each language implementation uses its own Bot, package and state. An authorized
OAuth client can be reused, but sharing it does not merge the two conversations.

## Ownership

| Area | Finance/code owner | Platform/data partner |
| --- | --- | --- |
| Agent behavior | Tool boundaries, prompts, follow-ups and acceptance tests | Approved runtime, models and package support |
| Resolver | Vocabulary, ambiguity and reportable hierarchy | Catalogue publication and metadata visibility enforcement |
| Calculator | Formulas, scope semantics, units and rounding | Source data quality and secure SQL access |
| Activity experience | Sign-in UX, typed/clicked choices and reply behavior | Bot registration, OAuth and tenant app policy |
| Releases | Review code and reconcile known-answer examples | Build/deploy, scoped permissions and release evidence |
| Operations | Define user impact and retry guidance | Telemetry, incident response and approved environment controls |

## Making a business change

| Change | Required work |
| --- | --- |
| Add an alias | Update curated metadata, test ambiguity, publish a new immutable catalogue/Search release |
| Add a reporting branch | Verify distinct fact coverage, authorization and reconciled totals |
| Add/change a KPI | Review calculator, supported mapping, source metadata, KPIpedia and tests together |
| Load new monthly facts | Validate ingestion and finance reconciliation; update terminology only when dimensions/scopes change |
| Change a clarification card | Preserve caller/request/option/release binding and test typed and clicked responses |
| Change a model, identity or network | Joint IT/security review and regression testing, not a prompt-only edit |

Follow [catalogue publication](resolver-data.md): stage, verify SQL, publish/probe
Search, then activate. Never activate a Search copy merely because its upload
request succeeded.

## Acceptance and operating limits

Finance should approve representative definitions, explicit and contextual periods,
ratios, missing-data behavior, ambiguous names and reporting scopes. Test with both
permitted and restricted users, including attempted cross-user card submissions.

Process-owned work can be interrupted by a restart. Saved conversation memory is
not a saved executing task; the user may need to retry. `reset` clears finance memory,
not sign-in or deployment selection. The [operations record](operations.md) separates
local tests, live delivery and independent financial verification.

Development state is **Public Preview with no SLA**. Staging and production are
planned independently with **GA Cosmos DB for NoSQL**, but their adapter and
deployments are not implemented. The Activity API remains **Preview** regardless
of the chosen database. Agree support and continuity requirements before promotion.
