"""Additive, versioned resolver metadata. Executed in Fabric by Publish-ResolverData.ps1."""

import json
import re


KPI_METADATA = {
    "KPI-001": ("Revenue", ["gross revenue", "gross sales", "revenue"], "Sales before revenue deductions."),
    "KPI-002": ("Revenue", ["revenue deductions", "discounts and returns"], "Discounts, returns and other deductions from gross revenue."),
    "KPI-003": ("Revenue", ["net revenue", "net sales", "sales", "revenue"], "Gross revenue less revenue deductions."),
    "KPI-004": ("Costs", ["cogs", "cost of goods sold", "cost of sales"], "Costs directly attributable to goods or services sold."),
    "KPI-005": ("Profitability", ["gross profit"], "Net revenue less cost of goods sold."),
    "KPI-006": ("Profitability", ["gross margin", "gross margin percentage", "margin"], "Gross profit divided by net revenue, expressed as a percentage."),
    "KPI-007": ("Costs", ["opex", "operating expenses", "operating costs"], "Operating costs excluding cost of goods sold and depreciation and amortisation."),
    "KPI-008": ("Profitability", ["ebitda"], "Gross profit less operating expenses."),
    "KPI-009": ("Profitability", ["ebitda margin", "margin"], "EBITDA divided by net revenue, expressed as a percentage."),
    "KPI-010": ("Costs", ["depreciation and amortisation", "depreciation and amortization", "d&a"], "Depreciation and amortisation expense."),
    "KPI-011": ("Profitability", ["operating income", "ebit", "operating profit"], "EBITDA less depreciation and amortisation."),
    "KPI-012": ("Profitability", ["operating margin", "ebit margin", "margin"], "Operating income divided by net revenue, expressed as a percentage."),
    "KPI-013": ("People", ["headcount", "fte", "full time equivalents", "staff count"], "Closing-month full-time equivalent employees; not summed over months."),
    "KPI-014": ("People", ["revenue per fte", "revenue per employee", "employee productivity"], "Net revenue divided by closing-month full-time equivalent employees."),
    "KPI-015": ("Working capital", ["dso", "days sales outstanding", "collection days"], "Revenue-weighted monthly days-sales-outstanding measure; receivables are not available in this demo."),
    "KPI-016": ("Budget and growth", ["budget variance", "budget variance percentage"], "Actual net revenue less budget net revenue, divided by budget net revenue, as a percentage."),
    "KPI-017": ("Budget and growth", ["net revenue yoy growth", "revenue growth", "year over year growth"], "Net revenue change against the same period in the preceding year, as a percentage."),
    "KPI-018": ("Budget and growth", ["budget net revenue", "budget revenue", "revenue budget"], "Planned net revenue, additive across the selected reporting scope and period."),
}

REGION_ALIASES = {
    "AMER": ["Americas", "North America"],
    "APAC": ["Asia Pacific", "Asia"],
    "EMEA": ["Europe", "Europe Middle East and Africa"],
    "LATAM": ["Latin America"],
}

DEPARTMENT_ALIASES = {
    "D100": ["retail"], "D110": ["wholesale", "distribution"],
    "D120": ["ecommerce", "e-commerce", "online sales"],
    "D130": ["professional services", "consulting"],
    "D200": ["manufacturing", "production"],
    "D210": ["supply chain", "logistics"], "D220": ["procurement", "purchasing"],
    "D300": ["marketing"], "D310": ["research and development", "r&d"],
    "D320": ["customer service", "customer support"],
    "D400": ["finance", "controlling"], "D410": ["hr", "human resources"],
    "D420": ["it", "information technology"], "D430": ["legal", "compliance"],
}


def slug(value):
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


def build_catalog(regions, departments, kpis, pairs, version):
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9.-]{0,63}", version):
        raise ValueError("A safe, nonempty catalogue version is required.")
    regions = sorted(regions, key=lambda x: x["region_code"])
    departments = sorted(departments, key=lambda x: x["department_code"])
    kpis = sorted(kpis, key=lambda x: x["kpi_code"])
    if {k["kpi_code"] for k in kpis} != set(KPI_METADATA):
        raise ValueError("Review executable KPI mappings before publishing changed KPI codes.")
    known_pairs = {(r["region_code"], d["department_code"]) for r in regions for d in departments}
    pairs = set(pairs)
    if not pairs or not pairs <= known_pairs:
        raise ValueError("Fact scopes contain unknown or no dimension keys.")
    entities, scopes = [], []

    def add(entity_id, kind, name, aliases, definition, parent_id=None, **fields):
        parent = next((x for x in entities if x["entity_id"] == parent_id), None)
        path = f'{parent["hierarchy_path"]} > {name}' if parent else name
        entity = {
            "catalog_version": version, "entity_id": entity_id, "entity_kind": kind,
            "canonical_name": name, "aliases_json": json.dumps(sorted(set([name, entity_id] + aliases))),
            "definition": definition, "parent_id": parent_id, "hierarchy_path": path,
            "kpi_code": None, "region_code": None, "department_code": None,
            "department_group": None, "is_reportable": kind != "kpi_group",
        }
        entity.update(fields)
        entities.append(entity)
        return entity_id

    def scope(node_id, region=None, department=None, group=None):
        group_codes = {d["department_code"] for d in departments if group is None or d["department_group"] == group}
        covered = sorted((r, d) for r, d in pairs if
                         (region is None or r == region) and
                         (department is None or d == department) and d in group_codes)
        if not covered:
            raise ValueError(f"Organization {node_id} has no fact coverage.")
        scopes.extend({"catalog_version": version, "node_id": node_id, "region_code": r, "department_code": d}
                      for r, d in covered)

    root = add("org-company", "org", "Zava Finance", ["company", "all", "global", "worldwide", "zava"],
               "All existing reporting region and department combinations. Reporting scope, not legal ownership.")
    scope(root)
    groups = sorted({d["department_group"] for d in departments})
    for r in regions:
        rc, rn = r["region_code"], r["region_name"]
        rid = add(f"org-region-{slug(rc)}", "org", rn, [rc] + REGION_ALIASES.get(rc, []),
                  f"All reporting departments in {rn}.", root, region_code=rc)
        scope(rid, region=rc)
        for group in groups:
            gid = add(f"{rid}-group-{slug(group)}", "org", group, [group, f"{rc} {group}", f"{rn} {group}"],
                      f"{group} reporting departments in {rn}.", rid, region_code=rc, department_group=group)
            scope(gid, region=rc, group=group)
            for d in (d for d in departments if d["department_group"] == group):
                dc, dn = d["department_code"], d["department_name"]
                aliases = DEPARTMENT_ALIASES.get(dc, []) + [dc, f"{rc} {dc}", f"{rc} {dn}", f"{dn} in {rn}"]
                did = add(f"{rid}-department-{slug(dc)}", "org", dn, aliases,
                          f"{dn} within {group}, {rn}.", gid,
                          region_code=rc, department_code=dc, department_group=group)
                scope(did, region=rc, department=dc)
    for group in groups:
        gid = add(f"org-global-group-{slug(group)}", "org", f"Global {group}",
                  [group, f"all {group}"], f"{group} across all reporting regions.", root,
                  department_group=group)
        scope(gid, group=group)
    for d in departments:
        dc, dn, group = d["department_code"], d["department_name"], d["department_group"]
        did = add(f"org-global-department-{slug(dc)}", "org", f"Global {dn}",
                  [dn, dc, f"all {dn}"] + DEPARTMENT_ALIASES.get(dc, []),
                  f"{dn} across all reporting regions.", f"org-global-group-{slug(group)}",
                  department_code=dc, department_group=group)
        scope(did, department=dc)
    for family in sorted({m[0] for m in KPI_METADATA.values()}):
        add(f"kpi-group-{slug(family)}", "kpi_group", family, [],
            "Navigation family only. Select a specific measure; never sum member KPI values.")
    for k in kpis:
        code = k["kpi_code"]
        family, aliases, definition = KPI_METADATA[code]
        add(f"kpi-{code.lower()}", "kpi", k["kpi_name"], aliases + [code],
            f'{definition} Unit: {k["unit"]}. Aggregation: {k["aggregation"]}.',
            f"kpi-group-{slug(family)}", kpi_code=code)
    validate_catalog(entities, scopes, pairs)
    return entities, scopes


def validate_catalog(entities, scopes, fact_pairs):
    by_id = {e["entity_id"]: e for e in entities}
    if len(by_id) != len(entities):
        raise ValueError("Duplicate stable entity IDs.")
    for entity in entities:
        seen = {entity["entity_id"]}
        parent = entity["parent_id"]
        while parent:
            if parent not in by_id or parent in seen:
                raise ValueError("Missing parent or cyclic hierarchy.")
            seen.add(parent)
            parent = by_id[parent]["parent_id"]
        aliases = json.loads(entity["aliases_json"])
        if not aliases or not all(isinstance(a, str) and a.strip() for a in aliases):
            raise ValueError("Empty or invalid alias.")
    keys = {(s["node_id"], s["region_code"], s["department_code"]) for s in scopes}
    if len(keys) != len(scopes):
        raise ValueError("Duplicate scope key would multiply facts.")
    for s in scopes:
        if s["node_id"] not in by_id or by_id[s["node_id"]]["entity_kind"] != "org":
            raise ValueError("Scope references a non-organization entity.")
        if (s["region_code"], s["department_code"]) not in fact_pairs:
            raise ValueError("Scope contains a fact key not present in the source.")
    company = {(r, d) for n, r, d in keys if n == "org-company"}
    if company != fact_pairs:
        raise ValueError("Company coverage differs from existing facts.")
    for e in entities:
        if e["entity_kind"] == "org" and not any(n == e["entity_id"] for n, _, _ in keys):
            raise ValueError("Reportable organization has no scope.")


def publish(spark, settings):
    from delta.tables import DeltaTable
    from pyspark.sql import functions as F

    workspace, lakehouse, version = settings["workspace_id"], settings["lakehouse_id"], settings["catalog_version"]
    root = f"abfss://{workspace}@onelake.dfs.fabric.microsoft.com/{lakehouse}/Tables"
    sources = ["dim_region", "dim_department", "dim_kpi", "dim_date",
               "fact_finance_monthly", "fact_kpi_monthly", "fact_headcount_monthly", "fact_gl_transaction", "dim_account"]

    def source_versions():
        return {name: DeltaTable.forPath(spark, f"{root}/{name}").history(1).select("version").first()[0]
                for name in sources}

    baseline = source_versions()
    def read(name):
        return spark.read.format("delta").option("versionAsOf", baseline[name]).load(f"{root}/{name}")

    regions = [r.asDict() for r in read("dim_region").collect()]
    departments = [r.asDict() for r in read("dim_department").collect()]
    kpis = [r.asDict() for r in read("dim_kpi").collect()]
    pairs = {(r[0], r[1]) for r in read("fact_finance_monthly").select("region_code", "department_code").distinct().collect()}
    entities, scopes = build_catalog(regions, departments, kpis, pairs, version)
    catalog_schema = ("catalog_version string, entity_id string, entity_kind string, canonical_name string, "
                      "aliases_json string, definition string, parent_id string, hierarchy_path string, "
                      "kpi_code string, region_code string, department_code string, department_group string, is_reportable boolean")
    scope_schema = "catalog_version string, node_id string, region_code string, department_code string"
    def stage(name, rows, schema):
        frame = spark.createDataFrame(rows, schema)
        path = f"{root}/{name}"
        if DeltaTable.isDeltaTable(spark, path):
            existing = spark.read.format("delta").load(path).where(F.col("catalog_version") == version).select(frame.columns)
            if existing.limit(1).count():
                if existing.exceptAll(frame).limit(1).count() or frame.exceptAll(existing).limit(1).count():
                    raise ValueError(f"{version} is immutable; publish a new version instead of changing {name}.")
                return
        frame.write.format("delta").mode("append").save(path)

    stage("resolver_catalog", entities, catalog_schema)
    stage("resolver_scope", scopes, scope_schema)
    calendar = [{"catalog_version": version, "fiscal_year_start_month": 1,
                 "period_convention": "[start,end)", "closed_month_lag": 0,
                 "closure_policy": "Explicit dates only; future synthetic rows do not imply a closed period."}]
    stage("resolver_calendar", calendar, "catalog_version string, fiscal_year_start_month int, period_convention string, closed_month_lag int, closure_policy string")
    if source_versions() != baseline:
        raise RuntimeError("Source tables changed during publication. Review and retry with a new catalogue version.")
    release_schema = "catalog_version string, search_index string, embedding_deployment string, embedding_dimensions int"
    release_path = f"{root}/resolver_release"
    if settings["activate_release"]:
        if not settings["search_index"]:
            raise ValueError("A verified version-specific Search index is required to activate a release.")
        spark.createDataFrame([{"catalog_version": version, "search_index": settings["search_index"],
                                "embedding_deployment": settings["embedding_deployment"],
                                "embedding_dimensions": settings["embedding_dimensions"]}], release_schema
                              ).write.format("delta").mode("overwrite").save(release_path)
    elif not DeltaTable.isDeltaTable(spark, release_path):
        spark.createDataFrame([], release_schema).write.format("delta").mode("errorifexists").save(release_path)
    result = {"catalog_version": version, "entities": len(entities), "scope_rows": len(scopes),
              "fact_pairs": len(pairs), "source_delta_versions": baseline, "original_tables_unchanged": True,
              "release_activated": settings["activate_release"]}
    print(json.dumps(result))
    return result


if "spark" in globals() and "resolver_settings" in globals():
    from notebookutils import mssparkutils
    mssparkutils.notebook.exit(json.dumps(publish(spark, resolver_settings)))
