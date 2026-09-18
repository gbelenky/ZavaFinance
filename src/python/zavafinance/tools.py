"""Executable MAF tools; permissioned replies stay outside model messages."""

import asyncio
import inspect
import logging
from collections.abc import Awaitable
from datetime import datetime, timezone
from typing import Callable

import httpx
from agent_framework import FunctionTool, MiddlewareFailure, tool
from azure.core.exceptions import AzureError

from .config import Settings
from .contracts import FinanceReply, SessionState, TokenProvider
from .finance.resolver import ResolverSearch
from .finance.statement import StatementTool
from .integrations import (
    DATA_AGENT, KNOWLEDGE_BASE, CopilotStudioClient, DownstreamHTTPError, DownstreamProtocolError,
    FabricDataAgentClient, kpi_question, source_footer,
)
from .model import WITHHELD_RESULT
from .tool_contracts import TOOL_INPUTS

logger = logging.getLogger(__name__)
_DOWNSTREAM_ERRORS = (DownstreamHTTPError, DownstreamProtocolError, httpx.HTTPError, AzureError)


class FinanceTools:
    def __init__(self, settings: Settings, state: SessionState, session_key: str,
                 tokens: TokenProvider, *, query_factory: Callable, search: ResolverSearch | None = None,
                 copilot_factory: Callable | None = None,
                 data_agent_factory: Callable | None = None) -> None:
        self.settings, self.state, self.session_key, self.tokens = settings, state, session_key, tokens
        self.query_factory, self.search = query_factory, search
        self.copilot_factory = copilot_factory or (lambda tokens: CopilotStudioClient(settings, tokens))
        self.data_agent_factory = data_agent_factory or (lambda tokens: FabricDataAgentClient(settings, tokens))
        self.reply: FinanceReply | None = None
        self.today = datetime.now(timezone.utc).date()

    def functions(self) -> list[FunctionTool]:
        functions = []
        for name, inputs in TOOL_INPUTS.items():
            description = inputs.__doc__
            if name == "get_statement":
                description = (f"{description}\n\nUTC reference date: {self.today.isoformat()}.\n"
                               f"Current calendar year: {self.today.year}.")
            functions.append(tool(getattr(self, name), name=name, description=description, schema=inputs))
        return functions

    async def _complete(self, operation: Awaitable[str]) -> str:
        try:
            text = await operation
        except (RuntimeError, ValueError, TypeError, OSError) as exc:
            # The SDK records tool exception messages even with content capture disabled.
            # Fail closed before that boundary, without a permissioned exception body.
            raise MiddlewareFailure(f"Finance tool failed. ErrorType={type(exc).__name__}") from None
        if not isinstance(text, str):
            raise MiddlewareFailure("A finance tool must return text.")
        self.reply = FinanceReply(text, self.state.reply_clarification)
        # Even MAF tool-result telemetry sees only the marker, not the private reply.
        return WITHHELD_RESULT

    async def statement(self) -> StatementTool:
        query = self.query_factory(self.tokens)
        if inspect.isawaitable(query):
            query = await query
        return StatementTool(query, self.state, self.today, search=self.search,
                             session_key=self.session_key, options=self.settings)

    async def get_statement(self, kpi: str | None = None, org: str = "", dateRange: str = "") -> str:
        return await self._complete(self._statement(kpi, org, dateRange))

    async def get_kpi_info(self, kpi: str) -> str:
        return await self._complete(self._definition(kpi))

    async def explore_finance(self, question: str) -> str:
        return await self._complete(self._analysis(question))

    async def _statement(self, kpi: str | None, org: str, date_range: str) -> str:
        return await (await self.statement()).execute(kpi=kpi, org=org, date_range=date_range)

    async def _definition(self, kpi: str) -> str:
        if not kpi.strip():
            return "I need a KPI name to look up."
        if not self.settings.copilot_environment_id or not self.settings.copilot_schema_name:
            return f"I could not reach KPIpedia to look up '{kpi}'."
        try:
            async with asyncio.timeout(self.settings.subagent_timeout_seconds):
                client = self.copilot_factory(self.tokens)
                if not self.state.copilot_conversation_id:
                    self.state.copilot_conversation_id = await client.start_conversation()
                if not self.state.copilot_conversation_id:
                    raise DownstreamProtocolError("Copilot Studio did not return a conversation id.")
                answer = await client.ask_question(kpi_question(kpi), self.state.copilot_conversation_id)
            if not isinstance(answer, str):
                raise DownstreamProtocolError("KPIpedia returned invalid text.")
            if not answer.strip():
                return f"KPIpedia returned no description for '{kpi}'."
            self.state.last_kpi_name = kpi
            return source_footer(answer, KNOWLEDGE_BASE)
        except (TimeoutError, httpx.TimeoutException):
            return f"KPIpedia did not respond in time for '{kpi}'. Please try again."
        except _DOWNSTREAM_ERRORS as exc:
            logger.warning("KPIpedia request failed. ErrorType=%s", type(exc).__name__)
            return f"I could not reach KPIpedia to look up '{kpi}'."

    async def _analysis(self, question: str) -> str:
        if not question.strip():
            return "What would you like me to analyse?"
        if not self.settings.fabric_workspace_id or not self.settings.fabric_data_agent_id:
            return "Open-ended finance analysis is not configured in this environment."
        try:
            async with asyncio.timeout(self.settings.data_agent_timeout_seconds):
                answer = await self.data_agent_factory(self.tokens).query(question)
            if not isinstance(answer, str):
                raise DownstreamProtocolError("Fabric returned invalid text.")
            return (source_footer(answer, DATA_AGENT) if answer.strip()
                    else "The finance data agent did not return an answer for that question.")
        except (TimeoutError, httpx.TimeoutException):
            return ("The finance data agent did not respond in time. Please try again, or ask for "
                    "a specific KPI, organization and period instead.")
        except DownstreamHTTPError as exc:
            if exc.status_code == 429:
                return ("The finance data agent is busy \u2014 the Fabric capacity backing it has hit its "
                        "compute limit. Please try again in a few minutes. For a specific figure, ask "
                        "for a KPI, organization and period instead, which uses a lighter query.")
            return "I could not reach the finance data agent for that analysis."
        except _DOWNSTREAM_ERRORS as exc:
            logger.warning("Fabric request failed. ErrorType=%s", type(exc).__name__)
            return "I could not reach the finance data agent for that analysis."
