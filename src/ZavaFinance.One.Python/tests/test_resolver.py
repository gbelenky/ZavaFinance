import asyncio
import json
import unittest
from dataclasses import replace
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal

from zavafinance.config import Settings
from zavafinance.contracts import ClarificationSubmission, SessionState
from zavafinance.wire import validate_prompt
from zavafinance.finance import (
    CatalogChangedException, ClarificationSelection, ResolutionStatus, ResolverCandidateId,
    ResolverCatalog, ResolverEntity, ResolverRelease, StatementResolver, StatementResult,
    StatementTool,
)

RELEASE = ResolverRelease("v1", "resolver-v1", "embedding-model", 1536)


def entity(id, kind, name, aliases=(), code=None, parent=None, region=None, reportable=True):
    return ResolverEntity("v1", id, kind, name, aliases, "Definition for " + name, parent,
                          (region + " / " if region else "") + name, code, region, None, None, reportable)


def standard():
    return ResolverCatalog(RELEASE, (
        entity("KPI-003", "kpi", "Net Revenue", ("net sales",), "KPI-003"),
        entity("KPI-011", "kpi", "Operating Income (EBIT)", ("EBIT",), "KPI-011"),
        entity("region-emea", "org", "EMEA"),
        entity("region-apac", "org", "APAC"),
        entity("dept-east", "org", "Marketing", ("market operations",), region="EMEA"),
        entity("dept-west", "org", "Marketing", ("market operations",), region="APAC"),
        entity("company", "org", "All company", ("company",)),
    ))


class Query:
    def __init__(self):
        self.snapshot = standard()
        self.loads = 0
        self.calls = []
        self.failure = None
        self.release_override = None

    async def load_catalog(self):
        self.loads += 1
        if self.failure:
            raise self.failure
        return self.snapshot

    async def get_release(self):
        return self.release_override or self.snapshot.release

    async def get_statement(self, kpi, organization, period):
        self.calls.append((kpi, organization, period))
        return StatementResult(kpi, organization, period, Decimal(42), Decimal(40), "USD")


class Search:
    def __init__(self, hits=(), chosen=None):
        self.hits = [ResolverCandidateId(*hit) for hit in hits]
        self.chosen = chosen
        self.searches = []
        self.choices = []

    async def search(self, raw_term, field, release):
        self.searches.append((raw_term, field, release))
        return self.hits

    async def choose(self, raw_term, field, candidates):
        self.choices.append((raw_term, field, candidates))
        return self.chosen


class ResolutionTests(unittest.IsolatedAsyncioTestCase):
    async def test_exact_ids_normalized_names_and_aliases_without_search(self):
        for term in ("KPI-003", " NET SALES ", "Nét   Revenue", "kpi-003"):
            with self.subTest(term=term):
                query, search = Query(), Search()
                result = await StatementResolver(query, search).resolve(query.snapshot, term, "kpi")
                self.assertEqual(ResolutionStatus.RESOLVED, result.status)
                self.assertEqual("KPI-003", result.entity.id)
                self.assertFalse(search.searches)
                self.assertFalse(search.choices)

    async def test_exact_alias_collision_retains_all_scopes(self):
        query, search = Query(), Search()
        result = await StatementResolver(query, search).resolve(query.snapshot, "market operations", "org")
        self.assertEqual(ResolutionStatus.CLARIFY, result.status)
        self.assertEqual(["dept-west", "dept-east"], [entity.id for entity in result.candidates])
        self.assertFalse(search.searches)

    async def test_partial_and_unknown_terms_never_use_containment(self):
        query = Query()
        for term in ("income", "revenues", "EMEA except Marketing", " ", "!!!"):
            result = await StatementResolver(query).resolve(query.snapshot, term, "kpi")
            self.assertEqual(ResolutionStatus.NO_MATCH, result.status)

    async def test_one_semantic_suggestion_always_confirms(self):
        query, search = Query(), Search([("KPI-003", "v1")])
        result = await StatementResolver(query, search).resolve(query.snapshot, "net reveneu", "kpi")
        self.assertEqual(ResolutionStatus.CLARIFY, result.status)
        self.assertIsNone(result.entity)
        self.assertEqual(1, query.loads)
        self.assertFalse(search.choices)

    async def test_search_name_collision_cannot_be_pruned(self):
        query, search = Query(), Search([("dept-east", "v1"), ("dept-west", "v1")], ["dept-east"])
        result = await StatementResolver(query, search).resolve(query.snapshot, "marketng", "org")
        self.assertEqual(2, len(result.candidates))
        self.assertFalse(search.choices)

    async def test_candidates_are_reauthorized_and_raw_term_preserved(self):
        query = Query()
        search = Search([("unauthorized", "v1"), ("region-emea", "old"),
                         ("KPI-003", "v1"), ("KPI-011", "v1")], ["KPI-011"])
        result = await StatementResolver(query, search).resolve(query.snapshot, " operating ernings ", "kpi")
        self.assertEqual((" operating ernings ", "kpi", RELEASE), search.searches[0])
        self.assertEqual({"KPI-003", "KPI-011"}, {entity.id for entity in search.choices[0][2]})
        self.assertEqual(ResolutionStatus.CLARIFY, result.status)
        self.assertEqual(["KPI-011"], [entity.id for entity in result.candidates])

    async def test_revoked_wrong_kind_and_wrong_version_hits_are_not_exposed(self):
        query = Query()
        search = Search([("revoked", "v1"), ("company", "v1"), ("KPI-003", "v0")])
        result = await StatementResolver(query, search).resolve(query.snapshot, "private", "kpi")
        self.assertEqual(ResolutionStatus.NO_MATCH, result.status)
        self.assertFalse(search.choices)

    async def test_invalid_model_ids_never_choose_arbitrarily(self):
        for choices in (["company"], ["not-in-candidates"], ["dept-east", "dept-east"], [42]):
            query = Query()
            search = Search([("dept-east", "v1"), ("region-emea", "v1")], choices)
            result = await StatementResolver(query, search).resolve(query.snapshot, "marketng", "org")
            self.assertEqual(ResolutionStatus.CLARIFY, result.status)
            self.assertEqual(2, len(result.candidates))

    async def test_empty_model_choice_is_no_match(self):
        query = Query()
        result = await StatementResolver(query, Search([("region-emea", "v1"), ("region-apac", "v1")], [])).resolve(
            query.snapshot, "unknown", "org")
        self.assertEqual(ResolutionStatus.NO_MATCH, result.status)

    async def test_full_release_change_during_search_fails_closed(self):
        for changes in ({"catalog_version": "v2"}, {"search_index": "another-index"},
                        {"embedding_deployment": "other-model"}, {"embedding_dimensions": 3072}):
            query = Query()
            old = query.snapshot
            query.snapshot = replace(old, release=replace(RELEASE, **changes))
            search = Search([("KPI-003", "v1")])
            with self.assertRaises(CatalogChangedException):
                await StatementResolver(query, search).resolve(old, "revenuee", "kpi")


class ContinuationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.query, self.state = Query(), SessionState()
        self.now = datetime(2026, 9, 30, 23, 59, tzinfo=timezone.utc)

    def tool(self, owner="user", today=date(2026, 9, 30), search=None):
        return StatementTool(self.query, self.state, today, session_key=owner, options=Settings(),
                             clock=lambda: self.now, search=search)

    async def prompt(self):
        await self.tool().execute("  NET SALES  ", "Marketing", "last month")
        return self.state.pending_clarification

    @staticmethod
    def submission(pending):
        return ClarificationSubmission(pending["request_id"], pending["candidate_ids"][0], pending["catalog_version"])

    async def test_explicit_org_scopes_keep_versioned_node(self):
        for term, id in (("company", "company"), ("EMEA", "region-emea"),
                         ("EMEA / Marketing", "dept-east"), ("APAC / Marketing", "dept-west"), ("dept-east", "dept-east")):
            await self.tool().execute("Net Revenue", term, "2026")
            scope = self.query.calls[-1][1]
            self.assertEqual(id, scope.code)
            self.assertEqual(RELEASE, scope.release)
            self.assertEqual("v1", scope.catalog_version)

    async def test_pending_is_json_serializable_minimal_and_choice_executes_only_selected_node(self):
        pending = await self.prompt()
        persisted = json.loads(json.dumps(pending))
        self.assertEqual("  NET SALES  ", persisted["arguments"]["kpi"])
        self.assertEqual("Marketing", persisted["arguments"]["org"])
        self.assertEqual("last month", persisted["arguments"]["date_range"])
        self.assertNotIn("Definition for", json.dumps(persisted))
        self.assertNotIn("aliases", json.dumps(persisted))
        self.assertFalse(self.query.calls)
        self.state.pending_clarification = persisted
        recognized, selected = ClarificationSelection.try_read("second one", persisted)
        self.assertTrue(recognized)
        answer = await self.tool().continue_(selected)
        self.assertIn("_Source:", answer)
        self.assertEqual(selected.option_id, self.query.calls[-1][1].code)
        self.assertEqual("Net Revenue", self.state.last_kpi_name)
        self.assertIsNone(self.state.pending_clarification)
        self.assertIsNone(self.state.reply_clarification)

    async def test_relative_date_retains_original_clock_across_month(self):
        pending = await self.prompt()
        self.now += timedelta(minutes=2)
        await self.tool(today=date(2026, 10, 1)).continue_(self.submission(pending))
        self.assertEqual(date(2026, 8, 1), self.query.calls[0][2].start)

    async def test_invalid_stale_cross_user_or_expired_click_never_reads_catalog(self):
        for attack in ("request", "id", "version", "user", "expired"):
            with self.subTest(attack=attack):
                pending = await self.prompt()
                submission, tool = self.submission(pending), self.tool()
                if attack == "request": submission = replace(submission, request_id="forged")
                if attack == "id": submission = replace(submission, option_id="company")
                if attack == "version": submission = replace(submission, catalog_version="v2")
                if attack == "user": tool = self.tool(owner="another-user")
                if attack == "expired": self.now += timedelta(hours=1)
                loads = self.query.loads
                answer = await tool.continue_(submission)
                self.assertIn("ask the statement again", answer)
                self.assertEqual(loads, self.query.loads)
                self.assertFalse(self.query.calls)

    async def test_replayed_or_reset_choice_cannot_execute(self):
        pending = await self.prompt()
        selection = self.submission(pending)
        self.state.pending_clarification = None
        await self.tool().continue_(selection)
        self.assertFalse(self.query.calls)
        pending = await self.prompt()
        selection = self.submission(pending)
        await self.tool().continue_(selection)
        await self.tool().continue_(selection)
        self.assertEqual(1, len(self.query.calls))

    async def test_new_question_invalidates_previous_card_even_when_missing_fields(self):
        selection = self.submission(await self.prompt())
        await self.tool().execute("EBIT")
        await self.tool().continue_(selection)
        self.assertFalse(self.query.calls)

    async def test_release_binding_mutations_and_revocation_after_selection(self):
        for changes in ({"catalog_version": "v2"}, {"search_index": "another-index"},
                        {"embedding_deployment": "other-model"}, {"embedding_dimensions": 3072}, None):
            self.query.snapshot = standard()
            pending = await self.prompt()
            selection = self.submission(pending)
            self.query.snapshot = (
                replace(self.query.snapshot, release=replace(RELEASE, **changes)) if changes else
                replace(self.query.snapshot, entities=tuple(entity for entity in self.query.snapshot.entities if entity.id != selection.option_id)))
            await self.tool().continue_(selection)
            self.assertFalse(self.query.calls)
            self.assertIsNone(self.state.pending_clarification)

    async def test_release_change_immediately_before_query(self):
        self.query.release_override = replace(RELEASE, embedding_dimensions=3072)
        self.assertIn("catalogue has changed", await self.tool().execute("Net Revenue", "EMEA", "2026"))
        self.assertFalse(self.query.calls)

    async def test_legacy_or_corrupt_binding_is_rejected_without_catalog_load(self):
        for changes in ({"release": None}, {"release": {}}, {"expires_at_utc": "invalid"},
                        {"field": "other"}, {"arguments": {}},
                        {"release": {**RELEASE.__dict__, "catalog_version": "v2"}}):
            pending = await self.prompt()
            selection = self.submission(pending)
            pending.update(changes)
            loads = self.query.loads
            self.assertIn("binding has changed", await self.tool().continue_(selection))
            self.assertEqual(loads, self.query.loads)
            self.assertFalse(self.query.calls)

    async def test_kpi_group_offers_only_supported_reportable_descendants(self):
        self.query.snapshot = ResolverCatalog(RELEASE, (
            entity("group", "kpi_group", "Profitability", reportable=False),
            entity("subgroup", "kpi_group", "Operating", parent="group", reportable=False),
            entity("KPI-003", "kpi", "Net Revenue", code="KPI-003", parent="group"),
            entity("KPI-011", "kpi", "Operating Income", code="KPI-011", parent="subgroup"),
            entity("KPI-999", "kpi", "Unsupported", code="KPI-999", parent="group"),
            entity("unreportable", "kpi", "Hidden", code="KPI-003", parent="group", reportable=False),
            entity("EMEA", "org", "EMEA"),
        ))
        await self.tool().execute("Profitability", "EMEA", "2026")
        self.assertEqual(["KPI-003", "KPI-011"], self.state.pending_clarification["candidate_ids"])
        self.assertFalse(self.query.calls)
        pending = self.state.pending_clarification
        await self.tool().continue_(ClarificationSubmission(pending["request_id"], "KPI-011", "v1"))
        self.assertEqual("KPI-011", self.query.calls[0][0].code)

    async def test_unknown_unreportable_and_too_many_choices_never_execute(self):
        for entities, kpi, org, expected in (
            ((entity("x", "kpi", "Unsupported", code="KPI-999"),), "Unsupported", "EMEA", "supported statement"),
            ((entity("g", "kpi_group", "Empty", reportable=False),), "Empty", "EMEA", "no supported reportable"),
            ((*standard().entities, entity("x", "org", "Not reportable", reportable=False)), "Net Revenue", "Not reportable", "not a reportable"),
            (tuple(entity(str(i), "kpi", "Same", code="KPI-003") for i in range(26)), "Same", "EMEA", "too many possible"),
        ):
            self.query.snapshot = ResolverCatalog(RELEASE, entities)
            self.assertIn(expected, await self.tool().execute(kpi, org, "2026"))
            self.assertFalse(self.query.calls)

    async def test_catalog_names_not_static_names_are_rendered(self):
        self.query.snapshot = replace(self.query.snapshot, entities=(
            entity("renamed", "kpi", "Published net turnover", code="KPI-003"), *self.query.snapshot.entities[2:]))
        self.assertIn("**Published net turnover", await self.tool().execute("renamed", "EMEA", "2026"))

    async def test_invalid_prompt_metadata_never_enters_pending_state(self):
        for invalid in (replace(entity("valid", "org", "Marketing"), id="x" * 129),
                        entity("unsafe id", "org", "Marketing"), entity("valid", "org", "Marketing", region="bad\0region")):
            self.query.snapshot = replace(standard(), entities=(*standard().entities[:4], invalid, entity("other", "org", "Marketing")))
            with self.assertLogs("zavafinance.finance.statement", level="ERROR"):
                self.assertIn("could not retrieve", await self.tool().execute("Net Revenue", "Marketing", "2026"))
            self.assertIsNone(self.state.pending_clarification)
            self.assertIsNone(self.state.reply_clarification)
            self.assertFalse(self.query.calls)

    async def test_option_labels_follow_utf16_wire_limit_and_remain_selectable(self):
        name = "😀" * 99 + "xx"
        self.query.snapshot = replace(standard(), entities=(*standard().entities[:4],
            entity("first", "org", name, ("long-name",)), entity("second", "org", name, ("long-name",))))
        await self.tool().execute("Net Revenue", "long-name", "2026")
        validate_prompt(self.state.reply_clarification)
        label = self.state.reply_clarification.options[0].label
        self.assertEqual(200, len(label.encode("utf-16-le")) // 2)
        self.assertEqual((True, None), ClarificationSelection.try_read(label, self.state.pending_clarification))
        self.assertEqual("second", ClarificationSelection.try_read("second", self.state.pending_clarification)[1].option_id)

    async def test_missing_args_and_unconfigured_warehouse(self):
        tool = StatementTool(None, self.state, date(2026, 9, 30), session_key="user", options=Settings())
        self.assertEqual("Which KPI would you like a statement for?", await tool.execute())
        self.assertIn("Which organization", await tool.execute("Net Revenue"))
        self.assertIn("Which period", await tool.execute("Net Revenue", "EMEA"))
        self.assertIn("could not interpret", await tool.execute("Net Revenue", "EMEA", "Q3 2026 excluding August"))
        self.assertIn("not configured", await tool.execute("Net Revenue", "EMEA", "2026"))
        self.assertFalse(self.query.calls)

    async def test_errors_are_safe_and_cancellation_propagates(self):
        self.query.failure = ValueError("secret-token and private warehouse detail")
        with self.assertLogs("zavafinance.finance.statement", level="ERROR") as logs:
            result = await self.tool().execute("Net Revenue", "EMEA", "2026")
        self.assertNotIn("secret-token", result + str(logs.output))
        self.assertIn("could not retrieve", result)
        self.query.failure = TimeoutError()
        self.assertIn("did not respond in time", await self.tool().execute("Net Revenue", "EMEA", "2026"))
        self.query.failure = asyncio.CancelledError()
        with self.assertRaises(asyncio.CancelledError):
            await self.tool().execute("Net Revenue", "EMEA", "2026")


class TypedChoiceTests(unittest.TestCase):
    def pending(self, labels=("First unit", "Second unit")):
        return {"request_id": "r", "catalog_version": "v1", "candidate_ids": ["a", "b"],
                "candidate_label_hashes": [ClarificationSelection.hash_label(label) for label in labels]}

    def test_ordinals_numbers_exact_ids_and_normalized_labels(self):
        for text, id in (("second one", "b"), ("the second option", "b"), ("option 2", "b"),
                         ("2nd", "b"), ("1!", "a"), ("  FÍRST---unit  ", "a"), ("b", "b")):
            recognized, submission = ClarificationSelection.try_read(text, self.pending())
            self.assertTrue(recognized)
            self.assertEqual(id, submission.option_id)
        for text in ("Third unit", "First un", "show option 2", "1;DROP TABLE"):
            self.assertEqual((False, None), ClarificationSelection.try_read(text, self.pending()))
        self.assertEqual((True, None), ClarificationSelection.try_read("99", self.pending()))
        self.assertEqual((True, None), ClarificationSelection.try_read("1", None))

    def test_duplicate_normalized_and_truncated_labels_remain_ambiguous(self):
        for labels, text in ((("Márketing & Sales", "marketing and sales"), "MARKETING & SALES"),
                             (("a" * 199 + "…", "a" * 199 + "…"), "a" * 199 + "…")):
            pending = self.pending(labels)
            self.assertEqual((True, None), ClarificationSelection.try_read(text, pending))
            self.assertEqual("b", ClarificationSelection.try_read("second one", pending)[1].option_id)

    def test_reset_phrases_are_exact(self):
        for text in ("reset", "/reset", "start over", "cancel", "never mind", " RESET "):
            self.assertTrue(ClarificationSelection.is_reset(text))
        self.assertFalse(ClarificationSelection.is_reset("reset Europe sales"))

    def test_release_validation(self):
        for changes in ({"catalog_version": ""}, {"catalog_version": "v1'"},
                        {"search_index": "UPPERCASE"}, {"search_index": "a"},
                        {"embedding_deployment": "../x"}, {"embedding_dimensions": 0},
                        {"embedding_dimensions": 3073}, {"embedding_dimensions": True}):
            with self.assertRaises(ValueError):
                replace(RELEASE, **changes).validate()
