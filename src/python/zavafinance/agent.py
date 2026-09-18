"""MAF agent composition plus channel-independent conversation commands."""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass, field
from typing import Callable

from agent_framework import Agent, AgentSession, MiddlewareFailure, SupportsChatGetResponse
from agent_framework.openai import OpenAIChatOptions

from .agent_policy import FinanceHistory, SingleModelTurn, StopAfterTool
from .config import Settings
from .contracts import ClarificationSubmission, FinanceReply, SessionState, SessionStore, TokenProvider
from .finance.clarification import ClarificationSelection
from .finance.resolver import ResolverSearch
from .model import SYSTEM_INSTRUCTIONS, ToolSelectionError
from .tools import FinanceTools

logger = logging.getLogger(__name__)
_INVALID_SELECTION = ("I could not select a valid finance tool for that request. Please try asking "
                      "one KPI definition, statement, or finance analysis question at a time.")


@dataclass
class _TurnGate:
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    users: int = 0


class FinanceAgent:
    def __init__(self, settings: Settings, client: SupportsChatGetResponse[OpenAIChatOptions],
                 store: SessionStore, *, model_options: OpenAIChatOptions, query_factory: Callable,
                 search: ResolverSearch | None = None,
                 copilot_factory: Callable | None = None, data_agent_factory: Callable | None = None) -> None:
        if settings.max_history_messages < 3:
            raise ValueError("Native history must hold at least one complete turn.")
        self.settings, self.client, self.store = settings, client, store
        self.model_options = model_options.copy()
        self.query_factory, self.search = query_factory, search
        self.copilot_factory, self.data_agent_factory = copilot_factory, data_agent_factory
        self._turn_locks: dict[str, _TurnGate] = {}

    async def run_reply(self, session_key: str, question: str, token_provider: TokenProvider,
                        submission: ClarificationSubmission | None = None) -> FinanceReply:
        if not isinstance(session_key, str) or not session_key.strip():
            raise ValueError("A caller-bound session key is required.")
        if not isinstance(question, str):
            raise ValueError("Question must be text.")
        if submission is not None and not isinstance(submission, ClarificationSubmission):
            raise ValueError("Expected a validated clarification submission.")
        # There is no await between dictionary/reference-count updates on the event loop.
        gate = self._turn_locks.setdefault(session_key, _TurnGate())
        gate.users += 1
        entered = False
        try:
            await gate.lock.acquire()
            entered = True
            return await self._run_core(session_key, question, token_provider, submission)
        finally:
            if entered:
                gate.lock.release()
            gate.users -= 1
            if gate.users == 0:
                del self._turn_locks[session_key]

    async def _run_core(self, session_key: str, question: str, token_provider: TokenProvider,
                        submission: ClarificationSubmission | None) -> FinanceReply:
        async with asyncio.timeout(30):
            state = await self.store.load(session_key)
        reset = submission is None and ClarificationSelection.is_reset(question)
        if reset:
            state = SessionState()
        state.reply_clarification = None
        tools = FinanceTools(self.settings, state, session_key, token_provider,
                             query_factory=self.query_factory, search=self.search,
                             copilot_factory=self.copilot_factory, data_agent_factory=self.data_agent_factory)
        try:
            if reset:
                return FinanceReply("The conversation has been reset. What would you like to ask?")
            text_selection = False
            if submission is None:
                text_selection, submission = ClarificationSelection.try_read(question, state.pending_clarification)
            if submission is not None or text_selection:
                if state.pending_clarification is None:
                    return FinanceReply("There is no valid pending choice for that selection. Please ask the statement again.")
                if submission is None:
                    return FinanceReply("That selection does not uniquely identify a pending option. Please reply with its option number.")
                text = await (await tools.statement()).continue_(submission)
                return FinanceReply(text, state.reply_clarification)
            state.pending_clarification = None
            options = self.model_options.copy()
            options.update(store=False, tool_choice="auto", parallel_tool_calls=False)
            agent = Agent(
                client=self.client,
                name="ZavaFinance",
                instructions=SYSTEM_INSTRUCTIONS,
                tools=tools.functions(),
                default_options=options,
                context_providers=[FinanceHistory(state, self.settings.max_history_messages)],
                require_per_service_call_history_persistence=True,
                middleware=[SingleModelTurn(), StopAfterTool()],
            )
            async with agent:
                response = await agent.run(question, session=AgentSession(session_id=session_key))
            if tools.reply is None and any(
                content.type == "function_call" for message in response.messages for content in message.contents
            ):
                raise MiddlewareFailure("The selected finance tool did not produce a reply.")
            return tools.reply if tools.reply is not None else FinanceReply(response.text)
        except ToolSelectionError:
            logger.warning("Rejected invalid native finance selection.")
            return FinanceReply(_INVALID_SELECTION)
        finally:
            # Persist cancellation-safe, content-free history without retaining caller credentials.
            persistence = asyncio.create_task(self._persist(session_key, state))
            try:
                await asyncio.shield(persistence)
            except asyncio.CancelledError:
                await persistence
                raise

    async def _persist(self, session_key: str, state: SessionState) -> None:
        try:
            async with asyncio.timeout(10):
                await self.store.save(session_key, state)
        except Exception as exc:
            # Source parity: a failed state write must not discard an already completed answer.
            # Exception messages/bodies may contain permissioned content; log the type only.
            logger.error("Could not persist session state. ErrorType=%s", type(exc).__name__)
