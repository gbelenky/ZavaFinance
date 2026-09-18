import asyncio
import importlib.util
import json
import time
import unittest
from datetime import datetime, timezone
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import httpx
from agent_framework import ChatResponse, Content, Message

from zavafinance.agent import FinanceAgent
from zavafinance.config import Settings
from zavafinance.contracts import SessionState
from zavafinance.finance.resolver import ResolverEntity
from zavafinance.model import (
    FoundryModel, RouteDecision, ToolSelectionError, WITHHELD_RESULT, decision_messages,
    history_messages, read_json, read_selection, trim_history, validate_decision,
)
from zavafinance.tools import FinanceTools
from tests.fakes import MemoryStore


def router(response=None):
    result = object.__new__(FoundryModel)
    result.settings = Settings()
    result.client = SimpleNamespace(get_response=AsyncMock(return_value=response))
    return result


def response(*calls, text=""):
    return ChatResponse(messages=[Message("assistant", [*calls, *([text] if text else [])])])


class NativeValidationTests(unittest.TestCase):
    def test_valid_calls_and_optional_statement_fields(self):
        for name, args in [("get_statement", {}), ("get_statement", {"kpi": None}),
                           ("get_statement", {"kpi": " Margin ", "org": "Nordics", "dateRange": "Q3 2026"}),
                           ("get_kpi_info", {"kpi": ""}), ("explore_finance", {"question": "Why?"})]:
            with self.subTest(name=name, args=args):
                call = RouteDecision(name, args, "call-1")
                self.assertIs(validate_decision(call), call)

    def test_invalid_native_shapes(self):
        invalid = [RouteDecision(), RouteDecision("bogus", {}, "c"), RouteDecision([], {}, "c"),
                   RouteDecision("get_kpi_info", {}, "c"), RouteDecision("get_statement", {"org": None}, "c"),
                   RouteDecision("get_statement", {"kpi": []}, "c"), RouteDecision("get_kpi_info", {"kpi": 1}, "c"),
                   RouteDecision("explore_finance", {"question": {}, "extra": ""}, "c"),
                   RouteDecision("get_statement", {"date_range": ""}, "c"), RouteDecision("get_statement", [], "c"),
                   RouteDecision("get_statement", {}, ""), RouteDecision(text=" ", arguments={}),
                   RouteDecision(text="Help", arguments=[]), RouteDecision(text="Help", call_id="c")]
        for call in invalid:
            with self.subTest(call=call), self.assertRaises(ToolSelectionError):
                validate_decision(call)

    def test_strict_json(self):
        for raw in ['{"kpi":"a","kpi":"b"}', '{"a":NaN}', '{"a":Infinity}', "{", "not JSON"]:
            with self.subTest(raw=raw), self.assertRaises(ToolSelectionError):
                read_json(raw)

    def test_history_drops_whole_native_turn_and_never_permissioned_output(self):
        history = [{"role": "user", "content": "First?"}]
        history += decision_messages(RouteDecision("get_kpi_info", {"kpi": "Margin"}, "c"))
        history += [{"role": "user", "content": "Next?"}]
        self.assertEqual(trim_history(history, 3), history[-1:])
        messages = history_messages(history)
        self.assertEqual(messages[1].contents[0].name, "get_kpi_info")
        self.assertEqual(messages[2].contents[0].result, WITHHELD_RESULT)
        history[2]["content"] = "permissioned financial answer"
        with self.assertRaises(ToolSelectionError):
            history_messages(history)

    def test_tools_are_executable_and_schemas_come_from_typed_inputs(self):
        declarations = FinanceTools(Settings(), SessionState(), "s", object(),
                                    query_factory=lambda _: None).functions()
        self.assertEqual({d.name for d in declarations}, {"get_statement", "get_kpi_info", "explore_finance"})
        for declaration in declarations:
            self.assertIsNotNone(declaration.func)
            self.assertFalse(declaration.parameters()["additionalProperties"])

    def test_statement_date_reference_is_refreshed_for_each_turn(self):
        for now in (datetime(2026, 12, 31, 23, 59, tzinfo=timezone.utc),
                    datetime(2027, 1, 1, tzinfo=timezone.utc)):
            with self.subTest(now=now), patch("zavafinance.tools.datetime") as clock:
                clock.now.return_value = now
                tools = FinanceTools(Settings(), SessionState(), "s", object(),
                                     query_factory=lambda _: None).functions()
            statement = next(tool for tool in tools if tool.name == "get_statement")
            self.assertIn(f"UTC reference date: {now.date().isoformat()}", statement.description)
            self.assertIn(f"Current calendar year: {now.year}", statement.description)
            clock.now.assert_called_once_with(timezone.utc)

    def test_replayed_call_arguments_are_json_strings_without_mutating_state(self):
        cases = [
            ("get_kpi_info", {"kpi": 'Net "Revenue"\n\u00e9'}),
            ("get_statement", {}),
            ("get_statement", {"kpi": None, "org": "EMEA", "dateRange": "Q4 2025"}),
            ("explore_finance", {"question": "Why did margin change?"}),
        ]
        for name, arguments in cases:
            with self.subTest(name=name, arguments=arguments):
                history = [{"role": "user", "content": "Previous question"}]
                history += decision_messages(RouteDecision(name, arguments, "previous"))
                stored = json.dumps(history)
                messages = history_messages(history)
                call = messages[1].contents[0]
                self.assertIsInstance(call.arguments, str)
                self.assertEqual(json.loads(call.arguments), arguments)
                self.assertEqual(call.call_id, "previous")
                self.assertEqual(messages[2].contents[0].call_id, call.call_id)
                self.assertEqual(messages[2].contents[0].result, WITHHELD_RESULT)
                self.assertEqual(json.dumps(history), stored)


class RouterTests(unittest.IsolatedAsyncioTestCase):
    async def test_one_call_prose_discarded_and_followups_preserved(self):
        decision = read_selection(response(
            Content.from_function_call("c", "get_statement", arguments='{"org":"Nordics"}'),
            text="Do not return this speculative answer."))
        self.assertEqual(decision, RouteDecision("get_statement", {"org": "Nordics"}, "c"))

    async def test_invalid_response_never_selects_one_arbitrarily(self):
        calls = [
            [Content.from_function_call("1", "get_statement", arguments={}),
             Content.from_function_call("2", "get_kpi_info", arguments={"kpi": "Margin"})],
            [Content.from_function_call("1", "get_statement", arguments={}, informational_only=True)],
            [Content.from_function_call("1", "get_statement", arguments={}, exception="invalid")],
            [Content.from_function_call("1", "get_statement", arguments='{"org":"x","org":"y"}')],
            [Content.from_function_call("1", "get_statement", arguments="[]")],
        ]
        for content in calls:
            with self.subTest(content=content), self.assertRaises(ToolSelectionError):
                read_selection(response(*content))

    async def test_plain_text_response(self):
        self.assertEqual(read_selection(response(text="I can help with finance.")).text,
                         "I can help with finance.")
        with self.assertRaises(ToolSelectionError):
            read_selection(response())

    async def test_rerank_preserves_ambiguity_rejects_unbound_ids_and_never_stores(self):
        candidates = [ResolverEntity("v1", "one", "kpi", "One"), ResolverEntity("v1", "two", "kpi", "Two")]
        for text, expected in [('{"ids":["one","two"]}', ["one", "two"]), ('{"ids":["unbound"]}', None),
                               ('{"ids":[],"answer":"wrong"}', None), ('{"ids":[1]}', None)]:
            model = router(response(text=text))
            result = await model.rerank("ambiguous", "kpi", candidates)
            self.assertEqual(json.loads(result)["ids"] if result is not None else None, expected)
            options = model.client.get_response.call_args.kwargs["options"]
            self.assertFalse(options["store"])
            self.assertNotIn("tools", options)
        model = router()
        self.assertEqual(await model.rerank("anything", "org", []), '{"ids":[]}')
        model.client.get_response.assert_not_called()

    async def test_rerank_cancellation_propagates(self):
        model = router()
        model.client.get_response.side_effect = asyncio.CancelledError()
        with self.assertRaises(asyncio.CancelledError):
            await model.rerank("one", "kpi", [ResolverEntity("v1", "one", "kpi", "One")])

    async def test_shutdown_closes_project_even_if_openai_close_fails(self):
        model = router()
        model._openai = SimpleNamespace(close=AsyncMock(side_effect=RuntimeError("close failure")))
        model._project = SimpleNamespace(close=AsyncMock())
        with self.assertRaises(RuntimeError):
            await model.aclose()
        model._openai.close.assert_awaited_once()
        model._project.close.assert_awaited_once()


@unittest.skipUnless(importlib.util.find_spec("agent_framework_openai"), "Parent must install published OpenAI connector.")
class ResponsesHTTPTests(unittest.IsolatedAsyncioTestCase):
    async def test_actual_project_constructor_is_lazy_and_does_not_own_credential(self):
        credential = SimpleNamespace(get_token=AsyncMock(), close=AsyncMock())
        model = FoundryModel(Settings(project_endpoint="https://fixture.services.ai.azure.com/api/projects/demo"),
                            credential)
        try:
            credential.get_token.assert_not_called()
            self.assertEqual(model._openai.max_retries, 0)
            self.assertEqual(model._openai.timeout, 60.0)
            self.assertEqual(str(model._openai.base_url),
                             "https://fixture.services.ai.azure.com/api/projects/demo/openai/v1/")
        finally:
            await model.aclose()
        credential.close.assert_not_called()

    async def test_real_maf_reranker_uses_stateless_structured_response_without_tools(self):
        from agent_framework.openai import OpenAIChatClient
        from openai import AsyncOpenAI
        requests = []
        def handle(request):
            requests.append(json.loads(request.content))
            return httpx.Response(200, json={
                "id": "resp-rerank", "object": "response", "created_at": 1, "status": "completed",
                "error": None, "incomplete_details": None, "model": "gpt-5.4-mini", "instructions": None,
                "output": [{"type": "message", "id": "msg-1", "status": "completed", "role": "assistant",
                            "content": [{"type": "output_text", "text": '{"ids":["one","two"]}', "annotations": []}]}],
                "parallel_tool_calls": False, "tools": [], "tool_choice": "auto", "temperature": None,
                "top_p": None, "usage": {"input_tokens": 1, "output_tokens": 1, "total_tokens": 2}})
        async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
            sdk = AsyncOpenAI(api_key="fixture-only", http_client=http, max_retries=0)
            model = router()
            model.client = OpenAIChatClient(model="gpt-5.4-mini", async_client=sdk)
            candidates = [ResolverEntity("v1", key, "org", key) for key in ["one", "two"]]
            self.assertEqual(json.loads(await model.rerank("Ambiguous office", "org", candidates)),
                             {"ids": ["one", "two"]})
            self.assertEqual(len(requests), 1)
            request = requests[0]
            self.assertFalse(request["store"])
            self.assertFalse(request.get("tools"))
            self.assertNotIn("temperature", request)
            self.assertNotIn("previous_response_id", request)
            self.assertEqual(request["reasoning"], {"effort": "low"})
            format_ = request["text"]["format"]
            self.assertEqual(format_["type"], "json_schema")
            self.assertTrue(format_["strict"])
            self.assertEqual(format_["schema"]["properties"]["ids"]["items"]["enum"], ["one", "two"])
            await sdk.close()

    async def test_real_maf_agent_wire_and_no_second_request(self):
        from azure.ai.projects.aio import AIProjectClient
        from azure.core.credentials import AccessToken
        requests = []
        credential = SimpleNamespace(get_token=AsyncMock(return_value=AccessToken("fixture-token", int(time.time()) + 3600)))

        def handle(request):
            self.assertEqual(request.headers["Authorization"], "Bearer fixture-token")
            body = json.loads(request.content)
            requests.append(body)
            return httpx.Response(200, json={
                "id": "resp-1", "object": "response", "created_at": 1, "status": "completed",
                "error": None, "incomplete_details": None, "model": "gpt-5.4-mini", "instructions": None,
                "output": [{"type": "function_call", "id": "fc-1", "call_id": "call-1",
                            "name": "get_statement", "arguments": "{}", "status": "completed"}],
                "parallel_tool_calls": False, "tools": [], "tool_choice": "auto", "temperature": None,
                "top_p": None, "usage": {"input_tokens": 1, "output_tokens": 1, "total_tokens": 2}})

        async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
            get_client = AIProjectClient.get_openai_client
            def configured_client(project, **kwargs):
                return get_client(project, **kwargs, http_client=http)
            with patch.object(AIProjectClient, "get_openai_client", configured_client):
                model = FoundryModel(Settings(project_endpoint="https://fixture.services.ai.azure.com/api/projects/demo"),
                                    credential)
            self.addAsyncCleanup(model.aclose)
            history = [{"role": "user", "content": "Define Margin"}]
            history += decision_messages(RouteDecision("get_kpi_info", {"kpi": "Margin"}, "previous"))
            store = MemoryStore()
            store.states["s"] = SessionState(history=history)
            app = FinanceAgent(model.settings, model.client, store, model_options=model.options(),
                               query_factory=lambda _: None)
            await app.run_reply("s", "show it", object())
            self.assertEqual(store.states["s"].history[-2]["arguments"], {})
            self.assertEqual(len(requests), 1)
            request = requests[0]
            self.assertFalse(request["store"])
            self.assertFalse(request["parallel_tool_calls"])
            self.assertNotIn("temperature", request)
            self.assertEqual(request["reasoning"], {"effort": "low"})
            declaration = next(t for t in request["tools"] if t["name"] == "get_statement")
            self.assertFalse(declaration["strict"])
            self.assertEqual(declaration["parameters"].get("required", []), [])
            replayed_call = next(i for i in request["input"] if i.get("type") == "function_call")
            self.assertIsInstance(replayed_call["arguments"], str)
            self.assertEqual(json.loads(replayed_call["arguments"]), {"kpi": "Margin"})
            self.assertEqual(replayed_call["call_id"], "previous")
            self.assertTrue(any(i.get("type") == "function_call_output" and i["output"] == WITHHELD_RESULT
                                for i in request["input"]))
            credential.get_token.assert_awaited()
            self.assertEqual(credential.get_token.call_args.args, ("https://ai.azure.com/.default",))
