import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "resolver_catalog", Path(__file__).parents[1] / "scripts" / "fabric" / "resolver_catalog.py")
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


class ResolverCatalogTests(unittest.TestCase):
    def setUp(self):
        self.regions = [{"region_code": r, "region_name": r} for r in ("EMEA", "APAC")]
        self.departments = [
            {"department_code": "D100", "department_name": "Retail Operations", "department_group": "Commercial"},
            {"department_code": "D420", "department_name": "Information Technology", "department_group": "Corporate"}]
        self.kpis = [{"kpi_code": code, "kpi_name": values[1][0], "unit": "USD", "aggregation": "Additive"}
                     for code, values in catalog.KPI_METADATA.items()]
        self.pairs = {(r["region_code"], d["department_code"]) for r in self.regions for d in self.departments}
        self.entities, self.scopes = catalog.build_catalog(
            self.regions, self.departments, self.kpis, self.pairs, "test-v1")

    def test_deterministic_and_order_independent(self):
        entities, scopes = catalog.build_catalog(
            self.regions[::-1], self.departments[::-1], self.kpis[::-1], self.pairs, "test-v1")
        self.assertEqual((entities, scopes), (self.entities, self.scopes))

    def test_branch_scope_is_distinct_fact_pairs(self):
        scope = lambda node: {(s["region_code"], s["department_code"]) for s in self.scopes if s["node_id"] == node}
        self.assertEqual(self.pairs, scope("org-company"))
        self.assertEqual({("EMEA", "D100"), ("EMEA", "D420")}, scope("org-region-emea"))
        self.assertEqual({("EMEA", "D420"), ("APAC", "D420")}, scope("org-global-group-corporate"))
        self.assertEqual({("EMEA", "D100")}, scope("org-region-emea-department-d100"))

    def test_margin_ambiguity_is_preserved(self):
        matches = [e["kpi_code"] for e in self.entities if "margin" in json.loads(e["aliases_json"])]
        self.assertEqual(["KPI-006", "KPI-009", "KPI-012"], matches)

    def test_repeated_department_labels_have_distinct_paths(self):
        matches = [e for e in self.entities if "it" in json.loads(e["aliases_json"])]
        self.assertEqual(3, len(matches))
        self.assertEqual(3, len({e["hierarchy_path"] for e in matches}))

    def test_families_cannot_be_reported(self):
        self.assertTrue(all(not e["is_reportable"] and not e["kpi_code"]
                            for e in self.entities if e["entity_kind"] == "kpi_group"))

    def test_unknown_measure_requires_review(self):
        with self.assertRaisesRegex(ValueError, "KPI mappings"):
            catalog.build_catalog(self.regions, self.departments, self.kpis[:-1], self.pairs, "test-v1")

    def test_duplicate_scope_rejected(self):
        with self.assertRaisesRegex(ValueError, "Duplicate scope"):
            catalog.validate_catalog(self.entities, self.scopes + [self.scopes[0]], self.pairs)

    def test_cycle_rejected(self):
        self.entities[0]["parent_id"] = self.entities[0]["entity_id"]
        with self.assertRaisesRegex(ValueError, "cyclic"):
            catalog.validate_catalog(self.entities, self.scopes, self.pairs)


if __name__ == "__main__":
    unittest.main()
