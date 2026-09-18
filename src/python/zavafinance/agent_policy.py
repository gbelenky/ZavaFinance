"""MAF extension points for single-tool replies and schema-v1 history privacy."""

import asyncio
from collections.abc import Awaitable, Callable, Sequence
from typing import Any

from agent_framework import (
    AgentSession, ChatContext, ChatMiddleware, ChatResponse, FunctionInvocationContext,
    FunctionMiddleware, HistoryProvider, Message, MiddlewareFailure, MiddlewareTermination,
    SessionContext, SupportsAgentRun,
)

from .contracts import SessionState
from .model import decision_messages, history_messages, read_selection, trim_history


class SingleModelTurn(ChatMiddleware):
    def __init__(self) -> None:
        self._requested = False

    async def process(self, context: ChatContext, call_next: Callable[[], Awaitable[None]]) -> None:
        if context.stream or self._requested:
            raise MiddlewareFailure("A finance turn permits only one non-streaming model request.")
        self._requested = True
        async with asyncio.timeout(60):
            await call_next()


class StopAfterTool(FunctionMiddleware):
    async def process(self, context: FunctionInvocationContext,
                      call_next: Callable[[], Awaitable[None]]) -> None:
        await asyncio.sleep(0)
        await call_next()
        raise MiddlewareTermination(result=context.result)


class FinanceHistory(HistoryProvider):
    """Translate existing storage through MAF's per-service-call history lifecycle.

    Recording the validated intent before invocation keeps cancelled turns paired.
    Tool outputs and incidental model prose never enter the persisted history.
    """

    def __init__(self, finance_state: SessionState, limit: int) -> None:
        super().__init__("finance_history", store_inputs=False)
        self.finance_state, self.limit = finance_state, limit

    async def get_messages(self, session_id: str | None, *, state: dict[str, Any] | None = None,
                           **kwargs: Any) -> list[Message]:
        self.finance_state.history = trim_history(self.finance_state.history, self.limit - 1)
        return history_messages(self.finance_state.history)

    async def before_run(self, *, agent: SupportsAgentRun, session: AgentSession,
                         context: SessionContext, state: dict[str, Any]) -> None:
        await super().before_run(agent=agent, session=session, context=context, state=state)
        self.finance_state.history.extend(
            {"role": "user", "content": message.text} for message in context.input_messages)

    async def save_messages(self, session_id: str | None, messages: Sequence[Message], *,
                            state: dict[str, Any] | None = None, **kwargs: Any) -> None:
        decision = read_selection(ChatResponse(messages=list(messages)))
        self.finance_state.history.extend(decision_messages(decision))
        self.finance_state.history = trim_history(self.finance_state.history, self.limit)
