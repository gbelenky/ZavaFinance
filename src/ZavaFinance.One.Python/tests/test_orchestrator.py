import asyncio
import copy
import json
import unittest
from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

from zavafinance.config import Settings
from zavafinance.contracts import ClarificationPrompt, ClarificationSubmission, SessionState
from zavafinance.finance.clarification import ClarificationSelection
from zavafinance.integrations import DATA_AGENT, KNOWLEDGE_BASE, DownstreamHTTPError
from zavafinance.model import RouteDecision, ToolSelectionError, WITHHELD_RESULT
from zavafinance.orchestrator import Orchestrator


class MemoryStore:
    def __init__(self):
        self.states = {}
        self.saves = []

    async def load(self, key):
        return copy.deepcopy(self.states.get(key, SessionState()))

    async def save(self, key, state):
        self.saves.append(key)
        self.states[key] = copy.deepcopy(state)


class OrchestratorTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.settings = Settings(copilot_environment_id="test-env", copilot_schema_name="test-agent",
                                 fabric_workspace_id="test-workspace", fabric_data_agent_id="test-data-agent")
        self.model = SimpleNamespace(route=AsyncMock(return_value=RouteDecision(text="Help")))
        self.store = MemoryStore()
        self.query_factory = Mock(return_value=None)
        self.copilot = SimpleNamespace(start_conversation=AsyncMock(return_value="conversation"),
                                       ask_question=AsyncMock(return_value="Private definition"))
        self.copilot_factory = Mock(return_value=self.copilot)
        self.fabric = SimpleNamespace(query=AsyncMock(return_value="Private analysis"))
        self.fabric_factory = Mock(return_value=self.fabric)
        self.tokens = object()
        self.app = Orchestrator(self.settings, self.model, self.store, query_factory=self.query_factory,
                                copilot_factory=self.copilot_factory, data_agent_factory=self.fabric_factory)

    def route(self, name, args):
        self.model.route.return_value = RouteDecision(name, args, "call-1", "Ignore incidental prose.")

    async def run_question(self, text="question", key="s", submission=None):
        return await self.app.run_reply(key, text, self.tokens, submission)

    async def test_kpi_verbatim_footer_and_content_free_history(self):
        self.route("get_kpi_info", {"kpi": " Margin "})
        reply = await self.run_question("What is margin?")
        self.assertEqual(reply.text, f"Private definition\n\n_Source: {KNOWLEDGE_BASE}._")
        self.copilot.start_conversation.assert_awaited_once()
        self.copilot.ask_question.assert_awaited_once_with(
            "What is Margin? Please explain how it is defined and calculated.", "conversation")
        self.model.route.assert_awaited_once()
        self.query_factory.assert_not_called()
        self.fabric_factory.assert_not_called()
        state = self.store.states["s"]
        self.assertEqual(state.last_kpi_name, " Margin ")
        self.assertEqual(state.copilot_conversation_id, "conversation")
        self.assertEqual(len(state.history), 3)
        self.assertEqual(state.history[-1]["content"], WITHHELD_RESULT)
        self.assertNotIn("Private", json.dumps(state.history))
        self.assertNotIn("Ignore", json.dumps(state.history))

    async def test_latest_kpi_and_conversation_reused(self):
        self.route("get_kpi_info", {"kpi": "Margin"})
        await self.run_question("What is Margin?")
        self.route("get_kpi_info", {"kpi": "Cash"})
        await self.run_question("and Cash?")
        self.assertEqual(self.store.states["s"].last_kpi_name, "Cash")
        self.copilot.start_conversation.assert_awaited_once()
        history = self.model.route.call_args.args[0]
        self.assertEqual(history[0]["content"], "What is Margin?")
        self.assertIn(WITHHELD_RESULT, json.dumps(history))
        self.assertNotIn("Private", json.dumps(history))

    async def test_acquired_copilot_id_survives_ask_failure_and_keeps_kpi(self):
        self.store.states["s"] = SessionState(last_kpi_name="Prior")
        self.route("get_kpi_info", {"kpi": "Margin"})
        self.copilot.ask_question.side_effect = DownstreamHTTPError(403)
        reply = await self.run_question()
        self.assertEqual(reply.text, "I could not reach KPIpedia to look up 'Margin'.")
        self.assertEqual(self.store.states["s"].copilot_conversation_id, "conversation")
        self.assertEqual(self.store.states["s"].last_kpi_name, "Prior")

    async def test_kpi_empty_blank_timeout(self):
        self.route("get_kpi_info", {"kpi": " "})
        self.assertEqual((await self.run_question()).text, "I need a KPI name to look up.")
        self.copilot_factory.assert_not_called()
        self.route("get_kpi_info", {"kpi": "Margin"})
        self.copilot.ask_question.return_value = " "
        self.assertEqual((await self.run_question()).text, "KPIpedia returned no description for 'Margin'.")
        self.copilot.ask_question.side_effect = TimeoutError()
        self.assertIn("did not respond in time", (await self.run_question()).text)

    async def test_analysis_verbatim_no_synthesis_or_kpi_mutation(self):
        self.store.states["s"] = SessionState(last_kpi_name="Prior")
        self.route("explore_finance", {"question": "Why did margin fall?"})
        reply = await self.run_question()
        self.assertEqual(reply.text, f"Private analysis\n\n_Source: {DATA_AGENT}._")
        self.fabric.query.assert_awaited_once_with("Why did margin fall?")
        self.model.route.assert_awaited_once()
        self.query_factory.assert_not_called()
        self.copilot_factory.assert_not_called()
        self.assertEqual(self.store.states["s"].last_kpi_name, "Prior")
        self.assertNotIn("Private", json.dumps(self.store.states["s"].history))

    async def test_analysis_config_empty_blank_timeout_busy_and_error(self):
        self.route("explore_finance", {"question": ""})
        self.assertEqual((await self.run_question()).text, "What would you like me to analyse?")
        self.fabric_factory.assert_not_called()
        self.route("explore_finance", {"question": "Why?"})
        self.app.settings = replace(self.settings, fabric_workspace_id="")
        self.assertIn("not configured", (await self.run_question()).text)
        self.fabric_factory.assert_not_called()
        self.app.settings = self.settings
        self.fabric.query.return_value = ""
        self.assertIn("did not return an answer", (await self.run_question()).text)
        for error, fragment in [(TimeoutError(), "did not respond in time"), (DownstreamHTTPError(429), "compute limit"),
                                (DownstreamHTTPError(403), "could not reach")]:
            self.fabric.query.side_effect = error
            self.assertIn(fragment, (await self.run_question()).text)

    async def test_invalid_tool_has_no_factory_or_invalid_history(self):
        for decision in [RouteDecision("invented", {}, "c"), RouteDecision("get_statement", {"sql": "unsafe"}, "c")]:
            self.model.route.return_value = decision
            self.assertIn("could not select a valid", (await self.run_question()).text)
        self.query_factory.assert_not_called()
        self.copilot_factory.assert_not_called()
        self.fabric_factory.assert_not_called()
        self.assertEqual([h["role"] for h in self.store.states["s"].history], ["user", "user"])
        self.model.route.side_effect = ToolSelectionError("multiple calls")
        self.assertIn("could not select a valid", (await self.run_question()).text)

    async def test_no_tool_text_does_not_create_clients(self):
        reply = await self.run_question("Hello")
        self.assertEqual(reply.text, "Help")
        self.assertEqual(self.store.states["s"].history[-1], {"role": "assistant", "content": "Help"})
        self.query_factory.assert_not_called()
        self.copilot_factory.assert_not_called()
        self.fabric_factory.assert_not_called()

    async def test_history_cap_keeps_complete_turns(self):
        self.app.settings = replace(self.settings, max_history_messages=5)
        self.route("get_kpi_info", {"kpi": "Margin"})
        for index in range(8):
            await self.run_question(f"Question {index}")
            history = self.store.states["s"].history
            self.assertLessEqual(len(history), 5)
            self.assertEqual([h["role"] for h in history], ["user", "assistant", "tool"])
        for call in self.model.route.call_args_list:
            self.assertLessEqual(len(call.args[0]), 4)
        self.assertEqual(self.app._turn_locks, {})

    async def test_statement_receives_sticky_state_raw_arguments_and_utc_date(self):
        self.store.states["s"] = SessionState(last_kpi_name="Margin")
        self.route("get_statement", {"org": "West office", "dateRange": "Christmas Q3 2026"})
        tool = SimpleNamespace(execute=AsyncMock(return_value="Private statement"))
        with patch("zavafinance.orchestrator.StatementTool", return_value=tool) as constructor:
            reply = await self.run_question("show it")
        self.assertEqual(reply.text, "Private statement")
        self.assertEqual(constructor.call_args.args[1].last_kpi_name, "Margin")
        self.assertEqual(constructor.call_args.kwargs["session_key"], "s")
        tool.execute.assert_awaited_once_with(kpi=None, org="West office", date_range="Christmas Q3 2026")
        self.assertNotIn("Private", json.dumps(self.store.states["s"].history))

    async def test_reset_variants_clear_all_state_and_do_not_route(self):
        for text in ["reset", " /reset ", "START OVER", "cancel", "never mind"]:
            self.store.states["s"] = SessionState(last_kpi_name="Margin", copilot_conversation_id="old",
                                                   pending_clarification={"request_id": "old"},
                                                   history=[{"role": "user", "content": "Old"}])
            self.assertIn("conversation has been reset", (await self.run_question(text)).text)
            self.assertEqual(self.store.states["s"], SessionState())
        self.model.route.assert_not_called()
        self.query_factory.assert_not_called()

    async def test_no_pending_ordinal_bypasses_model(self):
        for text in ["first", "the 2nd one!", "99.", "option 0"]:
            self.assertIn("no valid pending choice", (await self.run_question(text)).text)
        self.model.route.assert_not_called()
        self.assertEqual(self.store.states["s"].history, [])

    async def test_pending_selection_delegates_binding_checks_and_never_adds_history(self):
        pending = {"request_id": "r", "catalog_version": "v", "candidate_ids": ["a", "b"],
                   "candidate_label_hashes": [ClarificationSelection.hash_label("West"),
                                              ClarificationSelection.hash_label("East")]}
        self.store.states["s"] = SessionState(pending_clarification=pending)
        prompt = ClarificationPrompt("r2", "org", "follow-up", [], "v")

        async def continuation(submission):
            self.assertEqual(submission, ClarificationSubmission("r", "b", "v"))
            tool.state.reply_clarification = prompt
            return "permissioned next question"

        tool = SimpleNamespace(continue_=AsyncMock(side_effect=continuation))

        def create(query, state, today, **kwargs):
            tool.state = state
            return tool

        with patch("zavafinance.orchestrator.StatementTool", side_effect=create):
            reply = await self.run_question("East")
        self.assertEqual(reply.clarification, prompt)
        self.assertEqual(self.store.states["s"].history, [])
        self.model.route.assert_not_called()

    async def test_duplicate_label_is_recognized_but_not_selected(self):
        self.store.states["s"] = SessionState(pending_clarification={
            "request_id": "r", "catalog_version": "v", "candidate_ids": ["a", "b"],
            "candidate_label_hashes": [ClarificationSelection.hash_label("West")] * 2})
        self.assertIn("does not uniquely identify", (await self.run_question("West")).text)
        self.model.route.assert_not_called()
        self.query_factory.assert_not_called()

    async def test_new_question_invalidates_pending_before_routing(self):
        self.store.states["s"] = SessionState(pending_clarification={"request_id": "r"})
        await self.run_question("Explain revenue")
        self.assertIsNone(self.store.states["s"].pending_clarification)
        self.model.route.assert_awaited_once()

    async def test_card_submission_takes_precedence_over_reset(self):
        self.store.states["s"] = SessionState(last_kpi_name="Margin")
        reply = await self.run_question("reset", submission=ClarificationSubmission("r", "a", "v"))
        self.assertIn("no valid pending choice", reply.text)
        self.assertEqual(self.store.states["s"].last_kpi_name, "Margin")

    async def test_save_failure_preserves_completed_answer(self):
        self.store.save = AsyncMock(side_effect=RuntimeError("sensitive details"))
        with self.assertLogs("zavafinance.orchestrator", level="ERROR") as logs:
            self.assertEqual((await self.run_question()).text, "Help")
        self.assertNotIn("sensitive details", str(logs.output))

    async def test_load_failure_no_reset_and_no_model(self):
        self.store.load = AsyncMock(side_effect=RuntimeError("unavailable"))
        with self.assertRaises(RuntimeError):
            await self.run_question("reset")
        self.model.route.assert_not_called()
        self.assertEqual(self.store.saves, [])
        self.assertEqual(self.app._turn_locks, {})

    async def test_same_session_serializes_and_different_callers_do_not_share_state(self):
        entered, release = asyncio.Event(), asyncio.Event()
        async def route(history, question):
            if question == "first question":
                entered.set()
                await release.wait()
            return RouteDecision(text=question)
        self.model.route.side_effect = route
        first = asyncio.create_task(self.run_question("first question"))
        await entered.wait()
        second = asyncio.create_task(self.run_question("second question"))
        await asyncio.sleep(0)
        other = await self.run_question("independent", key="other")
        self.assertEqual(other.text, "independent")
        self.assertEqual(self.app._turn_locks["s"].users, 2)
        self.assertFalse(second.done())
        release.set()
        await asyncio.gather(first, second)
        self.assertEqual(self.store.states["s"].history[0]["content"], "first question")
        self.assertEqual(self.store.states["other"].history[0]["content"], "independent")
        self.assertEqual(self.app._turn_locks, {})

    async def test_cancelled_waiter_does_not_remove_active_gate(self):
        entered, release = asyncio.Event(), asyncio.Event()
        async def route(*args):
            entered.set()
            await release.wait()
            return RouteDecision(text="done")
        self.model.route.side_effect = route
        active = asyncio.create_task(self.run_question())
        await entered.wait()
        waiter = asyncio.create_task(self.run_question())
        await asyncio.sleep(0)
        waiter.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await waiter
        self.assertEqual(self.app._turn_locks["s"].users, 1)
        release.set()
        await active
        self.assertEqual(self.app._turn_locks, {})

    async def test_cancelled_tool_preserves_marker_and_conversation_then_releases_gate(self):
        entered = asyncio.Event()
        async def ask(*args):
            entered.set()
            await asyncio.Event().wait()
        self.route("get_kpi_info", {"kpi": "Margin"})
        self.copilot.ask_question.side_effect = ask
        active = asyncio.create_task(self.run_question())
        await entered.wait()
        active.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await active
        self.assertEqual(self.store.states["s"].history[-1]["content"], WITHHELD_RESULT)
        self.assertEqual(self.store.states["s"].copilot_conversation_id, "conversation")
        self.assertEqual(self.app._turn_locks, {})
