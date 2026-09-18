"""Caller-bound Copilot Studio and Fabric Streamable HTTP integrations."""

from __future__ import annotations

import asyncio
import codecs
import json
import re
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Any
from urllib.parse import quote

import httpx

from .config import Settings
from .contracts import TokenProvider


KNOWLEDGE_BASE = "KPIpedia (Copilot Studio)"
DATA_AGENT = "Zava finance data agent (Microsoft Fabric)"
FABRIC_SCOPE = "https://api.fabric.microsoft.com/.default"
_MAX_BODY = 2 * 1024 * 1024
_MCP_PROTOCOL = "2025-06-18"


class DownstreamProtocolError(ValueError):
    """A downstream response violated its published protocol."""


class DownstreamHTTPError(RuntimeError):
    def __init__(self, status_code: int) -> None:
        self.status_code = status_code
        super().__init__(f"Downstream request failed with HTTP {status_code}.")


def source_footer(answer: str, source: str) -> str:
    if not answer.strip() or "_Source:" in answer:
        return answer
    return f"{answer.rstrip()}\n\n_Source: {source}._"


def kpi_question(kpi: str) -> str:
    value = kpi.strip()
    return value if " " in value and value.endswith("?") else f"What is {value}? Please explain how it is defined and calculated."


def _object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise DownstreamProtocolError("Duplicate downstream JSON member.")
        result[key] = value
    return result


def _json(value: str | bytes) -> dict[str, Any]:
    try:
        result = json.loads(value, object_pairs_hook=_object,
                            parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Nonfinite JSON.")))
    except (ValueError, UnicodeError) as exc:
        raise DownstreamProtocolError("Invalid downstream JSON.") from exc
    if not isinstance(result, dict):
        raise DownstreamProtocolError("Expected a downstream JSON object.")
    return result


def _bearer(token: str) -> str:
    if (not isinstance(token, str) or not token or len(token) > 131_072
            or any(not 0x21 <= ord(character) <= 0x7E for character in token)):
        raise DownstreamProtocolError("The downstream token provider returned an invalid token.")
    return f"Bearer {token}"


def _status(response: httpx.Response) -> None:
    if not 200 <= response.status_code < 300:
        raise DownstreamHTTPError(response.status_code)


async def _events(response: httpx.Response) -> AsyncIterator[tuple[str, str]]:
    """Decode bounded UTF-8 SSE records, including multi-line data and comments."""
    event, data, records = "", [], 0
    async for line in _lines(response):
        if not line:
            if data:
                records += 1
                if records > 2048:
                    raise DownstreamProtocolError("Downstream stream exceeded its event limit.")
                yield event or "message", "\n".join(data)
            event, data = "", []
        elif not line.startswith(":"):
            field, _, value = line.partition(":")
            value = value[1:] if value.startswith(" ") else value
            if field == "event":
                event = value
            elif field == "data":
                data.append(value)
    if data:
        raise DownstreamProtocolError("Downstream SSE ended before an event delimiter.")


async def _lines(response: httpx.Response) -> AsyncIterator[str]:
    decoder = codecs.getincrementaldecoder("utf-8-sig")(errors="strict")
    pending, size = "", 0
    try:
        async for chunk in response.aiter_bytes():
            size += len(chunk)
            if size > _MAX_BODY:
                raise DownstreamProtocolError("Downstream response exceeded its size limit.")
            pending += decoder.decode(chunk)
            while match := re.search(r"\r\n|\r|\n", pending):
                if match[0] == "\r" and match.end() == len(pending):
                    break
                yield pending[:match.start()]
                pending = pending[match.end():]
        pending += decoder.decode(b"", final=True)
    except UnicodeError:
        raise DownstreamProtocolError("Downstream stream is not valid UTF-8.") from None
    while match := re.search(r"\r\n|\r|\n", pending):
        yield pending[:match.start()]
        pending = pending[match.end():]
    if pending:
        yield pending


async def _body(response: httpx.Response) -> bytes:
    body = bytearray()
    async for chunk in response.aiter_bytes(chunk_size=16384):
        body.extend(chunk)
        if len(body) > _MAX_BODY:
            raise DownstreamProtocolError("Downstream response exceeded its size limit.")
    return bytes(body)


@asynccontextmanager
async def _http_client(supplied: httpx.AsyncClient | None) -> AsyncIterator[httpx.AsyncClient]:
    if supplied is not None:
        yield supplied
    else:
        async with httpx.AsyncClient(follow_redirects=False, timeout=httpx.Timeout(60, connect=10)) as client:
            yield client


class CopilotStudioClient:
    """Use official SDK settings/scope/URL helpers, with strict bounded HTTP parsing.

    The SDK's optional diagnostic body/header logging and experimental URL rebinding
    are deliberately not used. No credentials or conversation IDs are shared.
    """

    def __init__(self, settings: Settings, token_provider: TokenProvider, *,
                 http_client: httpx.AsyncClient | None = None) -> None:
        from microsoft_agents.copilotstudio.client import ConnectionSettings, CopilotClient
        from microsoft_agents.copilotstudio.client.power_platform_environment import PowerPlatformEnvironment

        if not re.fullmatch(r"[A-Za-z0-9_-]+", settings.copilot_environment_id):
            raise ValueError("A Copilot Studio environment identifier is required.")
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", settings.copilot_schema_name):
            raise ValueError("A Copilot Studio schema name is required.")
        self.settings = settings
        self._tokens = token_provider
        self._http = http_client
        connection = ConnectionSettings(environment_id=settings.copilot_environment_id,
                                        agent_identifier=settings.copilot_schema_name)
        self.scope = CopilotClient.scope_from_settings(connection)
        self._connection = connection
        self._url = PowerPlatformEnvironment.get_copilot_studio_connection_url

    async def _activities(self, payload: dict[str, Any], conversation_id: str | None = None) -> AsyncIterator[dict[str, Any]]:
        if conversation_id is not None and (not conversation_id.strip() or len(conversation_id) > 1024):
            raise DownstreamProtocolError("Invalid Copilot Studio conversation identifier.")
        url = self._url(settings=self._connection,
                        conversation_id=quote(conversation_id, safe="") if conversation_id else None)
        async with asyncio.timeout(self.settings.subagent_timeout_seconds):
            token = await self._tokens.get_token([self.scope])
            headers = {"Authorization": _bearer(token), "Accept": "text/event-stream",
                       "Content-Type": "application/json", "User-Agent": "ZavaFinance-One-Python/1.0"}
            async with _http_client(self._http) as client:
                async with client.stream("POST", url, json=payload, headers=headers,
                                         follow_redirects=False) as response:
                    _status(response)
                    if response.status_code != 200 or response.headers.get("content-type", "").split(";")[0].lower() != "text/event-stream":
                        raise DownstreamProtocolError("Expected a Copilot Studio activity stream.")
                    async for event, data in _events(response):
                        if event == "activity":
                            activity = _json(data)
                            if not isinstance(activity.get("type"), str):
                                raise DownstreamProtocolError("Copilot Studio activity has no type.")
                            if activity.get("type") == "event" and activity.get("name") == "error":
                                raise DownstreamProtocolError("Copilot Studio reported an error.")
                            yield activity
                        elif event == "error":
                            raise DownstreamProtocolError("Copilot Studio reported a stream error.")

    async def start_conversation(self) -> str:
        conversation_id = None
        async for activity in self._activities({"emitStartConversationEvent": True}):
            conversation = activity.get("conversation")
            if conversation is not None:
                if not isinstance(conversation, dict):
                    raise DownstreamProtocolError("Invalid Copilot Studio conversation.")
                value = conversation.get("id")
                if value is not None and (not isinstance(value, str) or not value.strip() or len(value) > 1024):
                    raise DownstreamProtocolError("Invalid Copilot Studio conversation identifier.")
                if value:
                    if conversation_id is not None and value != conversation_id:
                        raise DownstreamProtocolError("Copilot Studio changed conversation identity.")
                    conversation_id = value
        if not conversation_id:
            raise DownstreamProtocolError("Copilot Studio did not return a conversation id.")
        return conversation_id

    async def ask_question(self, question: str, conversation_id: str) -> str:
        parts = []
        payload = {"activity": {"type": "message", "text": question,
                                "conversation": {"id": conversation_id}}}
        async for activity in self._activities(payload, conversation_id):
            conversation = activity.get("conversation")
            if conversation is not None:
                if not isinstance(conversation, dict) or conversation.get("id", conversation_id) != conversation_id:
                    raise DownstreamProtocolError("Copilot Studio changed conversation identity.")
            if activity["type"] == "message":
                text = activity.get("text")
                if text is not None and not isinstance(text, str):
                    raise DownstreamProtocolError("Copilot Studio message text is invalid.")
                if text:
                    parts.append(text)
        return "\n".join(parts).strip()

    async def query(self, question: str, conversation_id: str | None = None) -> tuple[str, str]:
        async with asyncio.timeout(self.settings.subagent_timeout_seconds):
            conversation_id = conversation_id or await self.start_conversation()
            return await self.ask_question(question, conversation_id), conversation_id


class FabricDataAgentClient:
    """One fresh MCP 2025-06-18 session per caller/question; never a shared token."""

    def __init__(self, settings: Settings, token_provider: TokenProvider, *,
                 http_client: httpx.AsyncClient | None = None) -> None:
        self.settings = settings
        self._tokens = token_provider
        self._http = http_client
        self.endpoint = ("https://api.fabric.microsoft.com/v1/mcp/workspaces/"
                         f"{quote(settings.fabric_workspace_id, safe='')}/dataagents/"
                         f"{quote(settings.fabric_data_agent_id, safe='')}/agent")

    async def query(self, question: str) -> str:
        if not self.settings.fabric_workspace_id or not self.settings.fabric_data_agent_id:
            raise ValueError("Fabric data agent is not configured.")
        async with asyncio.timeout(self.settings.data_agent_timeout_seconds):
            token = await self._tokens.get_token([FABRIC_SCOPE])
            headers = {"Authorization": _bearer(token), "Accept": "application/json, text/event-stream",
                       "Content-Type": "application/json", "User-Agent": "ZavaFinance-One-Python/1.0"}
            async with _http_client(self._http) as client:
                try:
                    initialized = await self._rpc(client, headers, 1, "initialize", {
                        "protocolVersion": _MCP_PROTOCOL, "capabilities": {},
                        "clientInfo": {"name": "ZavaFinance", "version": "1.0"}})
                    if initialized.get("protocolVersion") != _MCP_PROTOCOL:
                        raise DownstreamProtocolError("Fabric negotiated an unsupported MCP protocol.")
                    headers["MCP-Protocol-Version"] = _MCP_PROTOCOL
                    await self._rpc(client, headers, None, "notifications/initialized")
                    tools = []
                    cursor = None
                    seen_cursors = set()
                    for _ in range(20):
                        listed = await self._rpc(client, headers, 2 + len(seen_cursors),
                                                 "tools/list", {"cursor": cursor} if cursor else {})
                        page = listed.get("tools")
                        if not isinstance(page, list):
                            raise DownstreamProtocolError("Fabric tool discovery has no tool list.")
                        tools.extend(page)
                        cursor = listed.get("nextCursor")
                        if cursor is None:
                            break
                        if not isinstance(cursor, str) or not cursor or cursor in seen_cursors:
                            raise DownstreamProtocolError("Fabric tool discovery returned an invalid cursor.")
                        seen_cursors.add(cursor)
                    else:
                        raise DownstreamProtocolError("Fabric tool discovery exceeded its page limit.")
                    if not tools:
                        raise DownstreamProtocolError("Fabric data agent exposes no MCP tool.")
                    tool = tools[0]
                    if not isinstance(tool, dict) or not isinstance(tool.get("name"), str) or not tool["name"]:
                        raise DownstreamProtocolError("Fabric data agent tool has no name.")
                    schema = tool.get("inputSchema", {})
                    if not isinstance(schema, dict) or not isinstance(schema.get("properties", {}), dict):
                        raise DownstreamProtocolError("Fabric tool schema is invalid.")
                    argument = next(iter(schema.get("properties", {})), "userQuestion")
                    parameters = {"name": tool["name"], "arguments": {argument: question}}
                    try:
                        result = await self._rpc(client, headers, 30, "tools/call", parameters)
                    except DownstreamHTTPError as exc:
                        if exc.status_code < 500:
                            raise
                        result = await self._rpc(client, headers, 31, "tools/call", parameters)
                    if result.get("isError") is True:
                        raise DownstreamProtocolError("Fabric data agent reported a tool error.")
                    if "isError" in result and not isinstance(result["isError"], bool):
                        raise DownstreamProtocolError("Fabric tool error flag is invalid.")
                    content = result.get("content")
                    if not isinstance(content, list):
                        raise DownstreamProtocolError("Fabric tool result has no content array.")
                    parts = []
                    for block in content:
                        if not isinstance(block, dict) or not isinstance(block.get("type"), str):
                            raise DownstreamProtocolError("Fabric returned invalid content.")
                        if block["type"] == "text":
                            if not isinstance(block.get("text"), str):
                                raise DownstreamProtocolError("Fabric returned invalid text content.")
                            parts.append(block["text"])
                    return "\n".join(parts)
                finally:
                    if "Mcp-Session-Id" in headers:
                        cleanup = asyncio.create_task(self._cleanup(client, headers))
                        try:
                            await asyncio.shield(cleanup)
                        except asyncio.CancelledError:
                            await cleanup
                            raise

    async def _cleanup(self, client: httpx.AsyncClient, headers: dict[str, str]) -> None:
        try:
            async with asyncio.timeout(5):
                async with client.stream("DELETE", self.endpoint, headers=headers, follow_redirects=False):
                    pass
        except (httpx.HTTPError, TimeoutError):
            # Closing an MCP session cannot discard an already completed finance answer.
            pass

    async def _rpc(self, client: httpx.AsyncClient, headers: dict[str, str],
                   request_id: int | None, method: str, parameters: dict[str, Any] | None = None) -> dict[str, Any]:
        message: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if request_id is not None:
            message["id"] = request_id
        if parameters is not None:
            message["params"] = parameters
        # The outer query deadline still bounds setup, retries and streamed progress.
        async with client.stream("POST", self.endpoint, headers=headers, json=message,
                                 follow_redirects=False,
                                 timeout=httpx.Timeout(60, connect=10,
                                                       read=self.settings.data_agent_timeout_seconds)) as response:
            _status(response)
            if method == "initialize":
                session = response.headers.get("Mcp-Session-Id")
                if session is not None:
                    if not session or len(session) > 4096 or any(not 0x21 <= ord(ch) <= 0x7E for ch in session):
                        raise DownstreamProtocolError("Fabric returned an invalid MCP session identifier.")
                    headers["Mcp-Session-Id"] = session
            if request_id is None:
                if response.status_code != 202:
                    raise DownstreamProtocolError("Fabric did not acknowledge the MCP notification.")
                return {}
            media = response.headers.get("content-type", "").split(";")[0].lower()
            if media == "application/json":
                return self._result(_json(await _body(response)), request_id)
            if media != "text/event-stream":
                raise DownstreamProtocolError("Fabric returned an unsupported MCP content type.")
            async for _, data in _events(response):
                value = _json(data)
                if "id" not in value and isinstance(value.get("method"), str) and value.get("jsonrpc") == "2.0":
                    continue
                return self._result(value, request_id)
            raise DownstreamProtocolError("Fabric stream ended without a matching MCP response.")

    @staticmethod
    def _result(value: dict[str, Any], request_id: int) -> dict[str, Any]:
        if value.get("jsonrpc") != "2.0" or type(value.get("id")) is not int or value["id"] != request_id:
            raise DownstreamProtocolError("Fabric MCP response does not match the request.")
        if "error" in value:
            raise DownstreamProtocolError("Fabric data agent returned an MCP error.")
        if not isinstance(value.get("result"), dict):
            raise DownstreamProtocolError("Fabric MCP result must be an object.")
        return value["result"]
