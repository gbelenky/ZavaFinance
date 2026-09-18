"""Native M365 composition and finance handlers, behind trusted Foundry ingress."""

import os
from contextlib import asynccontextmanager

import aiohttp
import httpx
from azure.ai.agentserver.activity import ActivityAgentServerHost, get_hosted_agent_env
from azure.core.exceptions import ClientAuthenticationError, HttpResponseError, ServiceRequestError
from microsoft_agents.activity import load_configuration_from_env
from microsoft_agents.authentication.msal import MsalConnectionManager
from microsoft_agents.hosting.core import AuthHandler, ClaimsIdentity, RestChannelServiceClientFactory

from .cards import SUBMISSION_QUESTION, create_message, invoke_response, is_supported_invoke, read_submission
from .channel.continuations import ContinuationQueue, QueueUnavailable, RequestBoundary
from .channel.continuations import normalize_continuation as normalize_continuation
from .channel.sdk_compat import (
    ActivityAdapter, ActivityApplication, DelegatedAuthorization, OAuthStorage,
    fail_safely, send_message, send_text,
)
from .config import Settings
from .contracts import FinanceApplication, IdentityError, ProtocolError
from .identity import OboTokenProvider, SessionKeyProvider, UserAssertionValidator, assert_payload_agrees
from .runtime import enforce_telemetry_privacy

IDENTITY_FAILURE = "I could not confirm who you are, so I cannot look anything up on your behalf. Please sign in and try again."
CHOICE_FAILURE = "I could not read that choice. Please use the latest card, or type your question again."
PERMISSION_FAILURE = "I could not obtain permission to answer on your behalf. Please sign in and try again."
SERVICE_FAILURE = "A finance service could not be reached. Please try again."


def create_activity_host(settings: Settings, orchestrator: FinanceApplication, validator=None, *,
                         channel_service_client_factory=None, queue_capacity=64, queue_workers=4):
    """Create the real native host; injected client factories are for isolated transport tests."""
    if not os.environ.get("FOUNDRY_HOSTING_ENVIRONMENT") or not os.environ.get("FOUNDRY_AGENT_INSTANCE_CLIENT_ID"):
        raise RuntimeError("Native Activity hosting requires protected Foundry ingress and an agent instance identity.")
    enforce_telemetry_privacy()
    config = load_configuration_from_env(get_hosted_agent_env())
    connections = MsalConnectionManager(**config)
    storage = OAuthStorage()
    adapter = ActivityAdapter(channel_service_client_factory=(
        channel_service_client_factory or RestChannelServiceClientFactory(connections)))
    auth = DelegatedAuthorization(storage, connections, auth_handlers={
        "mcs": AuthHandler(name="mcs", auth_type="UserAuthorization",
                           abs_oauth_connection_name=settings.oauth_connection_name,
                           title="Sign in", text="Sign in to Zava Finance"),
    }, auto_sign_in=False, use_cache=False)
    app = ActivityApplication(storage=storage, adapter=adapter, authorization=auth,
                              start_typing_timer=False, long_running_messages=False, **config)
    adapter.on_turn_error = fail_safely
    app.error(fail_safely)

    async def dispatch(item):
        # Only outbound service claims are retained, never a bearer token or user identity.
        identity = ClaimsIdentity({"appid": item.service_app_id, "aud": item.service_app_id},
                                  authentication_type="Bearer")
        await adapter.process_activity(identity, item.activity, app.on_turn)

    continuations = ContinuationQueue(dispatch, queue_capacity, queue_workers)
    app.continuations = continuations
    _register_finance_handlers(app, settings, orchestrator, validator or UserAssertionValidator(settings))

    # A prebuilt app bypasses b3's hosted FoundryStorage default for all OAuth state.
    host = ActivityAgentServerHost(agent_app=app)
    host.add_middleware(RequestBoundary)
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


def _register_finance_handlers(app: ActivityApplication, settings: Settings,
                               finance: FinanceApplication, validator):
    keys = SessionKeyProvider(settings.session_key_salt)

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
                await send_text(context, IDENTITY_FAILURE)
            return
        if not activity.conversation or not (activity.conversation.id or "").strip():
            if invoke:
                await context.send_activity(invoke_response(activity.name, 400))
            else:
                await send_text(context, "A conversation identifier is required.")
            return
        try:
            selection = read_submission(activity)
            if invoke and selection is None:
                raise ProtocolError("A clarification selection is required.")
        except ProtocolError:
            if invoke:
                await context.send_activity(invoke_response(activity.name, 400))
            else:
                await send_text(context, CHOICE_FAILURE)
            return
        if invoke:
            try:
                app.continuations.enqueue(context, activity)
            except QueueUnavailable:
                await context.send_activity(invoke_response(activity.name, 503))
                return
            await context.send_activity(invoke_response(activity.name, 200))
            return
        question = SUBMISSION_QUESTION if selection else (activity.text or "").strip()
        if not question:
            await send_text(context, "Ask me what a KPI means, for a figure, or why something moved.")
            return
        try:
            async with OboTokenProvider(settings, assertion) as provider:
                reply = await finance.run_reply(keys.create(caller, activity.conversation.id),
                                                question, provider, selection)
        except ClientAuthenticationError:
            await send_text(context, PERMISSION_FAILURE)
            return
        except (aiohttp.ClientError, httpx.HTTPError, HttpResponseError, ServiceRequestError, TimeoutError):
            await send_text(context, SERVICE_FAILURE)
            return
        await send_message(context, create_message(reply))

    app.activity("message", auth_handlers=["mcs"])(handle)
    app.add_route(lambda context: is_supported_invoke(context.activity), handle,
                  is_invoke=True, auth_handlers=["mcs"])

    @app.on_sign_in_failure
    async def sign_in_failed(context, state, connection_id):
        if is_supported_invoke(context.activity):
            await context.send_activity(invoke_response(context.activity.name, 401))
        else:
            await send_text(context, IDENTITY_FAILURE)
