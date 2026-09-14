"""
Computes reference KPI values from the generated Zava parquet files.

These are the same tables that were written to the lakehouse, so the numbers here are the
ground truth the .NET aggregation must reproduce. This exists because the SQL analytics
endpoint is TDS-only and outbound 1433 is blocked on the development network, so the query
itself cannot be executed locally -- but its *arithmetic* can still be pinned.

Mirrors FabricStatementQuery + KpiCalculator: sum additive components, then recompute ratios.

Usage:
    python tools/reference_values.py <path-to-parquet-folder>
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pandas as pd

OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "out")

finance = pd.read_parquet(OUT / "fact_finance_monthly.parquet")
kpi = pd.read_parquet(OUT / "fact_kpi_monthly.parquet")


def components(region: str | None, start: str, end: str) -> dict:
    """Half-open [start, end) month range, matching FinancePeriod."""
    f = finance[(finance.month_start >= start) & (finance.month_start < end)]
    k = kpi[(kpi.month_start >= start) & (kpi.month_start < end)]

    if region:
        f = f[f.region_code == region]
        k = k[k.region_code == region]

    def comp(name: str) -> float:
        return float(f[f.kpi_component == name].amount_usd.sum())

    gross = comp("GROSS_REVENUE")
    deductions = comp("REVENUE_DEDUCTIONS")
    cogs = comp("COGS")
    opex = comp("OPEX")
    da = comp("DA")

    net = gross - deductions
    gross_profit = net - cogs
    ebitda = gross_profit - opex
    operating_income = ebitda - da

    budget = float(k[k.kpi_code == "KPI-018"].kpi_value.sum())

    # Headcount is semi-additive: closing month only, summed across organizations.
    closing = k.month_start.max()
    headcount = float(
        k[(k.kpi_code == "KPI-013") & (k.month_start == closing)].kpi_value.sum()
    )

    return {
        "gross_revenue": round(gross, 2),
        "revenue_deductions": round(deductions, 2),
        "net_revenue": round(net, 2),
        "cogs": round(cogs, 2),
        "gross_profit": round(gross_profit, 2),
        "opex": round(opex, 2),
        "ebitda": round(ebitda, 2),
        "da": round(da, 2),
        "operating_income": round(operating_income, 2),
        "budget_net_revenue": round(budget, 2),
        "closing_headcount": headcount,
        "gross_margin_pct": None if net == 0 else round(gross_profit / net * 100, 2),
        "operating_margin_pct": None if net == 0 else round(operating_income / net * 100, 2),
        "revenue_per_fte": None if headcount == 0 else round(net / headcount, 2),
    }


CASES = {
    "EMEA 2025-11": ("EMEA", "2025-11-01", "2025-12-01"),
    "EMEA Q4 2025": ("EMEA", "2025-10-01", "2026-01-01"),
    "APAC Q3 2026": ("APAC", "2026-07-01", "2026-10-01"),
    "ALL 2025": (None, "2025-01-01", "2026-01-01"),
}

result = {name: components(*args) for name, args in CASES.items()}
print(json.dumps(result, indent=2))

# The aggregation trap, demonstrated on the real data: averaging monthly margins is not the
# same as the margin computed from summed components.
bounds = ["2025-10-01", "2025-11-01", "2025-12-01", "2026-01-01"]
monthly = [
    components("EMEA", bounds[i], bounds[i + 1])["gross_margin_pct"] for i in range(3)
]
naive = round(sum(monthly) / len(monthly), 2)
correct = result["EMEA Q4 2025"]["gross_margin_pct"]

print(
    f"\nEMEA Q4 2025 gross margin -- monthly {monthly} "
    f"naive-average {naive} vs correct {correct}",
    file=sys.stderr,
)
