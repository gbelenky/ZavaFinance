"""Typed Core state items, isolated by caller/conversation HMAC keys."""

from __future__ import annotations

import asyncio
import copy
import re
from typing import Any

from azure.ai.agentserver.core.storage import FoundryStateStore
from azure.core.credentials_async import AsyncTokenCredential
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from .config import Settings
from .contracts import SessionState
from .model import RouteDecision, ToolSelectionError, WITHHELD_RESULT, validate_decision


class StatePayloadError(ValueError):
    """An invalid persisted item must fail explicitly, never silently reset."""


class _StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class _StatementArguments(_StrictModel):
    kpi: str | None
    org: str
    date_range: str
    as_of_date: str
    selected_kpi_id: str | None = None
    selected_organization_id: str | None = None


class _Release(_StrictModel):
    catalog_version: str
    search_index: str
    embedding_deployment: str
    embedding_dimensions: int


class _Pending(_StrictModel):
    request_id: str
    field: str
    catalog_version: str
    arguments: _StatementArguments
    candidate_ids: list[str]
    expires_at_utc: str
    owner_session_key: str
    release: _Release | None = None
    candidate_label_hashes: list[str] | None = None


class _SessionItem(_StrictModel):
    schema_version: int
    history: list[dict[str, Any]] = Field(default_factory=list)
    copilot_conversation_id: str | None = None
    last_kpi_name: str | None = None
    pending_clarification: _Pending | None = None


def _history(value: list[dict[str, Any]]) -> None:
    awaiting = None
    for index, message in enumerate(value):
        role = message.get("role")
        if awaiting is not None:
            if (set(message) != {"role", "call_id", "content"} or role != "tool"
                    or message["call_id"] != awaiting or message["content"] != WITHHELD_RESULT):
                raise StatePayloadError("Stored native call has no content-free result marker.")
            awaiting = None
            continue
        if role == "assistant" and "name" in message:
            if set(message) != {"role", "name", "call_id", "arguments"} or index == 0 or value[index - 1]["role"] != "user":
                raise StatePayloadError("Stored native call is not bound to a user turn.")
            try:
                validate_decision(RouteDecision(message["name"], message["arguments"], message["call_id"]))
            except ToolSelectionError as exc:
                raise StatePayloadError("Invalid stored native call.") from exc
            awaiting = message["call_id"]
        elif (role not in ("user", "assistant") or set(message) != {"role", "content"}
              or not isinstance(message["content"], str)
              or (role == "assistant" and (index == 0 or value[index - 1]["role"] != "user"))):
            raise StatePayloadError("Invalid stored message.")
    if awaiting is not None:
        raise StatePayloadError("Stored native call is unpaired.")


def serialize_state(state: SessionState) -> dict[str, Any]:
    if not isinstance(state, SessionState):
        raise StatePayloadError("Expected an agent session.")
    data = {"schema_version": 1, "history": state.history,
            "copilot_conversation_id": state.copilot_conversation_id,
            "last_kpi_name": state.last_kpi_name, "pending_clarification": state.pending_clarification}
    return _read_item(data).model_dump(mode="json")


def _read_item(data: Any) -> _SessionItem:
    try:
        item = _SessionItem.model_validate(data)
    except ValidationError:
        # Pydantic errors include input values; do not expose or log their representation.
        raise StatePayloadError("Invalid agent session payload.") from None
    if item.schema_version != 1:
        raise StatePayloadError("Unsupported agent session schema.")
    _history(item.history)
    return item


def deserialize_state(data: Any) -> SessionState:
    item = _read_item(data)
    return SessionState(history=copy.deepcopy(item.history),
                        copilot_conversation_id=item.copilot_conversation_id,
                        last_kpi_name=item.last_kpi_name,
                        pending_clarification=item.pending_clarification.model_dump(mode="json")
                        if item.pending_clarification else None)


class FoundrySessionStore:
    def __init__(self, settings: Settings, credential: AsyncTokenCredential) -> None:
        if not settings.session_key_salt.strip():
            raise ValueError("Caller-bound HMAC session keys require a Python session salt.")
        if settings.session_store_name != "zavafinance-one-python-sessions":
            raise ValueError("Python sessions require their separate state store.")
        if type(settings.session_ttl_seconds) is not int or settings.session_ttl_seconds != 30 * 86400:
            raise ValueError("Python sessions require a 30-day state TTL.")
        self.settings = settings
        self._credential = credential
        self._gate = asyncio.Lock()
        self._store = None

    @staticmethod
    def key_for(key: str) -> str:
        if not isinstance(key, str) or re.fullmatch(r"[A-Z2-7]{52}", key) is None:
            raise ValueError("A caller-bound HMAC session key is required.")
        return f"orchestrator_{key}"

    async def _get_store(self):
        if self._store is None:
            async with self._gate:
                if self._store is None:
                    self._store = await FoundryStateStore.get_or_create(
                        self.settings.session_store_name, credential=self._credential,
                        endpoint=self.settings.project_endpoint, user_isolation=False,
                        item_ttl_seconds=self.settings.session_ttl_seconds,
                        description="Zava Finance One Python caller-bound session state.")
        return self._store

    async def load(self, key: str) -> SessionState:
        storage_key = self.key_for(key)
        store = await self._get_store()
        item = await store.get_item(storage_key)
        if item is None:
            return SessionState()
        if not isinstance(item.value, dict) or set(item.value) != {"item"}:
            raise StatePayloadError("The stored agent session has no typed item field.")
        return deserialize_state(item.value["item"])

    async def save(self, key: str, state: SessionState) -> None:
        storage_key = self.key_for(key)
        data = serialize_state(state)
        store = await self._get_store()
        await store.set_item(storage_key, {"item": data}, tags=None, if_match=None, require_exists=False)

    async def aclose(self) -> None:
        if self._store is not None:
            await self._store.aclose()
