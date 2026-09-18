import asyncio
import copy
import base64
import json
import os
import time
import unittest
from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

from azure.core.credentials import AccessToken
from azure.core.pipeline.transport import AsyncHttpTransport
from azure.core.rest import AsyncHttpResponse
from azure.ai.agentserver.core.storage import FoundryStateStore as CoreStateStore

from zavafinance.config import Settings
from zavafinance.contracts import ClarificationPrompt, SessionState
from zavafinance.model import RouteDecision, decision_messages
from zavafinance.state import FoundrySessionStore, StatePayloadError, deserialize_state, serialize_state


KEY = "A" * 52


def session():
    history = [{"role": "user", "content": "What is Margin?"}]
    history += decision_messages(RouteDecision("get_kpi_info", {"kpi": "Margin"}, "c"))
    return SessionState(history=history, copilot_conversation_id="conversation", last_kpi_name="Margin",
                        pending_clarification={
                            "request_id": "request", "field": "org", "catalog_version": "v1",
                            "arguments": {"kpi": "Margin", "org": "West", "date_range": "Q3 2026",
                                          "as_of_date": "2026-09-17", "selected_kpi_id": "margin",
                                          "selected_organization_id": None},
                            "candidate_ids": ["one", "two"], "expires_at_utc": "2026-09-17T12:00:00+00:00",
                            "owner_session_key": KEY, "candidate_label_hashes": ["A" * 64, "B" * 64],
                            "release": {"catalog_version": "v1", "search_index": "finance-v1",
                                        "embedding_deployment": "embedding", "embedding_dimensions": 3}})


class PayloadTests(unittest.TestCase):
    def test_typed_roundtrip_omits_transient_clarification(self):
        state = session()
        state.reply_clarification = ClarificationPrompt("request", "org", "private choices", [], "v1")
        payload = serialize_state(state)
        self.assertNotIn("reply_clarification", payload)
        restored = deserialize_state(payload)
        self.assertIsNone(restored.reply_clarification)
        state.reply_clarification = None
        self.assertEqual(restored, state)
        restored.history[0]["content"] = "mutated"
        self.assertNotEqual(payload["history"][0]["content"], "mutated")

    def test_corrupt_unknown_and_legacy_payloads_fail_closed(self):
        for payload in [None, "", {}, {"schema_version": 2}, {"schema_version": "1"},
                        {"schema_version": 1, "access_token": "fixture"}, {"schema_version": 1, "history": ["wrong"]}]:
            with self.subTest(payload=payload), self.assertRaises(StatePayloadError):
                deserialize_state(payload)

    def test_tool_results_and_unpaired_calls_rejected(self):
        valid = serialize_state(session())
        bad_result = copy.deepcopy(valid)
        bad_result["history"][2]["content"] = "finance answer"
        unpaired = copy.deepcopy(valid)
        unpaired["history"].pop()
        unknown_arg = copy.deepcopy(valid)
        unknown_arg["history"][1]["arguments"]["hidden"] = "unexpected"
        extra_content = copy.deepcopy(valid)
        extra_content["history"][1]["content"] = "private model text"
        for value in [bad_result, unpaired, unknown_arg, extra_content]:
            with self.subTest(value=value), self.assertRaises(StatePayloadError):
                deserialize_state(value)

    def test_permissioned_labels_not_allowed_in_pending_state(self):
        value = serialize_state(session())
        value["pending_clarification"]["candidate_labels"] = ["private label"]
        with self.assertRaises(StatePayloadError):
            deserialize_state(value)

    def test_release_owner_expiry_checks_remain_statement_responsibility(self):
        value = serialize_state(session())
        value["pending_clarification"]["release"] = None
        value["pending_clarification"]["owner_session_key"] = "other caller"
        self.assertIsNotNone(deserialize_state(value).pending_clarification)


class StoreTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.settings = Settings(session_key_salt="fixture salt", project_endpoint="https://test.services.ai.azure.com/api/projects/test")
        self.backend = SimpleNamespace(get_item=AsyncMock(return_value=None), set_item=AsyncMock(), aclose=AsyncMock())
        self.factory = patch("zavafinance.state.FoundryStateStore.get_or_create", new=AsyncMock(return_value=self.backend))
        self.create = self.factory.start()
        self.addCleanup(self.factory.stop)
        self.store = FoundrySessionStore(self.settings, object())

    async def test_lazy_single_initialization_and_missing_state(self):
        self.create.assert_not_called()
        result = await asyncio.gather(*(self.store.load(KEY) for _ in range(5)))
        self.assertTrue(all(s == SessionState() for s in result))
        self.create.assert_awaited_once()
        args, kwargs = self.create.call_args
        self.assertEqual(args[0], "zavafinance-one-python-sessions")
        self.assertFalse(kwargs["user_isolation"])
        self.assertEqual(kwargs["item_ttl_seconds"], 2592000)
        self.backend.get_item.assert_awaited_with(f"orchestrator_{KEY}")

    async def test_exact_typed_item_put_and_get(self):
        state = session()
        await self.store.save(KEY, state)
        args, kwargs = self.backend.set_item.call_args
        self.assertEqual(args, (f"orchestrator_{KEY}", {"item": serialize_state(state)}))
        self.assertEqual(kwargs, {"tags": None, "if_match": None, "require_exists": False})
        self.backend.get_item.return_value = SimpleNamespace(value=args[1])
        self.assertEqual(await self.store.load(KEY), state)
        await self.store.aclose()
        self.backend.aclose.assert_awaited_once()

    async def test_storage_error_not_recovered_as_fresh_session(self):
        self.backend.get_item.side_effect = RuntimeError("storage failure")
        with self.assertRaises(RuntimeError):
            await self.store.load(KEY)
        self.backend.set_item.assert_not_called()

    async def test_bad_envelope_not_recovered(self):
        for value in [{"item": {}}, {"value": {}}, {"item": {"schema_version": 1}, "token": "fixture"}]:
            self.backend.get_item.return_value = SimpleNamespace(value=value)
            with self.subTest(value=value), self.assertRaises(StatePayloadError):
                await self.store.load(KEY)

    async def test_raw_session_keys_rejected_before_store_access(self):
        for key in ["", "caller-id", "a" * 52, "A" * 51, "A" * 53, "../"]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                await self.store.load(key)
        self.create.assert_not_called()
        for settings in [replace(self.settings, session_key_salt=""),
                         replace(self.settings, session_store_name="dotnet-sessions"),
                         replace(self.settings, session_ttl_seconds=1)]:
            with self.assertRaises(ValueError):
                FoundrySessionStore(settings, object())


class HostedCoreHTTPTests(unittest.IsolatedAsyncioTestCase):
    async def test_real_core_hosted_http_auth_create_put_and_typed_get(self):
        requests, items = [], {}
        store_exists = False

        class Transport(AsyncHttpTransport):
            async def open(self):
                pass

            async def close(self):
                pass

            async def __aexit__(self, *args):
                await self.close()

            async def send(self, request, **kwargs):
                nonlocal store_exists
                requests.append(request)
                body = json.loads(request.content) if request.content else {}
                descriptor = {"name": "zavafinance-one-python-sessions", "object": "state_store",
                              "user_isolation": False, "item_ttl_seconds": 2592000}
                if request.method == "GET" and "/items/" not in request.url:
                    status, payload = (200, descriptor) if store_exists else (404, {"error": {"code": "not_found"}})
                elif request.method == "POST":
                    store_exists = True
                    status, payload = 201, descriptor
                    self_test.assertEqual(body["item_ttl_seconds"], 2592000)
                    self_test.assertFalse(body["user_isolation"])
                elif request.method == "PUT":
                    items[request.url] = body["value"]
                    status, payload = 200, {"key": "fixture", "object": "state_store_item_ref", "etag": "e1"}
                    self_test.assertIsInstance(body["value"]["item"], dict)
                else:
                    status, payload = (200, {"key": "fixture", "object": "state_store_item", "etag": "e1",
                                            "value": items[request.url]})
                response = Mock(spec=AsyncHttpResponse)
                response.status_code, response.headers = status, {"content-type": "application/json"}
                response.request, response.reason = request, "fixture"
                response.text.return_value = json.dumps(payload)
                response.json.return_value = payload
                response.read = AsyncMock(return_value=json.dumps(payload).encode())
                response.close = AsyncMock()
                return response

        self_test = self
        credential = SimpleNamespace(get_token=AsyncMock(return_value=AccessToken("fixture-token", int(time.time()) + 3600)))
        original_create = CoreStateStore.get_or_create
        async def create(*args, **kwargs):
            return await original_create(*args, **kwargs, transport=Transport())
        settings = Settings(session_key_salt="fixture",
                            project_endpoint="https://fixture.services.ai.azure.com/api/projects/test")
        with patch.dict(os.environ, {"FOUNDRY_HOSTING_ENVIRONMENT": "fixture"}), \
                patch("zavafinance.state.FoundryStateStore.get_or_create", side_effect=create):
            store = FoundrySessionStore(settings, credential)
            await store.save(KEY, session())
            self.assertEqual(await store.load(KEY), session())
            await store.aclose()
        self.assertEqual([r.method for r in requests], ["GET", "POST", "PUT", "GET"])
        self.assertTrue(all("/storage/" in r.url for r in requests))
        self.assertTrue(all(r.headers["Authorization"] == "Bearer fixture-token" for r in requests))
        self.assertTrue(all("x-ms-user-id" not in r.headers for r in requests))
        credential.get_token.assert_awaited_once_with("https://ai.azure.com/.default")
        encoded = base64.urlsafe_b64encode(f"orchestrator_{KEY}".encode()).decode().rstrip("=")
        self.assertIn(f"/items/{encoded}", requests[-1].url)
