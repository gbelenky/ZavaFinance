"""Process-owned continuation work, released only after successful HTTP acknowledgement.

Only routing identifiers and normalized input cross the queue boundary. Workers
create a fresh SDK turn; neither OAuth tokens nor TurnContext objects are retained.
Pending work and OAuth continuations have no durable recovery.
"""

import asyncio
from contextlib import asynccontextmanager
from contextvars import ContextVar
from dataclasses import dataclass, field
from urllib.parse import urlsplit

from azure.ai.agentserver.core import (
    FoundryAgentRequestContext, get_request_context, reset_request_context, set_request_context,
)
from microsoft_agents.activity import Activity, ChannelAccount, ConversationAccount
from starlette.responses import JSONResponse

from ..cards import SCHEMA, SUBMIT_ACTION, SUBMISSION_QUESTION, read_submission
from ..contracts import ProtocolError
from ..wire import json_bytes, strict_json_loads, submission_data

_SERVICE_SUFFIXES = ("botframework.com", "api.powerplatform.com", "ng.msg.teams.microsoft.com")


class QueueUnavailable(RuntimeError):
    pass


def service_url_allowed(value: str | None) -> bool:
    if not isinstance(value, str):
        return False
    try:
        uri = urlsplit(value or "")
        return bool(uri.scheme == "https" and uri.hostname and uri.port in (None, 443)
                    and not (uri.username or uri.password or uri.query or uri.fragment)
                    and (uri.hostname == "smba.trafficmanager.net"
                         or any(uri.hostname == s or uri.hostname.endswith("." + s) for s in _SERVICE_SUFFIXES)))
    except ValueError:
        return False


def normalize_continuation(activity: Activity) -> Activity:
    """Copy routing and finance input, never arbitrary payload, claims, or channel data."""
    sender, recipient, conversation = activity.from_property, activity.recipient, activity.conversation
    result = Activity(
        type="message", delivery_mode="normal", id=activity.id,
        channel_id=activity.channel_id, service_url=activity.service_url,
        from_property=ChannelAccount(id=sender.id, aad_object_id=sender.aad_object_id) if sender else None,
        recipient=ChannelAccount(id=recipient.id) if recipient else None,
        conversation=ConversationAccount(id=conversation.id, tenant_id=conversation.tenant_id,
                                         is_group=conversation.is_group) if conversation else None,
    )
    if activity.locale is not None:
        result.locale = activity.locale
    selection = read_submission(activity)
    if selection is not None:
        result.text = SUBMISSION_QUESTION
        result.value = {"schema": SCHEMA, "action": SUBMIT_ACTION, **submission_data(selection)}
    elif activity.type == "message":
        result.text = activity.text
    else:
        raise ProtocolError("Only finance messages and selections can be continued.")
    return result


@dataclass
class _ResponseGate:
    ready: asyncio.Event = field(default_factory=asyncio.Event)
    delivered: bool = False


_response_gate: ContextVar[_ResponseGate | None] = ContextVar("finance_response_gate", default=None)


class RequestBoundary:
    """Bound and strictly parse input; release queued work after a successful HTTP response."""

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http" or scope["path"] != "/activity/messages" or scope["method"] != "POST":
            return await self.app(scope, receive, send)
        body = bytearray()
        while True:
            event = await receive()
            if event["type"] == "http.disconnect":
                return
            body.extend(event.get("body", b""))
            if len(body) > 262_144:
                return await JSONResponse({"error": "Activity payload too large."}, 413)(scope, receive, send)
            if not event.get("more_body", False):
                break
        try:
            payload = strict_json_loads(bytes(body), 262_144, 16)
            if not isinstance(payload, dict):
                raise ProtocolError("Activity must be an object.")
            conversation, sender = payload.get("conversation"), payload.get("from")
            if (not isinstance(conversation, dict) or not isinstance(sender, dict)
                    or any(not isinstance(v, str) or not v.strip()
                           for v in (payload.get("type"), payload.get("channelId"),
                                     conversation.get("id"), sender.get("id")))):
                raise ProtocolError("Activity routing identifiers are required.")
            if not service_url_allowed(payload.get("serviceUrl")):
                return await JSONResponse({"error": "Untrusted connector URL."}, 401)(scope, receive, send)
            # This user-delegated host does not support agentic claims/header entities.
            payload.pop("entities", None)
            payload.pop("callerId", None)
            body = json_bytes(payload, 262_144)
        except ProtocolError:
            return await JSONResponse({"error": "Malformed activity payload."}, 400)(scope, receive, send)
        delivered_request = False
        status = 500
        gate = _ResponseGate()
        token = _response_gate.set(gate)

        async def receive_body():
            nonlocal delivered_request
            if not delivered_request:
                delivered_request = True
                return {"type": "http.request", "body": body, "more_body": False}
            return await receive()

        async def send_response(message):
            nonlocal status
            await send(message)
            if message["type"] == "http.response.start":
                status = message["status"]
            if message["type"] == "http.response.body" and not message.get("more_body", False):
                gate.delivered = status < 400
                gate.ready.set()

        try:
            await self.app(scope, receive_body, send_response)
        finally:
            gate.ready.set()
            _response_gate.reset(token)


@dataclass(frozen=True)
class _QueuedTurn:
    activity: Activity
    service_app_id: str
    platform: FoundryAgentRequestContext
    gate: _ResponseGate


class ContinuationQueue:
    """Bounded workers owned by the host lifespan, not by individual request tasks."""

    def __init__(self, dispatch, capacity=64, workers=4):
        if capacity < 1 or workers < 1:
            raise ValueError("Queue capacity and worker count must be positive.")
        self._queue = asyncio.Queue(maxsize=capacity)
        self._dispatch = dispatch
        self._worker_count = workers
        self._workers: list[asyncio.Task] = []
        self.accepting = False

    def stop_accepting(self):
        self.accepting = False

    def enqueue(self, context, activity):
        if not self.accepting or any(t.done() for t in self._workers):
            raise QueueUnavailable("The activity continuation queue is unavailable.")
        gate = _response_gate.get()
        service_app_id = context.identity.claims.get("appid")
        if gate is None or not isinstance(service_app_id, str) or not service_app_id:
            raise QueueUnavailable("Missing trusted request transport context.")
        platform = get_request_context()
        item = _QueuedTurn(normalize_continuation(activity), service_app_id,
                           FoundryAgentRequestContext(call_id=platform.call_id, user_id=platform.user_id,
                                                      session_id=platform.session_id), gate)
        try:
            self._queue.put_nowait(item)
        except asyncio.QueueFull as exc:
            raise QueueUnavailable("The activity continuation queue is full.") from exc

    async def _work(self):
        while True:
            item = await self._queue.get()
            try:
                async with asyncio.timeout(300):
                    await item.gate.ready.wait()
                    if item.gate.delivered:
                        token = set_request_context(item.platform)
                        try:
                            await self._dispatch(item)
                        finally:
                            reset_request_context(token)
            finally:
                self._queue.task_done()
                del item

    @asynccontextmanager
    async def lifespan(self):
        async with asyncio.TaskGroup() as tasks:
            self._workers = [tasks.create_task(self._work(), name=f"finance-activity-{i}")
                             for i in range(self._worker_count)]
            self.accepting = True
            try:
                yield
            finally:
                self.stop_accepting()
                for worker in self._workers:
                    worker.cancel()
                while not self._queue.empty():
                    self._queue.get_nowait()
                    self._queue.task_done()
