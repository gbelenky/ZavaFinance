"""Compatibility boundary for the pinned native Activity and M365 1.5.0 SDKs.

Keep private SDK overrides here: OAuth cards can omit optional resources and
delivery IDs; finance messages cannot. OAuth storage is transient and replay
must start a clean turn rather than copy the original SDK state.
"""

import logging
import time
from contextvars import ContextVar

from microsoft_agents.activity import Activity, Attachment
from microsoft_agents.hosting.core import (
    AgentApplication, Authorization, ConnectorClientBase, HttpAdapterBase,
    MemoryStorage, TurnState, UserTokenClientBase,
)
from microsoft_agents.hosting.core._oauth._flow_state import _FlowStateTag
from microsoft_agents.hosting.core.app.oauth._handlers._user_authorization import _UserAuthorization

from ..cards import invoke_response, is_supported_invoke
from ..contracts import IdentityError
from .continuations import ContinuationQueue, QueueUnavailable, normalize_continuation, service_url_allowed

logger = logging.getLogger(__name__)
_SSO_FAILURE_CODES = frozenset((
    "installappfailed", "authrequestfailed", "installedappnotfound", "invokeerror",
    "resourcematchfailed", "oauthcardnotvalid", "tokenmissing",
    "userconsentrequired", "interactionrequired",
))
_processing_context: ContextVar[object] = ContextVar("finance_processing_context", default=None)
_oauth_control_send: ContextVar[bool] = ContextVar("finance_oauth_control_send", default=False)


class ActivityDeliveryError(RuntimeError):
    pass


class ActivityProcessingError(RuntimeError):
    pass


class ActivityAdapter(HttpAdapterBase):
    def _validate_service_url(self, identity, activity):
        return not identity.allow_anonymous and service_url_allowed(activity.service_url)

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


class OAuthStorage(MemoryStorage):
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
        # M365 1.5.0 passes None to a nonnullable OAuthCard field; omit absent resources.
        for name, value in (("tokenExchangeResource", resource.token_exchange_resource),
                            ("tokenPostResource", resource.token_post_resource)):
            if value is not None:
                content[name] = value.model_dump(by_alias=True, exclude_none=True)
        exchange = resource.token_exchange_resource
        logger.info("OAuth challenge: exchange_id_present=%s exchange_uri_present=%s",
                    bool(exchange and exchange.id), bool(exchange and exchange.uri))
        # Copilot may intercept this control card without assigning an activity ID.
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


class DelegatedAuthorization(Authorization):
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


class ActivityApplication(AgentApplication[TurnState]):
    continuations: ContinuationQueue

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
                await send_text(context, "I could not resume that request. Please ask again.")


async def send_text(context, text):
    await send_message(context, Activity(type="message", text=text))


async def send_message(context, activity):
    response = await context.send_activity(activity)
    if response is None or not isinstance(response.id, str) or not response.id.strip():
        raise ActivityDeliveryError("The connector did not acknowledge the reply with an activity ID.")


async def fail_safely(context, error):
    logger.error("Activity execution failed: %s", type(error).__name__)
    raise ActivityProcessingError("Activity execution failed.") from None
