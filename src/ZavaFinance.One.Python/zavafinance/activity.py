"""Native Foundry Activity host. The container must remain behind trusted Foundry ingress."""

import asyncio
import logging
import os
import time
from contextlib import asynccontextmanager
from contextvars import ContextVar
from dataclasses import dataclass, field
from urllib.parse import urlsplit

import aiohttp
import httpx
from azure.ai.agentserver.activity import ActivityAgentServerHost, get_hosted_agent_env
from azure.ai.agentserver.core import (
    FoundryAgentRequestContext, get_request_context, reset_request_context, set_request_context,
)
from azure.core.exceptions import ClientAuthenticationError, HttpResponseError, ServiceRequestError
from microsoft_agents.activity import Activity, Attachment, ChannelAccount, ConversationAccount, load_configuration_from_env
from microsoft_agents.authentication.msal import MsalConnectionManager
from microsoft_agents.hosting.core import (
    AgentApplication, AuthHandler, Authorization, ClaimsIdentity, ConnectorClientBase,
    HttpAdapterBase, MemoryStorage, RestChannelServiceClientFactory, TurnState, UserTokenClientBase,
)
from microsoft_agents.hosting.core._oauth._flow_state import _FlowStateTag
from microsoft_agents.hosting.core.app.oauth._handlers._user_authorization import _UserAuthorization
from starlette.responses import JSONResponse

from .cards import (
    SCHEMA, SUBMIT_ACTION, SUBMISSION_QUESTION, create_message, invoke_response,
    is_supported_invoke, read_submission,
)
from .config import Settings
from .contracts import IdentityError, ProtocolError
from .identity import OboTokenProvider, SessionKeyProvider, UserAssertionValidator, assert_payload_agrees
from .runtime import enforce_telemetry_privacy
from .wire import json_bytes, strict_json_loads, submission_data

logger = logging.getLogger(__name__)
IDENTITY_FAILURE = "I could not confirm who you are, so I cannot look anything up on your behalf. Please sign in and try again."
CHOICE_FAILURE = "I could not read that choice. Please use the latest card, or type your question again."
PERMISSION_FAILURE = "I could not obtain permission to answer on your behalf. Please sign in and try again."
SERVICE_FAILURE = "A finance service could not be reached. Please try again."
_SERVICE_SUFFIXES = ("botframework.com", "api.powerplatform.com", "ng.msg.teams.microsoft.com")
_SSO_FAILURE_CODES = frozenset((
    "installappfailed", "authrequestfailed", "installedappnotfound", "invokeerror",
    "resourcematchfailed", "oauthcardnotvalid", "tokenmissing",
    "userconsentrequired", "interactionrequired",
))


class ActivityDeliveryError(RuntimeError):
    pass


class ActivityProcessingError(RuntimeError):
    pass


class QueueUnavailable(RuntimeError):
    pass


def _service_url_allowed(value: str | None) -> bool:
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


def _routing_activity(activity: Activity) -> Activity:
    """Copy transport identifiers, not arbitrary payload, entities, claims, or channel data."""
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
    return result


def normalize_continuation(activity: Activity) -> Activity:
    result = _routing_activity(activity)
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
_processing_context: ContextVar[object] = ContextVar("finance_processing_context", default=None)
_oauth_control_send: ContextVar[bool] = ContextVar("finance_oauth_control_send", default=False)


class _RequestBoundary:
    """Bound and strictly parse input; publish queue work only after a successful HTTP response."""

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
            if not _service_url_allowed(payload.get("serviceUrl")):
                return await JSONResponse({"error": "Untrusted connector URL."}, 401)(scope, receive, send)
            # Agentic claims/header entities are not supported by this simple user-delegated host.
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


class _SafeAdapter(HttpAdapterBase):
    def _validate_service_url(self, identity, activity):
        return not identity.allow_anonymous and _service_url_allowed(activity.service_url)

    def _create_turn_context(self, *args, **kwargs):
        context = super()._create_turn_context(*args, **kwargs)
        _processing_context.set(context)
        return context

    async def process_activity(self, claims_identity, activity, callback):
        if not self._validate_service_url(claims_identity, activity):
            raise PermissionError("A trusted connector URL and authenticated service identity are required.")
        if activity.type in ("message", "invoke"):
            activity.delivery_mode = "normal"
        token = _processing_context.set(None)
        completed = False
        try:
            result = await super().process_activity(claims_identity, activity, callback)
            completed = True
            return result
        finally:
            context = _processing_context.get()
            _processing_context.reset(token)
            # M365 1.5.0 closes these only on a successful pipeline execution.
            if not completed and context is not None:
                try:
                    connector = context.services.get(ConnectorClientBase)
                    if connector is not None:
                        await connector.close()
                finally:
                    user_tokens = context.services.get(UserTokenClientBase)
                    if user_tokens is not None:
                        await user_tokens.close()

    async def send_activities(self, context, activities):
        responses = await super().send_activities(context, activities)
        for activity, response in zip(activities, responses, strict=True):
            if (activity.type == "message" and context.activity.delivery_mode != "expectReplies"
                    and not _oauth_control_send.get()):
                if response is None or not isinstance(response.id, str) or not response.id.strip():
                    raise ActivityDeliveryError("The connector did not acknowledge the reply with an activity ID.")
        return responses


class _AuthStorage(MemoryStorage):
    """Transient OAuth state only; bounded, expiring, and deliberately without recovery."""

    def __init__(self):
        super().__init__()
        self._expiry: dict[str, float] = {}

    def _expire(self):
        now = time.monotonic()
        for key in list(self._expiry):
            if self._expiry[key] <= now:
                self._expiry.pop(key)
                self._memory.pop(key, None)

    async def read(self, keys, **kwargs):
        self._expire()
        return await super().read(keys, **kwargs)

    async def write(self, changes):
        if not changes or any(not key for key in changes):
            raise ValueError("OAuth storage keys are required.")
        async with self._lock:
            self._expire()
            if len(self._memory.keys() | changes.keys()) > 2048:
                raise QueueUnavailable("OAuth state capacity reached.")
            serialized = {key: value.store_item_to_json() for key, value in changes.items()}
            self._memory.update(serialized)
            self._expiry.update({key: time.monotonic() + 900 for key in changes})

    async def delete(self, keys):
        await super().delete(keys)
        for key in keys:
            self._expiry.pop(key, None)


class _SafeUserAuthorization(_UserAuthorization):
    async def _handle_flow_response(self, context, flow_response):
        operation = context.activity.name if context.activity.name in (
            "signin/tokenExchange", "signin/verifyState", "signin/failure") else (
                context.activity.type if context.activity.type in ("message", "event") else "other")
        logger.info("OAuth transition: operation=%s state=%s error=%s token_present=%s",
                    operation, flow_response.flow_state.tag, flow_response.flow_error_tag,
                    bool(flow_response.token_response and flow_response.token_response.token))
        if flow_response.flow_state.tag != _FlowStateTag.BEGIN:
            return await super()._handle_flow_response(context, flow_response)
        resource = flow_response.sign_in_resource
        if resource is None or not resource.sign_in_link:
            raise IdentityError("The OAuth service did not provide a sign-in resource.")
        content = {
            "text": "Sign in", "connectionName": flow_response.flow_state.connection,
            "buttons": [{"title": "Sign in", "type": "signin", "value": resource.sign_in_link}],
        }
        # M365 1.5.0 explicitly passes None to a nonnullable OAuthCard field.
        # Omit absent optional resources rather than rejecting valid OAuth responses.
        for name, value in (("tokenExchangeResource", resource.token_exchange_resource),
                            ("tokenPostResource", resource.token_post_resource)):
            if value is not None:
                content[name] = value.model_dump(by_alias=True, exclude_none=True)
        exchange = resource.token_exchange_resource
        logger.info("OAuth challenge: exchange_id_present=%s exchange_uri_present=%s",
                    bool(exchange and exchange.id), bool(exchange and exchange.uri))
        # Copilot can intercept this control card without assigning a message ID.
        # HTTP failures still propagate; finance replies still require a delivery ID.
        token = _oauth_control_send.set(True)
        try:
            response = await context.send_activity(Activity(type="message", attachments=[
                Attachment(content_type="application/vnd.microsoft.card.oauth", content=content),
            ]))
            logger.info("OAuth challenge accepted by connector: activity_id_present=%s",
                        bool(response and response.id))
        finally:
            _oauth_control_send.reset(token)


class _SafeAuthorization(Authorization):
    def _init_handlers(self):
        for name, handler in self._handler_settings.items():
            if name != "mcs" or handler.auth_type != "userauthorization":
                raise ValueError("Only delegated mcs user authorization is supported.")
            self._handlers[name] = _SafeUserAuthorization(
                storage=self._storage, connection_manager=self._connection_manager, auth_handler=handler)

    async def _save_sign_in_state(self, context, state):
        original = await self._load_sign_in_state(context)
        if original is not None and original.continuation_activity is not None:
            state.continuation_activity = normalize_continuation(original.continuation_activity)
        elif state.continuation_activity is not None:
            if state.continuation_activity.type != "message" and not is_supported_invoke(state.continuation_activity):
                return
            state.continuation_activity = normalize_continuation(state.continuation_activity)
        await super()._save_sign_in_state(context, state)


@dataclass(frozen=True)
class _QueuedTurn:
    activity: Activity
    service_app_id: str
    platform: FoundryAgentRequestContext
    gate: _ResponseGate


class _ContinuationQueue:
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


class _SafeApplication(AgentApplication[TurnState]):
    continuations: _ContinuationQueue

    async def on_turn(self, context):
        sso_failure = context.activity.type == "invoke" and context.activity.name == "signin/failure"
        if sso_failure:
            value = context.activity.value
            code = value.get("code") if isinstance(value, dict) else None
            known = isinstance(code, str) and code in _SSO_FAILURE_CODES
            logger.warning("SSO client failure: code=%s", code if known else "unknown")
        await super().on_turn(context)
        acknowledged = context.turn_state.get(self.adapter.INVOKE_RESPONSE_KEY)
        if sso_failure and not acknowledged:
            await context.send_activity(Activity(type="invokeResponse", value={"status": 200}))
        elif is_supported_invoke(context.activity) and not acknowledged:
            await context.send_activity(invoke_response(context.activity.name, 401))

    async def _handle_turn_skip(self, context, turn_state, result):
        # Pinned SDK override: never copy turn_state or retain the original TurnContext.
        if result.should_replay:
            try:
                self.continuations.enqueue(context, result.continuation_activity or context.activity)
            except QueueUnavailable:
                await _send_text(context, "I could not resume that request. Please ask again.")


async def _send_text(context, text):
    await _send_message(context, Activity(type="message", text=text))


async def _send_message(context, activity):
    response = await context.send_activity(activity)
    if response is None or not isinstance(response.id, str) or not response.id.strip():
        raise ActivityDeliveryError("The connector did not acknowledge the reply with an activity ID.")


def create_activity_host(settings: Settings, orchestrator, validator=None, *,
                         channel_service_client_factory=None, queue_capacity=64, queue_workers=4):
    """Create the real native host; injected client factories are for isolated transport tests."""
    if not os.environ.get("FOUNDRY_HOSTING_ENVIRONMENT") or not os.environ.get("FOUNDRY_AGENT_INSTANCE_CLIENT_ID"):
        raise RuntimeError("Native Activity hosting requires protected Foundry ingress and an agent instance identity.")
    enforce_telemetry_privacy()
    config = load_configuration_from_env(get_hosted_agent_env())
    connections = MsalConnectionManager(**config)
    storage = _AuthStorage()
    adapter = _SafeAdapter(channel_service_client_factory=(
        channel_service_client_factory or RestChannelServiceClientFactory(connections)))
    auth = _SafeAuthorization(storage, connections, auth_handlers={
        "mcs": AuthHandler(name="mcs", auth_type="UserAuthorization",
                           abs_oauth_connection_name=settings.oauth_connection_name,
                           title="Sign in", text="Sign in to Zava Finance"),
    }, auto_sign_in=False, use_cache=False)
    app = _SafeApplication(storage=storage, adapter=adapter, authorization=auth,
                           start_typing_timer=False,
                           long_running_messages=False, **config)
    validator = validator or UserAssertionValidator(settings)
    keys = SessionKeyProvider(settings.session_key_salt)

    async def fail_safely(context, error):
        logger.error("Activity execution failed: %s", type(error).__name__)
        raise ActivityProcessingError("Activity execution failed.") from None

    adapter.on_turn_error = fail_safely
    app.error(fail_safely)

    async def dispatch(item):
        # Only outbound service claims are retained, never a bearer token or user identity.
        identity = ClaimsIdentity({"appid": item.service_app_id, "aud": item.service_app_id},
                                  authentication_type="Bearer")
        await adapter.process_activity(identity, item.activity, app.on_turn)

    continuations = _ContinuationQueue(dispatch, queue_capacity, queue_workers)
    app.continuations = continuations

    async def resolve(context):
        response = await app.auth.get_token(context, auth_handler_id="mcs")
        if response is None or not response.token:
            raise IdentityError("No delegated mcs assertion.")
        caller = await validator.validate(response.token)
        assert_payload_agrees(context.activity, caller)
        return caller, response.token

    async def handle(context, state):
        activity = context.activity
        invoke = is_supported_invoke(activity)
        try:
            caller, assertion = await resolve(context)
        except IdentityError:
            if invoke:
                await context.send_activity(invoke_response(activity.name, 401))
            else:
                await _send_text(context, IDENTITY_FAILURE)
            return
        if not activity.conversation or not (activity.conversation.id or "").strip():
            if invoke:
                await context.send_activity(invoke_response(activity.name, 400))
            else:
                await _send_text(context, "A conversation identifier is required.")
            return
        try:
            selection = read_submission(activity)
            if invoke and selection is None:
                raise ProtocolError("A clarification selection is required.")
        except ProtocolError:
            if invoke:
                await context.send_activity(invoke_response(activity.name, 400))
            else:
                await _send_text(context, CHOICE_FAILURE)
            return
        if invoke:
            try:
                continuations.enqueue(context, activity)
            except QueueUnavailable:
                await context.send_activity(invoke_response(activity.name, 503))
                return
            await context.send_activity(invoke_response(activity.name, 200))
            return
        question = SUBMISSION_QUESTION if selection else (activity.text or "").strip()
        if not question:
            await _send_text(context, "Ask me what a KPI means, for a figure, or why something moved.")
            return
        try:
            async with OboTokenProvider(settings, assertion) as provider:
                reply = await orchestrator.run_reply(keys.create(caller, activity.conversation.id),
                                                     question, provider, selection)
        except ClientAuthenticationError:
            await _send_text(context, PERMISSION_FAILURE)
            return
        except (aiohttp.ClientError, httpx.HTTPError, HttpResponseError, ServiceRequestError, TimeoutError):
            await _send_text(context, SERVICE_FAILURE)
            return
        await _send_message(context, create_message(reply))

    app.activity("message", auth_handlers=["mcs"])(handle)
    app.add_route(lambda context: is_supported_invoke(context.activity), handle,
                  is_invoke=True, auth_handlers=["mcs"])

    @app.on_sign_in_failure
    async def sign_in_failed(context, state, connection_id):
        if is_supported_invoke(context.activity):
            await context.send_activity(invoke_response(context.activity.name, 401))
        else:
            await _send_text(context, IDENTITY_FAILURE)

    # A prebuilt app bypasses b3's hosted FoundryStorage default for all OAuth state.
    host = ActivityAgentServerHost(agent_app=app)
    host.add_middleware(_RequestBoundary)
    original_lifespan = host.router.lifespan_context

    @asynccontextmanager
    async def lifespan(application):
        async with original_lifespan(application):
            async with continuations.lifespan():
                try:
                    yield
                finally:
                    continuations.stop_accepting()

    host.router.lifespan_context = lifespan
    host.register_pre_shutdown_callback(continuations.stop_accepting)
    return host
