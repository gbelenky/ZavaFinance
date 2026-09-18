import asyncio
import logging
import os
import unittest
from contextlib import AsyncExitStack
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import httpx
from azure.ai.agentserver.activity import ActivityAgentServerHost
from azure.ai.agentserver.core import get_request_context
from microsoft_agents.activity import Activity, ResourceResponse, SignInResource, TokenExchangeResource, TokenResponse
from microsoft_agents.hosting.core import MemoryStorage

from zavafinance.activity import (
    CHOICE_FAILURE, IDENTITY_FAILURE, _ContinuationQueue, _ResponseGate, _response_gate,
    ActivityDeliveryError, QueueUnavailable, create_activity_host, normalize_continuation,
)
from zavafinance.cards import SUBMISSION_QUESTION, create_card
from zavafinance.config import Settings
from zavafinance.contracts import (
    CallerIdentity, ClarificationOption, ClarificationPrompt, FinanceReply, IdentityError,
)

TENANT = "11111111-1111-1111-1111-111111111111"
USER = "22222222-2222-2222-2222-222222222222"
CLIENT = "33333333-3333-3333-3333-333333333333"


def prompt():
    return ClarificationPrompt("r1", "org", "Which organization?",
                               [ClarificationOption("o1", "East", "Eastern division")], "v1")


def selection():
    return create_card(prompt())["body"][2]["selectAction"]["data"]


def incoming(**changes):
    value = {"type": "message", "id": "activity-1", "text": "Revenue?",
             "channelId": "msteams", "serviceUrl": "https://smba.trafficmanager.net/teams/",
             "from": {"id": "channel-user", "aadObjectId": USER}, "recipient": {"id": "bot"},
             "conversation": {"id": "conversation-1", "tenantId": TENANT}}
    value.update(changes)
    return value


class FakeChannelFactory:
    """Synthetic OAuth service and connector, exercised through the real SDK pipeline."""

    def __init__(self):
        self.sends = []
        self.created = []
        self.tokens = []
        self.no_token = False
        self.empty_ack = False
        self.empty_oauth_ack = False
        self.send_error = None
        self.sent = asyncio.Event()
        self.exchanges = []

    async def create_user_token_client(self, context, claims, anonymous):
        if anonymous:
            raise AssertionError("Runtime must never select anonymous auth.")
        if not self.created:
            context.turn_state["synthetic-original-secret"] = "must-not-be-copied"
        self.created.append(context)
        index = len(self.created)
        token = TokenResponse(token=f"synthetic-mcs-{index}", connection_name="mcs")
        self.tokens.append(token.token)

        async def get_user_token(**kwargs):
            if kwargs["connection_name"] != "mcs":
                raise AssertionError("Only mcs authentication is allowed.")
            return None if self.no_token else token

        async def get_token_or_sign_in_resource(**kwargs):
            return SimpleNamespace(
                token_response=None if self.no_token else token,
                sign_in_resource=SignInResource(
                    sign_in_link="https://login.microsoftonline.com/synthetic",
                    token_exchange_resource=TokenExchangeResource(id="sso-resource", uri=f"api://botid-{CLIENT}")),
            )

        async def exchange_token(**kwargs):
            self.exchanges.append(kwargs["exchange_request"].token)
            return None if self.no_token else token

        return SimpleNamespace(
            close=AsyncMock(), get_user_token=get_user_token,
            get_token_or_sign_in_resource=get_token_or_sign_in_resource,
            exchange_token=exchange_token,
        )

    async def create_connector_client(self, context, claims, service_url, audience, scopes, anonymous):
        if anonymous:
            raise AssertionError("Connector must use authenticated service identity.")

        async def send(*args):
            if self.send_error is not None:
                raise self.send_error
            self.sends.append(args[-1].model_copy(deep=True))
            self.sent.set()
            if self.empty_oauth_ack and args[-1].attachments:
                if args[-1].attachments[0].content_type == "application/vnd.microsoft.card.oauth":
                    return ResourceResponse()
            return ResourceResponse() if self.empty_ack else ResourceResponse(id=f"sent-{len(self.sends)}")

        return SimpleNamespace(close=AsyncMock(), conversations=SimpleNamespace(
            send_to_conversation=send, reply_to_activity=send))


class ActivityHttpTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.stack = AsyncExitStack()
        await self.stack.__aenter__()
        logging.disable(logging.CRITICAL)
        self.stack.callback(logging.disable, logging.NOTSET)
        self.stack.enter_context(patch.dict(os.environ, {
            "FOUNDRY_HOSTING_ENVIRONMENT": "test-fixture",
            "FOUNDRY_AGENT_INSTANCE_CLIENT_ID": CLIENT,
            "FOUNDRY_AGENT_TENANT_ID": TENANT, "AZURE_TENANT_ID": TENANT,
            "FOUNDRY_PROJECT_ENDPOINT": "https://fixture.services.ai.azure.com/api/projects/test",
            "OTEL_SDK_DISABLED": "true", "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT": "true",
        }, clear=True))
        self.settings = Settings(obo_tenant_id=TENANT, obo_client_id=CLIENT, obo_audience=CLIENT,
                                 session_key_salt="separate-python-test-salt")
        self.factory = FakeChannelFactory()
        self.validator = SimpleNamespace(validate=AsyncMock(return_value=CallerIdentity(TENANT, USER)))
        self.calls = []
        self.started = asyncio.Event()
        self.release = asyncio.Event()
        self.release.set()
        self.reply = FinanceReply("The answer.")

        async def run_reply(session_key, question, provider, submission=None):
            self.calls.append((session_key, question, provider._assertion, submission, get_request_context().call_id))
            self.started.set()
            await self.release.wait()
            return self.reply

        self.host = create_activity_host(
            self.settings, SimpleNamespace(run_reply=run_reply), self.validator,
            channel_service_client_factory=self.factory, queue_capacity=1, queue_workers=1,
        )
        self.errors = []
        safe_error = self.host.adapter.on_turn_error

        async def record_error(context, error):
            self.errors.append(error)
            await safe_error(context, error)

        self.host.adapter.on_turn_error = record_error
        await self.stack.enter_async_context(self.host.router.lifespan_context(self.host))
        self.client = await self.stack.enter_async_context(httpx.AsyncClient(
            transport=httpx.ASGITransport(app=self.host), base_url="https://native.test"))

    async def asyncTearDown(self):
        self.release.set()
        await self.stack.aclose()

    async def post(self, body, **kwargs):
        return await self.client.post("/activity/messages", json=body, **kwargs)

    async def drain(self):
        await asyncio.wait_for(self.host.agent_app.continuations._queue.join(), 5)

    async def test_actual_host_message_and_attachment_only_delivery(self):
        self.assertIsInstance(self.host, ActivityAgentServerHost)
        self.reply = FinanceReply("Choose", prompt())
        response = await self.post(incoming(text="1", deliveryMode="expectReplies"))
        self.assertEqual(202, response.status_code, (response.text, self.errors))
        self.assertEqual("1", self.calls[0][1])
        self.assertIsNone(self.calls[0][3])
        self.assertIsNone(self.factory.sends[-1].text)
        self.assertEqual(1, len(self.factory.sends[-1].attachments))
        self.validator.validate.assert_awaited()

    async def test_hosted_oauth_uses_explicit_memory_storage_not_foundry_storage(self):
        self.assertTrue(self.host.config.is_hosted)
        self.assertIsInstance(self.host.agent_app._storage, MemoryStorage)
        self.assertIs(self.host.agent_app._storage, self.host.agent_app.auth._storage)
        self.assertIsNone(self.host._owned_storage)

    async def test_direct_host_construction_overrides_content_capture_to_false(self):
        self.assertEqual("false", os.environ["OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"])

    async def test_empty_message_and_identity_failures_do_not_call_finance(self):
        response = await self.post(incoming(text=" "))
        self.assertEqual(202, response.status_code)
        self.assertEqual([], self.calls)
        self.validator.validate.side_effect = IdentityError("synthetic rejection")
        await self.post(incoming())
        self.assertEqual(IDENTITY_FAILURE, self.factory.sends[-1].text)
        self.assertEqual([], self.calls)

    async def test_payload_mismatch_cannot_choose_finance_identity(self):
        response = await self.post(incoming(conversation={"id": "conversation-1", "tenantId": CLIENT}))
        self.assertEqual(202, response.status_code)
        self.assertEqual(IDENTITY_FAILURE, self.factory.sends[-1].text)
        self.assertEqual([], self.calls)

    async def test_queued_turn_revalidates_user_and_rejects_revocation(self):
        self.validator.validate.side_effect = [CallerIdentity(TENANT, USER), IdentityError("revoked")]
        response = await self.post(incoming(type="invoke", name="task/submit", value={"data": selection()}))
        self.assertEqual(200, response.status_code, response.text)
        await self.drain()
        self.assertEqual([], self.calls)
        self.assertEqual(2, self.validator.validate.await_count)
        self.assertEqual(IDENTITY_FAILURE, self.factory.sends[-1].text)

    async def test_invokes_ack_early_and_continue_with_fresh_normal_transport(self):
        for name, value in (
            ("adaptiveCard/action", {"action": {"type": "Action.Execute", "data": selection()}}),
            ("task/submit", {"data": selection()}),
        ):
            with self.subTest(name=name):
                self.release.clear()
                self.started.clear()
                body = incoming(type="invoke", name=name, value=value, deliveryMode="expectReplies",
                                text="untrusted question", channelData={"bearer": "must-not-be-copied"},
                                entities=[{"type": "agentic", "tenantId": CLIENT}])
                response = await asyncio.wait_for(self.post(body, headers={"x-agent-foundry-call-id": "fixture-call"}), 2)
                self.assertEqual(200, response.status_code, response.text)
                self.assertEqual(200 if name == "adaptiveCard/action" else None,
                                 response.json().get("statusCode"))
                await asyncio.wait_for(self.started.wait(), 2)
                continuation = self.factory.created[-1]
                self.assertEqual("message", continuation.activity.type)
                self.assertEqual("normal", continuation.activity.delivery_mode)
                self.assertFalse(continuation.activity.name)
                self.assertFalse(continuation.activity.channel_data)
                self.assertFalse(continuation.activity.entities)
                self.assertNotIn("synthetic-original-secret", continuation.turn_state)
                self.assertEqual(SUBMISSION_QUESTION, self.calls[-1][1])
                self.assertEqual(self.factory.tokens[-1], self.calls[-1][2])
                self.assertEqual("o1", self.calls[-1][3].option_id)
                self.assertEqual("fixture-call", self.calls[-1][4])
                self.release.set()
                await self.drain()
                self.assertEqual("The answer.", self.factory.sends[-1].text)

    async def test_queue_is_bounded_and_refuses_after_shutdown(self):
        self.release.clear()
        body = incoming(type="invoke", name="task/submit", value={"data": selection()})
        self.assertEqual(200, (await self.post(body)).status_code)
        await asyncio.wait_for(self.started.wait(), 2)
        self.assertEqual(200, (await self.post(body)).status_code)
        self.assertEqual(503, (await self.post(body)).status_code)
        self.host.agent_app.continuations.stop_accepting()
        self.assertEqual(503, (await self.post(body)).status_code)
        self.release.set()
        await self.drain()

    async def test_invalid_and_unauthorized_invoke_acks(self):
        body = incoming(type="invoke", name="adaptiveCard/action",
                        value={"action": {"type": "Action.Execute", "data": {**selection(), "resolvedId": "all"}}})
        response = await self.post(body)
        self.assertEqual(400, response.json()["statusCode"], response.text)
        self.validator.validate.side_effect = IdentityError("rejected")
        response = await self.post(incoming(type="invoke", name="task/submit", value={"data": selection()}))
        self.assertEqual(401, response.status_code, response.text)
        self.assertEqual([], self.calls)

    async def test_invalid_message_choice_and_empty_connector_ack(self):
        await self.post(incoming(value={**selection(), "resolvedId": 42}))
        self.assertEqual(CHOICE_FAILURE, self.factory.sends[-1].text)
        self.factory.empty_ack = True
        response = await self.post(incoming())
        self.assertEqual(500, response.status_code)
        self.assertNotIn("synthetic-mcs", response.text)

    async def test_input_and_outbound_url_boundary_before_oauth(self):
        for url in ("http://smba.trafficmanager.net/", "https://localhost/", "https://evilbotframework.com/",
                    "https://user:secret@smba.trafficmanager.net/", "https://smba.trafficmanager.net:444/",
                    "https://customer-owned.trafficmanager.net/"):
            with self.subTest(url=url):
                self.assertEqual(401, (await self.post(incoming(serviceUrl=url))).status_code)
        self.assertEqual([], self.factory.created)
        response = await self.client.post("/activity/messages", content='{"type":"message","type":"invoke"}',
                                          headers={"content-type": "application/json"})
        self.assertEqual(400, response.status_code)
        self.assertEqual(413, (await self.client.post("/activity/messages", content=b"x" * 262_145)).status_code)
        self.assertEqual(400, (await self.post(incoming(conversation={}))).status_code)
        self.assertEqual(405, (await self.client.get("/activity/messages")).status_code)

    async def test_oauth_sign_in_continuation_retains_no_token_or_payload(self):
        self.factory.no_token = True
        response = await self.post(incoming(channelData={"token": "do-not-retain"}, entities=[{"secret": "not-retained"}]))
        self.assertEqual(202, response.status_code, (response.text, self.errors))
        self.assertEqual([], self.calls)
        stored = repr(self.host.agent_app._storage._memory)
        self.assertNotIn("do-not-retain", stored)
        self.assertNotIn("not-retained", stored)
        self.assertNotIn("synthetic-mcs", stored)
        await self.post(incoming(text="000000"))
        self.assertNotIn("000000", repr(self.host.agent_app._storage._memory))
        self.factory.no_token = False
        response = await self.post(incoming(type="invoke", name="signin/verifyState", value={"state": "123456"}))
        self.assertEqual(200, response.status_code, response.text)
        await self.drain()
        self.assertEqual("Revenue?", self.calls[-1][1])
        self.assertEqual("normal", self.factory.created[-1].activity.delivery_mode)

    async def test_sso_token_exchange_never_replays_supplied_bearer(self):
        self.factory.no_token = True
        await self.post(incoming())
        self.factory.no_token = False
        response = await self.post(incoming(type="invoke", name="signin/tokenExchange",
                                            value={"id": "sso", "connectionName": "mcs", "token": "untrusted-sso-token"}))
        self.assertEqual(200, response.status_code, (response.text, self.errors))
        await self.drain()
        self.assertEqual(["untrusted-sso-token"], self.factory.exchanges)
        self.assertEqual(self.factory.tokens[-1], self.calls[-1][2])
        self.assertNotIn("untrusted-sso-token", repr(self.host.agent_app._storage._memory))
        self.assertFalse(self.factory.created[-1].activity.value)
        self.assertEqual("Revenue?", self.calls[-1][1])

    async def test_sso_control_card_empty_ack_preserves_continuation(self):
        self.factory.no_token = True
        self.factory.empty_oauth_ack = True
        response = await self.post(incoming())
        self.assertEqual(202, response.status_code, (response.text, self.errors))
        self.assertEqual([], self.calls)
        card = self.factory.sends[-1].attachments[0].content
        self.assertEqual({"id": "sso-resource", "uri": f"api://botid-{CLIENT}"}, card["tokenExchangeResource"])
        self.factory.no_token = False
        response = await self.post(incoming(type="invoke", name="signin/tokenExchange",
                                            value={"id": "sso-resource", "connectionName": "mcs",
                                                   "token": "untrusted-sso-token"}))
        self.assertEqual(200, response.status_code, (response.text, self.errors))
        await self.drain()
        self.assertEqual("Revenue?", self.calls[-1][1])
        self.assertEqual(self.factory.tokens[-1], self.calls[-1][2])
        self.assertEqual([], self.errors)
        self.assertNotIn("untrusted-sso-token", repr(self.host.agent_app._storage._memory))

    async def test_oauth_connector_http_failure_is_not_accepted(self):
        self.factory.no_token = True
        self.factory.send_error = httpx.HTTPStatusError(
            "Synthetic connector rejection", request=httpx.Request("POST", "https://connector.test"),
            response=httpx.Response(401))
        response = await self.post(incoming())
        self.assertEqual(500, response.status_code)
        self.assertEqual([], self.calls)
        self.assertIsInstance(self.errors[-1], httpx.HTTPStatusError)

    async def test_sso_client_failure_logs_only_safe_code_and_keeps_question(self):
        self.factory.no_token = True
        self.assertEqual(202, (await self.post(incoming())).status_code)
        for code, expected in (("resourcematchfailed", "resourcematchfailed"),
                               ("untrusted-secret", "unknown")):
            with patch("zavafinance.activity.logger.warning") as warning:
                response = await self.post(incoming(type="invoke", name="signin/failure",
                                                   value={"code": code, "message": "untrusted-secret"}))
                self.assertEqual(200, response.status_code, (response.text, self.errors))
                warning.assert_called_with("SSO client failure: code=%s", expected)
                self.assertEqual([], self.calls)
                self.assertNotIn("untrusted-secret", repr(self.host.agent_app._storage._memory))

    async def test_finance_ack_stays_strict_after_oauth_control_send(self):
        self.factory.no_token = True
        self.factory.empty_oauth_ack = True
        self.assertEqual(202, (await self.post(incoming())).status_code)
        self.factory.no_token = False
        response = await self.post(incoming(type="invoke", name="signin/tokenExchange",
                                            value={"id": "sso-resource", "connectionName": "mcs",
                                                   "token": "untrusted-sso-token"}))
        self.assertEqual(200, response.status_code, (response.text, self.errors))
        await self.drain()
        self.factory.empty_ack = True
        self.assertEqual(500, (await self.post(incoming())).status_code)
        self.assertIsInstance(self.errors[-1], ActivityDeliveryError)

    async def test_unauthenticated_clarification_has_protocol_401(self):
        self.factory.no_token = True
        response = await self.post(incoming(type="invoke", name="adaptiveCard/action", deliveryMode="expectReplies",
                                            value={"action": {"type": "Action.Execute", "data": selection()}}))
        self.assertEqual(200, response.status_code, response.text)
        self.assertEqual(401, response.json()["statusCode"])
        self.assertEqual([], self.calls)

    async def test_no_local_or_anonymous_runtime(self):
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaises(RuntimeError):
                create_activity_host(self.settings, object(), self.validator)

    async def test_parent_shutdown_hook_runs_once_after_queue_workers_finish(self):
        observed = []

        @self.host.shutdown_handler
        async def close_parent_dependencies():
            observed.append(all(task.done() for task in self.host.agent_app.continuations._workers))

        await self.stack.aclose()
        self.assertEqual([True], observed)
        await self.stack.aclose()
        self.assertEqual([True], observed)


class QueueTests(unittest.IsolatedAsyncioTestCase):
    async def test_ack_gate_cancellation_and_no_fire_and_forget(self):
        dispatch = AsyncMock()
        queue = _ContinuationQueue(dispatch, capacity=1, workers=1)
        gate = _ResponseGate()
        context = SimpleNamespace(identity=SimpleNamespace(claims={"appid": CLIENT}))
        token = _response_gate.set(gate)
        try:
            async with queue.lifespan():
                queue.enqueue(context, Activity.model_validate(incoming()))
                await asyncio.sleep(0)
                dispatch.assert_not_awaited()
                gate.ready.set()  # disconnected/failed response; never process the queued request
                await asyncio.wait_for(queue._queue.join(), 2)
                dispatch.assert_not_awaited()
                gate = _ResponseGate()
                _response_gate.set(gate)
                queue.enqueue(context, Activity.model_validate(incoming()))
            self.assertTrue(all(task.done() for task in queue._workers))
            with self.assertRaises(QueueUnavailable):
                queue.enqueue(context, Activity.model_validate(incoming()))
        finally:
            _response_gate.reset(token)

    async def test_normalization_uses_only_allowed_selection_fields(self):
        activity = Activity.model_validate(incoming(type="invoke", name="task/submit",
                                                    value={"data": selection()}, deliveryMode="expectReplies",
                                                    channelData={"bearer": "secret"}))
        normalized = normalize_continuation(activity)
        self.assertEqual("message", normalized.type)
        self.assertEqual("normal", normalized.delivery_mode)
        self.assertFalse(normalized.name)
        self.assertFalse(normalized.channel_data)
        self.assertEqual(selection(), normalized.value)

    async def test_shutdown_cancels_inflight_work_and_joins_workers(self):
        started, cancelled = asyncio.Event(), asyncio.Event()

        async def dispatch(item):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        queue = _ContinuationQueue(dispatch)
        gate = _ResponseGate(delivered=True)
        gate.ready.set()
        token = _response_gate.set(gate)
        try:
            async with queue.lifespan():
                queue.enqueue(SimpleNamespace(identity=SimpleNamespace(claims={"appid": CLIENT})),
                              Activity.model_validate(incoming()))
                await asyncio.wait_for(started.wait(), 2)
            self.assertTrue(cancelled.is_set())
            self.assertTrue(all(task.done() for task in queue._workers))
        finally:
            _response_gate.reset(token)

    async def test_worker_failure_is_observable_and_not_recovered(self):
        queue = _ContinuationQueue(AsyncMock(side_effect=RuntimeError("synthetic failure")))
        gate = _ResponseGate(delivered=True)
        gate.ready.set()
        token = _response_gate.set(gate)
        try:
            with self.assertRaises(ExceptionGroup):
                async with queue.lifespan():
                    queue.enqueue(SimpleNamespace(identity=SimpleNamespace(claims={"appid": CLIENT})),
                                  Activity.model_validate(incoming()))
                    await asyncio.sleep(1)
            self.assertFalse(queue.accepting)
            self.assertTrue(all(task.done() for task in queue._workers))
        finally:
            _response_gate.reset(token)
