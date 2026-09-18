import asyncio
import json
import time
import unittest
from datetime import datetime, timezone
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

import httpx
from agent_framework import Agent, MiddlewareFailure
from azure.ai.projects.aio import AIProjectClient
from azure.core.credentials import AccessToken

from zavafinance.config import Settings
from zavafinance.integrations import KNOWLEDGE_BASE
from zavafinance.model import FoundryModel, RouteDecision, WITHHELD_RESULT
from zavafinance.agent import FinanceAgent
from tests.fakes import MemoryStore, ScriptedModel
from zavafinance.agent_policy import SingleModelTurn
from zavafinance.finance.statement import StatementTool


class FrameworkAgentTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.settings = Settings(copilot_environment_id="test-env", copilot_schema_name="test-agent",
                                 fabric_workspace_id="workspace", fabric_data_agent_id="data-agent")
        self.model = ScriptedModel(self.settings)
        self.addAsyncCleanup(self.model.aclose)
        self.store = MemoryStore()

    def app(self, **kwargs):
        return FinanceAgent(self.settings, self.model.client, self.store,
                            model_options=self.model.options(), **kwargs)

    async def test_date_rules_and_reference_reach_maf_and_match_statement_execution(self):
        self.model.route.return_value = RouteDecision(
            "get_statement", {"kpi": "net revenue", "org": "EMEA", "dateRange": "last quarter"}, "call")
        app = self.app(query_factory=lambda _: None)
        with patch("zavafinance.tools.datetime") as clock, \
                patch("zavafinance.tools.StatementTool", wraps=StatementTool) as statement:
            clock.now.side_effect = [
                datetime(2026, 12, 31, 23, 59, tzinfo=timezone.utc),
                datetime(2027, 1, 1, tzinfo=timezone.utc),
            ]
            reply = await app.run_reply("s", "Show net revenue for EMEA last quarter.", object())
        self.assertIn("warehouse is not configured", reply.text)
        self.assertEqual(len(self.model.requests), 1)
        request = self.model.requests[0]
        declaration = next(tool for tool in request["tools"] if tool["name"] == "get_statement")
        for rule in ("Explicit dates take precedence", "most recent relevant, unambiguous period",
                     "current calendar year", "Q3 2025", "Do not drop unknown date qualifiers",
                     "UTC reference date: 2026-12-31", "Current calendar year: 2026"):
            self.assertIn(rule, declaration["description"])
        self.assertIn("date-resolution rules", declaration["parameters"]["properties"]["dateRange"]["description"])
        self.assertIn("date-resolution rules", request["instructions"])
        self.assertEqual(statement.call_args.args[2], datetime(2026, 12, 31).date())
        clock.now.assert_called_once_with(timezone.utc)

    async def test_maf_owns_execution_and_never_receives_the_financial_result(self):
        requests, runs, responses = [], [], []

        def handle(request):
            requests.append(json.loads(request.content))
            return httpx.Response(200, json={
                "id": f"resp-{len(requests)}", "object": "response", "created_at": 1,
                "status": "completed", "model": "gpt-5.4-mini",
                "output": [
                    {"type": "message", "id": "prose", "status": "completed", "role": "assistant",
                     "content": [{"type": "output_text", "text": "SPECULATIVE PROSE", "annotations": []}]},
                    {"type": "function_call", "id": "fc", "call_id": f"call-{len(requests)}",
                     "name": "get_kpi_info", "arguments": '{"kpi":"Margin"}', "status": "completed"}],
            })

        credential = SimpleNamespace(get_token=AsyncMock(
            return_value=AccessToken("test-only", int(time.time()) + 3600)))
        settings = Settings(project_endpoint="https://fixture.services.ai.azure.com/api/projects/demo",
                            copilot_environment_id="test-env", copilot_schema_name="test-agent")
        store = MemoryStore()
        copilot = SimpleNamespace(start_conversation=AsyncMock(return_value="conversation"),
                                  ask_question=AsyncMock(return_value="PRIVATE FINANCE RESULT"))
        original_run = Agent.run

        async def run(agent, *args, **kwargs):
            runs.append((agent, kwargs["session"]))
            response = await original_run(agent, *args, **kwargs)
            responses.append(response)
            return response

        async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
            get_client = AIProjectClient.get_openai_client
            with patch.object(AIProjectClient, "get_openai_client",
                              lambda project, **kwargs: get_client(project, **kwargs, http_client=http)):
                model = FoundryModel(settings, credential)
            try:
                app = FinanceAgent(settings, model.client, store, model_options=model.options(),
                                   query_factory=lambda _: None,
                                   copilot_factory=lambda _: copilot)
                with patch.object(Agent, "run", run):
                    for question in ("What is margin?", "Explain it again"):
                        reply = await app.run_reply("caller-bound-key", question, object())
                        self.assertEqual(reply.text, f"PRIVATE FINANCE RESULT\n\n_Source: {KNOWLEDGE_BASE}._")
                self.assertEqual(len(runs), 2, "The application must execute a real MAF Agent per turn.")
                self.assertEqual(len(requests), 2, "There must be no second model synthesis.")
                self.assertEqual(copilot.ask_question.await_count, 2)
                self.assertEqual(copilot.start_conversation.await_count, 1)
                self.assertIsNot(runs[0][1], runs[1][1])
                self.assertNotIn("PRIVATE FINANCE RESULT", str([r.to_dict() for r in responses]))
                for response in responses:
                    results = [c for m in response.messages for c in m.contents if c.type == "function_result"]
                    self.assertEqual(len(results), 1)
                    self.assertIn(WITHHELD_RESULT, str(results[0].result))
                for request in requests:
                    self.assertFalse(request["store"])
                    self.assertFalse(request["parallel_tool_calls"])
                self.assertIn(WITHHELD_RESULT, json.dumps(requests[1]))
                for value in (requests, store.states["caller-bound-key"].history,
                              [session.to_dict() for _, session in runs]):
                    self.assertNotIn("PRIVATE FINANCE RESULT", str(value))
                    self.assertNotIn("SPECULATIVE PROSE", str(value))
            finally:
                await model.aclose()

    async def test_invalid_wire_selections_execute_no_tools(self):
        def call(name="get_statement", arguments="{}", call_id="call-1"):
            return {"type": "function_call", "id": call_id, "call_id": call_id,
                    "name": name, "arguments": arguments, "status": "completed"}

        invalid = [
            [call(), call(call_id="call-2")],
            [call(name="invented")],
            [call(call_id=" ")],
            [call(arguments='{"kpi":"Margin","kpi":"Cash"}')],
            [call(arguments='{"kpi":42}')],
            [call(arguments='{"kpi":true}')],
            [call(arguments='{"org":null}')],
            [call(arguments='{"sql":"SELECT private_data"}')],
            [call(arguments="[]")],
            [call(arguments="not json")],
            [call(arguments='{"org":NaN}')],
            [call(name="get_kpi_info")],
        ]
        factory = Mock(side_effect=AssertionError("No tool should execute."))
        app = self.app(query_factory=factory, copilot_factory=factory, data_agent_factory=factory)
        for index, output in enumerate(invalid):
            with self.subTest(output=output):
                self.model.output = output
                reply = await app.run_reply(str(index), "question", object())
                self.assertIn("could not select a valid", reply.text)
                self.assertEqual(self.store.states[str(index)].history, [{"role": "user", "content": "question"}])
        self.assertEqual(len(self.model.requests), len(invalid))
        factory.assert_not_called()

    async def test_unexpected_tool_failure_aborts_instead_of_synthesizing(self):
        self.model.route.return_value = RouteDecision("get_kpi_info", {"kpi": "Margin"}, "call")
        copilot = SimpleNamespace(start_conversation=AsyncMock(return_value="conversation"),
                                  ask_question=AsyncMock(side_effect=RuntimeError("PRIVATE ERROR BODY")))
        app = self.app(query_factory=lambda _: None, copilot_factory=lambda _: copilot)
        with self.assertLogs(level="ERROR") as logs:
            with self.assertRaisesRegex(MiddlewareFailure, "ErrorType=RuntimeError"):
                await app.run_reply("s", "question", object())
        self.assertNotIn("PRIVATE ERROR BODY", str(logs.output))
        self.assertEqual(len(self.model.requests), 1)
        self.assertEqual(self.store.states["s"].history[-1]["content"], WITHHELD_RESULT)
        self.assertEqual(app._turn_locks, {})

    async def test_concurrent_tools_keep_caller_tokens_replies_and_sessions_separate(self):
        self.model.route.return_value = RouteDecision("explore_finance", {"question": "Why?"}, "call")
        entered, release = asyncio.Event(), asyncio.Event()
        first_token, second_token = object(), object()

        async def first_query(question):
            entered.set()
            await release.wait()
            return "PRIVATE FIRST"

        first_client = SimpleNamespace(query=AsyncMock(side_effect=first_query))
        second_client = SimpleNamespace(query=AsyncMock(return_value="PRIVATE SECOND"))

        def client(tokens):
            self.assertIn(tokens, (first_token, second_token))
            return first_client if tokens is first_token else second_client

        app = self.app(query_factory=lambda _: None, data_agent_factory=client)
        first = asyncio.create_task(app.run_reply("first", "first question", first_token))
        try:
            async with asyncio.timeout(5):
                await entered.wait()
                second = await app.run_reply("second", "second question", second_token)
            self.assertIn("PRIVATE SECOND", second.text)
            self.assertFalse(first.done())
        finally:
            release.set()
        self.assertIn("PRIVATE FIRST", (await first).text)
        self.assertEqual(len(self.model.requests), 2)
        for key, question in (("first", "first question"), ("second", "second question")):
            self.assertEqual(self.store.states[key].history[0]["content"], question)
            self.assertNotIn("PRIVATE", str(self.store.states[key]))

    async def test_model_timeout_is_not_active_during_finance_execution(self):
        self.model.route.return_value = RouteDecision("explore_finance", {"question": "Why?"}, "call")
        model_timeouts = []
        real_timeout = asyncio.timeout

        def timeout(seconds):
            scope = real_timeout(seconds)
            if seconds == 60:
                model_timeouts.append(scope)
            return scope

        async def query(question):
            self.assertEqual(len(model_timeouts), 1)
            with self.assertRaisesRegex(RuntimeError, "finished Timeout"):
                model_timeouts[0].reschedule(None)
            return "analysis"

        app = self.app(query_factory=lambda _: None,
                       data_agent_factory=lambda _: SimpleNamespace(query=query))
        with patch("zavafinance.agent_policy.asyncio.timeout", side_effect=timeout):
            self.assertIn("analysis", (await app.run_reply("s", "question", object())).text)

    async def test_single_model_guard_blocks_streaming_and_second_call(self):
        guard, invoke = SingleModelTurn(), AsyncMock()
        context = SimpleNamespace(stream=False)
        await guard.process(context, invoke)
        with self.assertRaises(MiddlewareFailure):
            await guard.process(context, invoke)
        invoke.assert_awaited_once()
        with self.assertRaises(MiddlewareFailure):
            await SingleModelTurn().process(SimpleNamespace(stream=True), invoke)

    async def test_model_cancellation_persists_question_without_starting_tools(self):
        entered = asyncio.Event()

        async def wait(*args):
            entered.set()
            await asyncio.Event().wait()

        self.model.route.side_effect = wait
        factory = Mock(side_effect=AssertionError("No tool should execute."))
        app = self.app(query_factory=factory, copilot_factory=factory, data_agent_factory=factory)
        task = asyncio.create_task(app.run_reply("s", "question", object()))
        try:
            async with asyncio.timeout(5):
                await entered.wait()
        finally:
            task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        factory.assert_not_called()
        self.assertEqual(self.store.states["s"].history, [{"role": "user", "content": "question"}])
        self.assertEqual(app._turn_locks, {})
