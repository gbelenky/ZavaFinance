"""Script only the model HTTP response; exercise the real MAF execution stack."""

import copy
import json
from unittest.mock import AsyncMock

import httpx
from agent_framework.openai import OpenAIChatClient
from openai import AsyncOpenAI

from zavafinance.model import FoundryModel, RouteDecision
from zavafinance.contracts import SessionState


class MemoryStore:
    def __init__(self):
        self.states = {}
        self.saves = []

    async def load(self, key):
        return copy.deepcopy(self.states.get(key, SessionState()))

    async def save(self, key, state):
        self.saves.append(key)
        self.states[key] = copy.deepcopy(state)


class ScriptedModel(FoundryModel):
    def __init__(self, settings):
        self.settings = settings
        self.route = AsyncMock(return_value=RouteDecision(text="Help"))
        self.requests = []
        self.output = None
        self._http = httpx.AsyncClient(transport=httpx.MockTransport(self._respond))
        self._openai = AsyncOpenAI(api_key="test-only", http_client=self._http, max_retries=0)
        self.client = OpenAIChatClient(model=settings.model_deployment, async_client=self._openai)

    async def _respond(self, request):
        body = json.loads(request.content)
        self.requests.append(body)
        history = []
        for item in body["input"]:
            if item.get("type") == "function_call":
                history.append({"role": "assistant", "name": item["name"], "call_id": item["call_id"],
                                "arguments": json.loads(item["arguments"])})
            elif item.get("type") == "function_call_output":
                history.append({"role": "tool", "call_id": item["call_id"], "content": item["output"]})
            elif item.get("role") in ("user", "assistant"):
                history.append({"role": item["role"],
                                "content": "".join(c["text"] for c in item["content"] if "text" in c)})
        question = history.pop()["content"]
        decision = await self.route(history, question)
        if self.output is not None:
            output = self.output
        elif decision.name:
            output = [{"type": "function_call", "id": "fc", "call_id": decision.call_id,
                       "name": decision.name, "arguments": json.dumps(decision.arguments), "status": "completed"}]
        else:
            output = [{"type": "message", "id": "msg", "role": "assistant", "status": "completed",
                       "content": [{"type": "output_text", "text": decision.text, "annotations": []}]}]
        return httpx.Response(200, json={"id": "resp", "object": "response", "created_at": 1,
                                       "status": "completed", "model": self.settings.model_deployment,
                                       "output": output})

    async def aclose(self):
        await self._openai.close()
