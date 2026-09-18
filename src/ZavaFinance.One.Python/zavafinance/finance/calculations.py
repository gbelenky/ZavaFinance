"""Executable KPI arithmetic; business vocabulary is loaded from Fabric, not this list."""

from dataclasses import dataclass
from decimal import Decimal, ROUND_HALF_EVEN, localcontext
from enum import StrEnum


class KpiUnit(StrEnum):
    CURRENCY = "currency"
    PERCENT = "percent"
    COUNT = "count"
    DAYS = "days"


class KpiAggregation(StrEnum):
    ADDITIVE = "additive"
    RATIO = "ratio"
    SEMI_ADDITIVE = "semi_additive"


@dataclass(frozen=True)
class KpiDefinition:
    code: str
    name: str
    unit: KpiUnit
    aggregation: KpiAggregation
    formula: str


class KpiCatalog:
    all = (
        KpiDefinition("KPI-001", "Gross Revenue", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "SUM(accounts 4000, 4010)"),
        KpiDefinition("KPI-002", "Revenue Deductions", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "SUM(accounts 4100, 4110, 4120)"),
        KpiDefinition("KPI-003", "Net Revenue", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "Gross Revenue - Revenue Deductions"),
        KpiDefinition("KPI-004", "COGS", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "SUM(accounts 5000, 5010, 5020, 5030)"),
        KpiDefinition("KPI-005", "Gross Profit", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "Net Revenue - COGS"),
        KpiDefinition("KPI-006", "Gross Margin %", KpiUnit.PERCENT, KpiAggregation.RATIO, "Gross Profit / Net Revenue"),
        KpiDefinition("KPI-007", "Operating Expenses", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "SUM(accounts 6000-6050)"),
        KpiDefinition("KPI-008", "EBITDA", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "Gross Profit - Operating Expenses"),
        KpiDefinition("KPI-009", "EBITDA Margin %", KpiUnit.PERCENT, KpiAggregation.RATIO, "EBITDA / Net Revenue"),
        KpiDefinition("KPI-010", "Depreciation & Amortisation", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "SUM(account 7000)"),
        KpiDefinition("KPI-011", "Operating Income (EBIT)", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "EBITDA - D&A"),
        KpiDefinition("KPI-012", "Operating Margin %", KpiUnit.PERCENT, KpiAggregation.RATIO, "Operating Income / Net Revenue"),
        KpiDefinition("KPI-013", "Headcount (FTE)", KpiUnit.COUNT, KpiAggregation.SEMI_ADDITIVE, "Period-end FTE"),
        KpiDefinition("KPI-014", "Revenue per FTE", KpiUnit.CURRENCY, KpiAggregation.RATIO, "Net Revenue / Headcount"),
        KpiDefinition("KPI-015", "Days Sales Outstanding", KpiUnit.DAYS, KpiAggregation.RATIO, "Revenue-weighted average of monthly DSO"),
        KpiDefinition("KPI-016", "Budget Variance %", KpiUnit.PERCENT, KpiAggregation.RATIO, "(Net Revenue - Budget Net Revenue) / Budget Net Revenue"),
        KpiDefinition("KPI-017", "Net Revenue YoY Growth %", KpiUnit.PERCENT, KpiAggregation.RATIO, "(Net Revenue - Net Revenue SPLY) / Net Revenue SPLY"),
        KpiDefinition("KPI-018", "Budget Net Revenue", KpiUnit.CURRENCY, KpiAggregation.ADDITIVE, "Budget plan value"),
    )

    @classmethod
    def by_code(cls, code: str) -> KpiDefinition | None:
        return next((k for k in cls.all if k.code == code), None)


@dataclass(frozen=True)
class FinanceComponents:
    gross_revenue: Decimal = Decimal(0)
    revenue_deductions: Decimal = Decimal(0)
    cogs: Decimal = Decimal(0)
    operating_expenses: Decimal = Decimal(0)
    depreciation_amortisation: Decimal = Decimal(0)
    budget_net_revenue: Decimal = Decimal(0)
    closing_headcount: int = 0
    net_revenue_prior_year: Decimal | None = None
    dso_weighted: Decimal = Decimal(0)
    has_rows: bool = False

    @property
    def net_revenue(self) -> Decimal:
        return self.gross_revenue - self.revenue_deductions

    @property
    def gross_profit(self) -> Decimal:
        return self.net_revenue - self.cogs

    @property
    def ebitda(self) -> Decimal:
        return self.gross_profit - self.operating_expenses

    @property
    def operating_income(self) -> Decimal:
        return self.ebitda - self.depreciation_amortisation


def rounded(value: Decimal, places: int) -> Decimal:
    return value.quantize(Decimal(1).scaleb(-places), rounding=ROUND_HALF_EVEN)


def divide(numerator: Decimal, denominator: Decimal | int, *, percent: bool = False) -> Decimal | None:
    if denominator == 0:
        return None
    return rounded(numerator / denominator * (100 if percent else 1), 2)


class KpiCalculator:
    @staticmethod
    def compute(kpi: KpiDefinition, c: FinanceComponents) -> Decimal | None:
        if not c.has_rows:
            return None
        with localcontext() as context:
            context.prec = 29
            match kpi.code:
                case "KPI-001": return c.gross_revenue
                case "KPI-002": return c.revenue_deductions
                case "KPI-003": return c.net_revenue
                case "KPI-004": return c.cogs
                case "KPI-005": return c.gross_profit
                case "KPI-006": return divide(c.gross_profit, c.net_revenue, percent=True)
                case "KPI-007": return c.operating_expenses
                case "KPI-008": return c.ebitda
                case "KPI-009": return divide(c.ebitda, c.net_revenue, percent=True)
                case "KPI-010": return c.depreciation_amortisation
                case "KPI-011": return c.operating_income
                case "KPI-012": return divide(c.operating_income, c.net_revenue, percent=True)
                case "KPI-013": return Decimal(c.closing_headcount)
                case "KPI-014": return divide(c.net_revenue, c.closing_headcount)
                case "KPI-015": return divide(c.dso_weighted, c.net_revenue)
                case "KPI-016": return divide(c.net_revenue - c.budget_net_revenue, c.budget_net_revenue, percent=True)
                case "KPI-017":
                    return None if c.net_revenue_prior_year is None else divide(
                        c.net_revenue - c.net_revenue_prior_year, c.net_revenue_prior_year, percent=True)
                case "KPI-018": return c.budget_net_revenue
                case _: return None
