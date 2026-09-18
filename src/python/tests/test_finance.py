import unittest
from dataclasses import replace
from datetime import date
from decimal import Decimal as D

from zavafinance.finance import (
    FinanceComponents, FinancePeriodParser, KpiCalculator, KpiCatalog,
    OrganizationScope, StatementResult, StatementTool, SourceFooter,
)


class CalculatorTests(unittest.TestCase):
    def setUp(self):
        self.components = FinanceComponents(
            gross_revenue=D(1000), revenue_deductions=D(100), cogs=D(400),
            operating_expenses=D(200), depreciation_amortisation=D(50),
            closing_headcount=10, budget_net_revenue=D(800),
            net_revenue_prior_year=D(750), dso_weighted=D(40500), has_rows=True,
        )

    def test_all_eighteen_formulas(self):
        expected = ("1000", "100", "900", "400", "500", "55.56", "200",
                    "300", "33.33", "50", "250", "27.78", "10", "90",
                    "45", "12.50", "20", "800")
        self.assertEqual(18, len(KpiCatalog.all))
        for kpi, value in zip(KpiCatalog.all, expected):
            with self.subTest(kpi=kpi.code):
                result = KpiCalculator.compute(kpi, self.components)
                self.assertIsInstance(result, D)
                self.assertEqual(D(value), result)

    def test_missing_rows_never_fabricate_zero(self):
        for kpi in KpiCatalog.all:
            self.assertIsNone(KpiCalculator.compute(kpi, FinanceComponents()))

    def test_zero_denominators_and_missing_prior_year(self):
        for code, changes in (
            ("KPI-006", {"gross_revenue": D(100)}),
            ("KPI-009", {"gross_revenue": D(100)}),
            ("KPI-012", {"gross_revenue": D(100)}),
            ("KPI-015", {"gross_revenue": D(100)}),
            ("KPI-014", {"closing_headcount": 0}),
            ("KPI-016", {"budget_net_revenue": D(0)}),
            ("KPI-017", {"net_revenue_prior_year": None}),
            ("KPI-017", {"net_revenue_prior_year": D(0)}),
        ):
            with self.subTest(code=code):
                self.assertIsNone(KpiCalculator.compute(
                    KpiCatalog.by_code(code), replace(self.components, **changes)))

    def test_ratios_recompute_instead_of_average(self):
        combined = replace(self.components, gross_revenue=D(1100),
                           revenue_deductions=D(0), cogs=D(720))
        self.assertEqual(D("34.55"), KpiCalculator.compute(KpiCatalog.by_code("KPI-006"), combined))

    def test_bankers_rounding_and_decimal_precision(self):
        components = replace(self.components, gross_revenue=D("1.005"),
                             revenue_deductions=D(0), closing_headcount=1)
        self.assertEqual(D("1.00"), KpiCalculator.compute(KpiCatalog.by_code("KPI-014"), components))
        components = replace(components, gross_revenue=D("1.015"))
        self.assertEqual(D("1.02"), KpiCalculator.compute(KpiCatalog.by_code("KPI-014"), components))

    def test_deployed_fabric_reference_values(self):
        fixtures = (
            (("93701276.73", "8061714.36", "49228405.15", "58917933.94", "5071299.87", "81837576.77", 3258),
             {"KPI-003": "85639562.37", "KPI-005": "36411157.22", "KPI-008": "-22506776.72",
              "KPI-011": "-27578076.59", "KPI-006": "42.52", "KPI-012": "-32.20", "KPI-014": "26285.93", "KPI-016": "4.65"}),
            (("271042492.51", "23404710.85", "141976212.38", "176468910.16", "15213992.99", "238146669.84", 3247),
             {"KPI-003": "247637781.66", "KPI-006": "42.67", "KPI-012": "-34.74", "KPI-014": "76266.64"}),
            (("3867705621.71", "328817052.89", "2036324109.80", "2378696906.37", "203889124.44", "3486153099.26", 11144),
             {"KPI-003": "3538888568.82", "KPI-006": "42.46", "KPI-012": "-30.52", "KPI-014": "317559.99"}),
        )
        for values, expected in fixtures:
            components = FinanceComponents(*(D(value) for value in values[:6]), closing_headcount=values[6], has_rows=True)
            for code, value in expected.items():
                with self.subTest(reference=values[0], kpi=code):
                    self.assertEqual(D(value), KpiCalculator.compute(KpiCatalog.by_code(code), components))


class PeriodTests(unittest.TestCase):
    today = date(2026, 9, 10)

    def test_valid_periods(self):
        cases = [
            ("Q3 2026", 2026, 7, 3, "Q3 2026"),
            ("q1 2025", 2025, 1, 3, "Q1 2025"),
            ("2026 Q4", 2026, 10, 3, "Q4 2026"),
            ("Q2 FY2026", 2026, 4, 3, "Q2 2026"),
            ("Q2 of FY2026", 2026, 4, 3, "Q2 2026"),
            ("November 2025", 2025, 11, 1, "November 2025"),
            ("nov 2025", 2025, 11, 1, "November 2025"),
            ("septemb 2026", 2026, 9, 1, "September 2026"),
            ("January to March 2026", 2026, 1, 3, "January to March 2026"),
            ("November 2025 to February 2026", 2025, 11, 4, "November to February 2026"),
            ("Jan–Mar 2026", 2026, 1, 3, "Jan to Mar 2026"),
            ("FY2026", 2026, 1, 12, "2026"),
            ("H1 2026", 2026, 1, 6, "H1 2026"),
            ("H2 FY 2026", 2026, 7, 6, "H2 2026"),
            ("last quarter", 2026, 4, 3, "Q2 2026"),
            ("current quarter", 2026, 7, 3, "Q3 2026"),
            ("last month", 2026, 8, 1, "August 2026"),
            ("this month", 2026, 9, 1, "September 2026"),
            ("previous year", 2025, 1, 12, "2025"),
            ("this year", 2026, 1, 9, "YTD 2026"),
            ("year to date", 2026, 1, 9, "YTD 2026"),
        ]
        for text, year, month, count, label in cases:
            with self.subTest(text=text):
                result = FinancePeriodParser.parse(text, self.today)
                self.assertIsNotNone(result)
                self.assertEqual(date(year, month, 1), result.start)
                self.assertEqual(count, result.month_count)
                self.assertEqual(label, result.label)

    def test_unrecognized_expression_is_never_broadened(self):
        for text in (None, "", " ", "soon", "Q5 2026", "H3 2026",
                     "January to Smarch 2026", "March to January 2026",
                     "Q3 2026 excluding August", "Q1 2026 and Q3 2026",
                     "November 2025 to garbage 2026", "2026 next summer",
                     "0000", "9999", "Q1 0000", "2026-01-01", "Nov 1 2026",
                     "2026\nexcluding August", "June 2026 trailing"):
            with self.subTest(text=text):
                self.assertIsNone(FinancePeriodParser.parse(text, self.today))

    def test_period_comparisons(self):
        period = FinancePeriodParser.parse("Q3 2026", self.today)
        self.assertEqual(date(2026, 9, 1), period.closing_month)
        self.assertEqual(date(2025, 7, 1), period.prior_year().start)
        self.assertEqual(date(2026, 4, 1), period.prior_period().start)
        self.assertEqual(3, period.prior_period().month_count)


class RenderingTests(unittest.TestCase):
    def result(self, code="KPI-003", value=D(1200000), prior=D(1000000)):
        return StatementResult(KpiCatalog.by_code(code), OrganizationScope("org", "e", "EMEA"),
                               FinancePeriodParser.parse("Q3 2026", date(2026, 9, 10)),
                               value, prior, "USD")

    def test_exact_currency_footer(self):
        self.assertEqual(
            "**Net Revenue — EMEA — Q3 2026**\n\n"
            "- Q3 2026: 1.2 M USD\n- Prior period: 1.0 M USD\n"
            "- Change: 20.0% up versus prior period\n\n"
            "_Source: Zava finance lakehouse. Net Revenue = Gross Revenue - Revenue Deductions._",
            StatementTool.render(self.result()),
        )

    def test_missing_data_has_no_success_footer(self):
        text = StatementTool.render(self.result(value=None))
        self.assertEqual("**Net Revenue — EMEA — Q3 2026**\n\nNo data is available for that combination.", text)

    def test_units_negative_prior_and_percentage_points(self):
        for result, expected in (
            (self.result("KPI-006", D("12.5"), D("10.25")), "2.25 pp up"),
            (self.result("KPI-013", D(42), None), "42 FTE"),
            (self.result("KPI-015", D("42.12"), None), "42.1 days"),
            (self.result(value=D(-50), prior=D(-100)), "50.0% up"),
        ):
            self.assertIn(expected, StatementTool.render(result))
        self.assertNotIn("Change:", StatementTool.render(self.result(prior=D(0))))

    def test_footer_is_additive_idempotent(self):
        self.assertEqual("", SourceFooter.append("", "x"))
        answer = SourceFooter.append("a  ", "b")
        self.assertEqual("a\n\n_Source: b._", answer)
        self.assertEqual(answer, SourceFooter.append(answer, "c"))
