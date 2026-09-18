"""One host-controlled native selection per caller-isolated turn."""

from __future__ import annotations

import asyncio
import copy
import inspect
import logging
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any

import httpx
from azure.core.exceptions import AzureError

from .config import Settings
from .contracts import ClarificationSubmission, FinanceReply, SessionState, TokenProvider
from .finance.clarification import ClarificationSelection
from .finance.statement import StatementTool
from .integrations import (
    DATA_AGENT, KNOWLEDGE_BASE, CopilotStudioClient, DownstreamHTTPError, DownstreamProtocolError,
    FabricDataAgentClient, kpi_question, source_footer,
)
from .model import ToolSelectionError, decision_messages, trim_history, validate_decision

logger = logging.getLogger(__name__)
_DOWNSTREAM_ERRORS = (DownstreamHTTPError, DownstreamProtocolError, httpx.HTTPError, AzureError)
_INVALID_SELECTION = ("I could not select a valid finance tool for that request. Please try asking "
                      "one KPI definition, statement, or finance analysis question at a time.")


@dataclass
class _TurnGate:
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    users: int = 0


class Orchestrator:
    def __init__(self, settings: Settings, model, store, *, query_factory, search=None,
                 copilot_factory=None, data_agent_factory=None) -> None:
        if settings.max_history_messages < 3:
            raise ValueError("Native history must hold at least one complete turn.")
        self.settings, self.model, self.store = settings, model, store
        self.query_factory, self.search = query_factory, search
        self.copilot_factory = copilot_factory or (lambda token_provider: CopilotStudioClient(settings, token_provider))
        self.data_agent_factory = data_agent_factory or (lambda token_provider: FabricDataAgentClient(settings, token_provider))
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

    async def _run_core(self, session_key, question, token_provider, submission) -> FinanceReply:
        async with asyncio.timeout(30):
            state = await self.store.load(session_key)
        reset = submission is None and ClarificationSelection.is_reset(question)
        if reset:
            state = SessionState()
        state.reply_clarification = None
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
                text = await (await self._statement(token_provider, state, session_key)).continue_(submission)
                return FinanceReply(text, state.reply_clarification)
            state.pending_clarification = None
            state.history = trim_history(state.history, self.settings.max_history_messages - 1)
            model_history = copy.deepcopy(state.history)
            state.history.append({"role": "user", "content": question})
            try:
                decision = validate_decision(await self.model.route(model_history, question))
                state.history.extend(decision_messages(decision))
            finally:
                state.history = trim_history(state.history, self.settings.max_history_messages)
            # Honor caller cancellation even when an injected/client adapter completed synchronously.
            await asyncio.sleep(0)
            if decision.name is None:
                return FinanceReply(decision.text)
            if decision.name == "get_statement":
                tool = await self._statement(token_provider, state, session_key)
                text = await tool.execute(kpi=decision.arguments.get("kpi"),
                                          org=decision.arguments.get("org", ""),
                                          date_range=decision.arguments.get("dateRange", ""))
            elif decision.name == "get_kpi_info":
                text = await self._kpi(decision.arguments["kpi"], token_provider, state)
            else:
                text = await self._explore(decision.arguments["question"], token_provider)
            if not isinstance(text, str):
                raise TypeError("A finance tool must return text.")
            return FinanceReply(text, state.reply_clarification)
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

    async def _statement(self, token_provider: TokenProvider, state: SessionState, session_key: str) -> StatementTool:
        query = self.query_factory(token_provider)
        if inspect.isawaitable(query):
            query = await query
        return StatementTool(query, state, datetime.now(timezone.utc).date(), search=self.search,
                             session_key=session_key, options=self.settings)

    async def _kpi(self, kpi: str, token_provider: TokenProvider, state: SessionState) -> str:
        if not kpi.strip():
            return "I need a KPI name to look up."
        if not self.settings.copilot_environment_id or not self.settings.copilot_schema_name:
            return f"I could not reach KPIpedia to look up '{kpi}'."
        try:
            async with asyncio.timeout(self.settings.subagent_timeout_seconds):
                client = self.copilot_factory(token_provider)
                if not state.copilot_conversation_id:
                    state.copilot_conversation_id = await client.start_conversation()
                if not state.copilot_conversation_id:
                    raise DownstreamProtocolError("Copilot Studio did not return a conversation id.")
                answer = await client.ask_question(kpi_question(kpi), state.copilot_conversation_id)
            if not isinstance(answer, str):
                raise DownstreamProtocolError("KPIpedia returned invalid text.")
            if not answer.strip():
                return f"KPIpedia returned no description for '{kpi}'."
            state.last_kpi_name = kpi
            return source_footer(answer, KNOWLEDGE_BASE)
        except (TimeoutError, httpx.TimeoutException):
            return f"KPIpedia did not respond in time for '{kpi}'. Please try again."
        except _DOWNSTREAM_ERRORS as exc:
            logger.warning("KPIpedia request failed. ErrorType=%s", type(exc).__name__)
            return f"I could not reach KPIpedia to look up '{kpi}'."

    async def _explore(self, question: str, token_provider: TokenProvider) -> str:
        if not question.strip():
            return "What would you like me to analyse?"
        if not self.settings.fabric_workspace_id or not self.settings.fabric_data_agent_id:
            return "Open-ended finance analysis is not configured in this environment."
        try:
            async with asyncio.timeout(self.settings.data_agent_timeout_seconds):
                answer = await self.data_agent_factory(token_provider).query(question)
            if not isinstance(answer, str):
                raise DownstreamProtocolError("Fabric returned invalid text.")
            return (source_footer(answer, DATA_AGENT) if answer.strip()
                    else "The finance data agent did not return an answer for that question.")
        except (TimeoutError, httpx.TimeoutException):
            return ("The finance data agent did not respond in time. Please try again, or ask for "
                    "a specific KPI, organization and period instead.")
        except DownstreamHTTPError as exc:
            if exc.status_code == 429:
                return ("The finance data agent is busy — the Fabric capacity backing it has hit its "
                        "compute limit. Please try again in a few minutes. For a specific figure, ask "
                        "for a KPI, organization and period instead, which uses a lighter query.")
            return "I could not reach the finance data agent for that analysis."
        except _DOWNSTREAM_ERRORS as exc:
            logger.warning("Fabric request failed. ErrorType=%s", type(exc).__name__)
            return "I could not reach the finance data agent for that analysis."
