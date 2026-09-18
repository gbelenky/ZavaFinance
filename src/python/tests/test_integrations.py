import asyncio
import importlib.util
import json
import unittest
from dataclasses import replace

import httpx
from aiohttp import web

from zavafinance.config import Settings
from zavafinance.integrations import (
    FABRIC_SCOPE, CopilotStudioClient, DownstreamHTTPError, DownstreamProtocolError,
    FabricDataAgentClient, _bearer, _events, kpi_question, source_footer,
)


class Tokens:
    def __init__(self, token="fixture-user"):
        self.token, self.scopes = token, []

    async def get_token(self, scopes):
        self.scopes.append(scopes)
        return self.token


def sse(event, value):
    return f"event: {event}\ndata: {json.dumps(value)}\n\n"


class StreamingBytes(httpx.AsyncByteStream):
    def __init__(self, *chunks):
        self.chunks = chunks

    async def __aiter__(self):
        for chunk in self.chunks:
            yield chunk


class HelpersTests(unittest.IsolatedAsyncioTestCase):
    async def test_sse_multiline_comments_utf8_split_and_crlf(self):
        content = b'\xef\xbb\xbf: heartbeat\r\nevent: activity\r\ndata: {"text":\r\ndata: "caf\xc3\xa9"}\r\n\r\n'
        response = httpx.Response(200, stream=StreamingBytes(content[:76], content[76:]))
        events = [event async for event in _events(response)]
        self.assertEqual(events, [("activity", '{"text":\n"café"}')])

    async def test_sse_bounds_invalid_unicode_and_truncation(self):
        for payload in [b'data: {"text":"\xff"}\n\n', b"data: {}\n", b"data: " + b"x" * (2 * 1024 * 1024)]:
            with self.subTest(size=len(payload)), self.assertRaises(DownstreamProtocolError):
                async for _ in _events(httpx.Response(200, stream=StreamingBytes(payload))):
                    pass

    async def test_sse_delivers_record_without_waiting_for_stream_close(self):
        class OpenStream(httpx.AsyncByteStream):
            async def __aiter__(self):
                yield b"data: {}\n\n"
                await asyncio.Event().wait()
        iterator = _events(httpx.Response(200, stream=OpenStream()))
        async with asyncio.timeout(1):
            self.assertEqual(await anext(iterator), ("message", "{}"))
        await iterator.aclose()

    def test_question_and_footer_source_parity(self):
        self.assertEqual(kpi_question(" Margin "), "What is Margin? Please explain how it is defined and calculated.")
        self.assertEqual(kpi_question("How is margin defined?"), "How is margin defined?")
        self.assertEqual(kpi_question("Margin?"), "What is Margin?? Please explain how it is defined and calculated.")
        for answer in ["", " ", "text _Source: existing._"]:
            self.assertEqual(source_footer(answer, "test"), answer)
        self.assertEqual(source_footer("text  \n", "test"), "text\n\n_Source: test._")
        self.assertEqual(source_footer("_source: lowercase", "test"),
                         "_source: lowercase\n\n_Source: test._")

    def test_tokens_reject_header_injection(self):
        for token in ["", None, "bad token", "bad\r\nHeader:value", "bad\0", "nonasciié"]:
            with self.subTest(token=token), self.assertRaises(DownstreamProtocolError):
                _bearer(token)
        self.assertEqual(_bearer("fixture-user"), "Bearer fixture-user")


class FabricFixture:
    def __init__(self, *, stream=False, fail_method=None, failures=(), tool_error=False, empty=False):
        self.requests = []
        self.stream, self.fail_method = stream, fail_method
        self.failures, self.tool_error, self.empty = list(failures), tool_error, empty

    def __call__(self, request):
        body = json.loads(request.content) if request.method == "POST" else {}
        self.requests.append((request, body))
        if request.method == "DELETE":
            return httpx.Response(204)
        method = body["method"]
        if method == self.fail_method and self.failures:
            return httpx.Response(self.failures.pop(0), text="sensitive failure body")
        if method == "notifications/initialized":
            return httpx.Response(202)
        session_headers = {}
        if method == "initialize":
            result = {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}},
                      "serverInfo": {"name": "fixture", "version": "1.0"}}
            session_headers = {"Mcp-Session-Id": "fixture-session"}
        elif method == "tools/list":
            result = {"tools": [{"name": "Finance", "inputSchema": {
                "type": "object", "properties": {"actualQuestion": {"type": "string"}, "unused": {}}}}]}
        else:
            result = {"isError": self.tool_error, "content": [] if self.empty else [
                {"type": "text", "text": "Private amount"}, {"type": "image", "data": "ignored"},
                {"type": "text", "text": "verbatim  "}]}
        envelope = {"jsonrpc": "2.0", "id": body["id"], "result": result}
        if self.stream:
            progress = sse("message", {"jsonrpc": "2.0", "method": "notifications/progress", "params": {}})
            return httpx.Response(200, text=progress + sse("message", envelope),
                                  headers={**session_headers, "Content-Type": "text/event-stream"})
        return httpx.Response(200, json=envelope, headers=session_headers)


class FabricTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.settings = Settings(fabric_workspace_id="workspace", fabric_data_agent_id="agent")

    async def execute(self, fixture, tokens=None):
        async with httpx.AsyncClient(transport=httpx.MockTransport(fixture)) as http:
            return await FabricDataAgentClient(self.settings, tokens or Tokens(), http_client=http).query("Why?")

    async def test_full_json_and_sse_flow_uses_discovered_tool_and_first_property(self):
        for stream in [False, True]:
            fixture, tokens = FabricFixture(stream=stream), Tokens()
            self.assertEqual(await self.execute(fixture, tokens), "Private amount\nverbatim  ")
            self.assertEqual(tokens.scopes, [[FABRIC_SCOPE]])
            bodies = [body for request, body in fixture.requests if request.method == "POST"]
            self.assertEqual([b["method"] for b in bodies],
                             ["initialize", "notifications/initialized", "tools/list", "tools/call"])
            self.assertEqual(bodies[-1]["params"], {"name": "Finance", "arguments": {"actualQuestion": "Why?"}})
            self.assertEqual(fixture.requests[-1][0].method, "DELETE")
            for index, (request, _) in enumerate(fixture.requests):
                self.assertEqual(request.headers["Authorization"], "Bearer fixture-user")
                if index:
                    self.assertEqual(request.headers["Mcp-Session-Id"], "fixture-session")
                    self.assertEqual(request.headers["MCP-Protocol-Version"], "2025-06-18")

    async def test_rpc_read_timeout_matches_query_budget_without_mutating_client(self):
        for budget in (300, 90, 17):
            with self.subTest(budget=budget):
                fixture = FabricFixture()
                async with httpx.AsyncClient(transport=httpx.MockTransport(fixture), timeout=5) as http:
                    settings = replace(self.settings, data_agent_timeout_seconds=budget)
                    await FabricDataAgentClient(settings, Tokens(), http_client=http).query("Why?")
                    self.assertEqual(http.timeout, httpx.Timeout(5))
                    self.assertFalse(http.is_closed)
                for request, _ in fixture.requests:
                    if request.method == "POST":
                        self.assertEqual(request.extensions["timeout"],
                                         {"connect": 10, "read": budget, "write": 60, "pool": 60})

    async def _verify_delayed_tool(self, delay: float, read_timeout: float):
        fixture = FabricFixture()

        async def handle(request):
            content = await request.read()
            if request.method == "POST" and json.loads(content)["method"] == "tools/call":
                await asyncio.sleep(delay)
            reply = fixture(httpx.Request(request.method, "http://localhost/agent", content=content))
            return web.Response(status=reply.status_code, body=reply.content, headers=reply.headers)

        app = web.Application()
        app.router.add_route("*", "/agent", handle)
        runner = web.AppRunner(app)
        await runner.setup()
        try:
            await web.TCPSite(runner, "127.0.0.1", 0).start()
            port = runner.addresses[0][1]
            async with httpx.AsyncClient(
                timeout=httpx.Timeout(60, connect=10, read=read_timeout), trust_env=False,
            ) as http:
                client = FabricDataAgentClient(self.settings, Tokens(), http_client=http)
                client.endpoint = f"http://127.0.0.1:{port}/agent"
                self.assertEqual(await client.query("Why?"), "Private amount\nverbatim  ")
            self.assertEqual(fixture.requests[-1][0].method, "DELETE")
        finally:
            await runner.cleanup()

    async def test_slow_tool_response_outlasts_client_read_default(self):
        await self._verify_delayed_tool(delay=0.2, read_timeout=0.05)

    async def test_query_deadline_still_cancels_setup_and_tool_without_retry(self):
        for stage in ("initialize", "tools/list", "tools/call"):
            with self.subTest(stage=stage):
                fixture = FabricFixture()

                async def handle(request):
                    reply = fixture(request)
                    if request.method == "POST" and json.loads(request.content)["method"] == stage:
                        await asyncio.Event().wait()
                    return reply

                async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
                    settings = replace(self.settings, data_agent_timeout_seconds=1)
                    client = FabricDataAgentClient(settings, Tokens(), http_client=http)
                    async with asyncio.timeout(3):
                        with self.assertRaises(TimeoutError):
                            await client.query("Why?")
                self.assertEqual(len([b for _, b in fixture.requests if b.get("method") == stage]), 1)
                if stage != "initialize":
                    self.assertEqual(fixture.requests[-1][0].method, "DELETE")

    async def test_progress_does_not_extend_deadline_and_stream_is_closed(self):
        fixture = FabricFixture()
        closed = asyncio.Event()

        class ProgressStream(httpx.AsyncByteStream):
            async def __aiter__(self):
                while True:
                    yield sse("message", {"jsonrpc": "2.0", "method": "notifications/progress"}).encode()
                    await asyncio.sleep(0.05)

            async def aclose(self):
                closed.set()

        def handle(request):
            reply = fixture(request)
            if request.method == "POST" and json.loads(request.content)["method"] == "tools/call":
                return httpx.Response(200, stream=ProgressStream(),
                                      headers={"Content-Type": "text/event-stream"})
            return reply

        async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
            settings = replace(self.settings, data_agent_timeout_seconds=1)
            client = FabricDataAgentClient(settings, Tokens(), http_client=http)
            async with asyncio.timeout(3):
                with self.assertRaises(TimeoutError):
                    await client.query("Why?")
        self.assertTrue(closed.is_set())
        self.assertEqual(fixture.requests[-1][0].method, "DELETE")
        self.assertEqual(len([b for _, b in fixture.requests if b.get("method") == "tools/call"]), 1)

    async def test_only_tool_call_5xx_is_retried_once(self):
        fixture = FabricFixture(fail_method="tools/call", failures=[503])
        self.assertIn("Private amount", await self.execute(fixture))
        calls = [body for _, body in fixture.requests if body.get("method") == "tools/call"]
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[0]["params"], calls[1]["params"])
        self.assertNotEqual(calls[0]["id"], calls[1]["id"])
        fixture = FabricFixture(fail_method="tools/call", failures=[503, 502, 500])
        with self.assertRaises(DownstreamHTTPError) as caught:
            await self.execute(fixture)
        self.assertEqual(caught.exception.status_code, 502)
        self.assertEqual(len([b for _, b in fixture.requests if b.get("method") == "tools/call"]), 2)

    async def test_setup_and_authorization_throttling_are_not_retried(self):
        for method, status in [("initialize", 503), ("tools/list", 500), ("notifications/initialized", 503),
                               ("tools/call", 401), ("tools/call", 403), ("tools/call", 404), ("tools/call", 429)]:
            fixture = FabricFixture(fail_method=method, failures=[status, 200])
            with self.subTest(method=method, status=status), self.assertRaises(DownstreamHTTPError) as caught:
                await self.execute(fixture)
            self.assertEqual(caught.exception.status_code, status)
            self.assertNotIn("sensitive", str(caught.exception))
            self.assertEqual(len([b for _, b in fixture.requests if b.get("method") == method]), 1)

    async def test_tool_error_never_returns_error_body_as_answer(self):
        fixture = FabricFixture(tool_error=True)
        with self.assertRaises(DownstreamProtocolError):
            await self.execute(fixture)
        self.assertEqual(fixture.requests[-1][0].method, "DELETE")
        self.assertEqual(await self.execute(FabricFixture(empty=True)), "")

    async def test_bad_jsonrpc_ids_types_protocols_and_ambiguous_payloads_fail(self):
        for reply in [{"jsonrpc": "2.0", "id": 99, "result": {}},
                      {"jsonrpc": "2.0", "id": True, "result": {}},
                      {"jsonrpc": "2.0", "id": 1, "result": {"protocolVersion": "wrong"}},
                      {"jsonrpc": "2.0", "id": 1, "result": {}, "error": {"message": "private"}},
                      {"jsonrpc": "2.0", "id": 1, "result": []}]:
            with self.subTest(reply=reply), self.assertRaises(DownstreamProtocolError):
                await self.execute(lambda request: httpx.Response(200, json=reply))

    async def test_two_callers_have_fresh_sessions_and_no_shared_token_headers(self):
        fixture = FabricFixture()
        async with httpx.AsyncClient(transport=httpx.MockTransport(fixture)) as http:
            one = FabricDataAgentClient(self.settings, Tokens("user-one"), http_client=http)
            two = FabricDataAgentClient(self.settings, Tokens("user-two"), http_client=http)
            await asyncio.gather(one.query("One?"), two.query("Two?"))
        requests = [r for r, b in fixture.requests if b.get("method") == "initialize"]
        self.assertEqual({r.headers["Authorization"] for r in requests}, {"Bearer user-one", "Bearer user-two"})
        self.assertTrue(all("Mcp-Session-Id" not in r.headers for r in requests))
        self.assertEqual(len([r for r, _ in fixture.requests if r.method == "DELETE"]), 2)

    async def test_cancelled_tool_still_closes_session(self):
        started = asyncio.Event()
        fixture = FabricFixture()
        async def handle(request):
            if request.method == "POST" and json.loads(request.content)["method"] == "tools/call":
                started.set()
                await asyncio.Event().wait()
            return fixture(request)
        task = asyncio.create_task(self.execute(handle))
        await started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(fixture.requests[-1][0].method, "DELETE")

    async def test_cleanup_error_preserves_completed_answer_and_redirects_are_not_followed(self):
        fixture = FabricFixture()
        def cleanup_failure(request):
            if request.method == "DELETE":
                raise httpx.ConnectError("private diagnostic")
            return fixture(request)
        self.assertIn("Private amount", await self.execute(cleanup_failure))
        requests = []
        def redirect(request):
            requests.append(request)
            return httpx.Response(307, headers={"Location": "https://other.example"})
        with self.assertRaises(DownstreamHTTPError):
            await self.execute(redirect)
        self.assertEqual(len(requests), 1)


@unittest.skipUnless(importlib.util.find_spec("microsoft_agents.copilotstudio"),
                     "Parent must install published Copilot Studio client.")
class CopilotStudioTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.settings = Settings(copilot_environment_id="00000000-0000-0000-0000-000000000000",
                                 copilot_schema_name="test_agent")

    async def test_official_scope_url_and_startup_id_without_message(self):
        requests, tokens = [], Tokens()
        def handle(request):
            body = json.loads(request.content)
            requests.append((request, body))
            if "emitStartConversationEvent" in body:
                content = sse("activity", {"type": "typing", "conversation": {"id": "conversation"}})
                content += sse("activity", {"type": "message", "text": "Startup text must be ignored"})
            else:
                content = sse("activity", {"type": "message", "text": "First", "conversation": {"id": "conversation"}})
                content += sse("activity", {"type": "typing"})
                content += sse("activity", {"type": "message", "text": "Second"})
            return httpx.Response(200, text=content, headers={"Content-Type": "text/event-stream"})
        async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
            client = CopilotStudioClient(self.settings, tokens, http_client=http)
            answer, conversation = await client.query("What is Margin?")
            self.assertEqual(answer, "First\nSecond")
            self.assertEqual(conversation, "conversation")
            self.assertEqual(tokens.scopes, [[client.scope], [client.scope]])
            self.assertTrue(client.scope.endswith("/.default"))
        self.assertEqual(requests[0][1], {"emitStartConversationEvent": True})
        self.assertEqual(requests[1][1], {"activity": {"type": "message", "text": "What is Margin?",
                                                     "conversation": {"id": "conversation"}}})
        self.assertIn("/conversations/conversation", str(requests[1][0].url))
        self.assertEqual(len(requests), 2)

    async def test_reuse_id_does_not_restart(self):
        requests = []
        def handle(request):
            requests.append(json.loads(request.content))
            return httpx.Response(200, text=sse("activity", {"type": "message", "text": "Answer"}),
                                  headers={"Content-Type": "text/event-stream"})
        async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
            client = CopilotStudioClient(self.settings, Tokens(), http_client=http)
            self.assertEqual(await client.query("Current question only", "existing"), ("Answer", "existing"))
        self.assertEqual(len(requests), 1)
        self.assertEqual(requests[0]["activity"]["text"], "Current question only")

    async def test_invalid_or_conflicting_startup_id_is_error(self):
        for activities in [[{"type": "message", "text": "No ID"}],
                           [{"type": "typing", "conversation": {"id": "a"}},
                            {"type": "typing", "conversation": {"id": "b"}}],
                           [{"type": "typing", "conversation": {"id": 123}}]]:
            def handle(request):
                return httpx.Response(200, text="".join(sse("activity", a) for a in activities),
                                      headers={"Content-Type": "text/event-stream"})
            async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
                with self.assertRaises(DownstreamProtocolError):
                    await CopilotStudioClient(self.settings, Tokens(), http_client=http).start_conversation()

    async def test_non_sse_error_events_statuses_and_empty_answers(self):
        replies = [httpx.Response(200, json={}), httpx.Response(429, text="private"),
                   httpx.Response(200, text=sse("error", {"message": "private"}),
                                  headers={"Content-Type": "text/event-stream"}),
                   httpx.Response(200, text=sse("activity", {"type": "message", "text": 12}),
                                  headers={"Content-Type": "text/event-stream"})]
        for reply in replies:
            requests = []
            def handle(request):
                requests.append(request)
                return reply
            async with httpx.AsyncClient(transport=httpx.MockTransport(handle)) as http:
                with self.assertRaises((DownstreamProtocolError, DownstreamHTTPError)):
                    await CopilotStudioClient(self.settings, Tokens(), http_client=http).ask_question("Q", "id")
            self.assertEqual(len(requests), 1)
