import asyncio
import json
import unittest
from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import patch

import httpx
from azure.core.exceptions import ClientAuthenticationError

from zavafinance.config import Settings
from zavafinance.finance import AzureResolverSearch, ResolverEntity, ResolverRelease

RELEASE = ResolverRelease("v1", "resolver-v1", "embedding-model", 2)
CANDIDATES = (ResolverEntity("v1", "candidate-one", "kpi", "Operating income", kpi_code="KPI-011"),)
SETTINGS = Settings(search_endpoint="https://search.example.test", embedding_endpoint="https://model.example.test")


class Credential:
    def __init__(self):
        self.scopes = []
        self.failure = None

    async def get_token(self, scope):
        self.scopes.append(scope)
        if self.failure:
            raise self.failure
        return SimpleNamespace(token="runtime-identity-not-a-caller-token")


class SearchTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.requests, self.credential = [], Credential()
        self.responses = [
            {"data": [{"embedding": [0.1, 0.2]}]},
            {"value": [{"entity_id": "candidate-one", "catalog_version": "v1", "definition": "privileged text"}]},
        ]
        self.status = 200
        self.failure = None
        self.delay = 0
        self.async_client = httpx.AsyncClient

    async def handler(self, request):
        self.requests.append(request)
        if self.delay:
            await asyncio.sleep(self.delay)
        if self.failure:
            raise self.failure
        return httpx.Response(self.status, json=self.responses.pop(0))

    def client(self, **kwargs):
        return self.async_client(transport=httpx.MockTransport(self.handler), **kwargs)

    async def search(self, field="kpi", settings=SETTINGS):
        with patch("httpx.AsyncClient", side_effect=self.client):
            return await AzureResolverSearch(settings, self.credential).search("  net ernings ", field, RELEASE)

    async def test_hybrid_search_uses_runtime_tokens_full_binding_and_returns_only_ids(self):
        result = await self.search()
        self.assertEqual("candidate-one", result[0].id)
        self.assertFalse(hasattr(result[0], "definition"))
        self.assertEqual(["https://cognitiveservices.azure.com/.default", "https://search.azure.com/.default"], self.credential.scopes)
        embedding, search = [json.loads(request.content) for request in self.requests]
        for request in self.requests:
            self.assertEqual("Bearer runtime-identity-not-a-caller-token", request.headers["Authorization"])
        self.assertEqual("2024-10-21", self.requests[0].url.params["api-version"])
        self.assertEqual("2024-07-01", self.requests[1].url.params["api-version"])
        self.assertEqual({"input": "  net ernings ", "dimensions": 2}, embedding)
        self.assertIn("/deployments/embedding-model/embeddings", self.requests[0].url.path)
        self.assertIn("/indexes/resolver-v1/docs/search", self.requests[1].url.path)
        self.assertEqual("semantic", search["queryType"])
        self.assertEqual("resolver-semantic", search["semanticConfiguration"])
        self.assertEqual("entity_id,catalog_version", search["select"])
        self.assertIn("catalog_version eq 'v1'", search["filter"])
        self.assertIn("entity_kind eq 'kpi_group'", search["filter"])
        self.assertEqual("preFilter", search["vectorFilterMode"])
        self.assertEqual([{"kind": "vector", "vector": [0.1, 0.2], "fields": "content_vector", "k": 50}], search["vectorQueries"])
        self.assertEqual(8, search["top"])

    async def test_organization_filter_cannot_be_kpi(self):
        await self.search("org")
        body = json.loads(self.requests[1].content)
        self.assertIn("entity_kind eq 'org'", body["filter"])
        self.assertNotIn("entity_kind eq 'kpi'", body["filter"])

    async def test_dimension_nonfinite_bool_text_or_invalid_embedding_fails_closed(self):
        for embedding in ([0.1], [0.1, True], [0.1, "0.2"], [0.1, 1e100], [], None):
            with self.subTest(embedding=embedding):
                self.responses = [{"data": [{"embedding": embedding}]}]
                self.requests = []
                self.assertFalse(await self.search())
                self.assertEqual(1, len(self.requests))
        for payload in ({}, {"data": None}, {"data": []}, {"data": [[], []]}, {"data": [None]}):
            self.responses = [payload]
            self.assertFalse(await self.search())

    async def test_malformed_search_documents_fail_closed(self):
        for document in ({}, {"entity_id": None, "catalog_version": "v1"},
                         {"entity_id": 42, "catalog_version": "v1"}, {"entity_id": "x", "catalog_version": 1}):
            self.responses = [{"data": [{"embedding": [0.1, 0.2]}]}, {"value": [document]}]
            self.assertFalse(await self.search())

    async def test_service_and_auth_failures_never_log_sensitive_details(self):
        for status in (403, 429, 503):
            self.status = status
            self.responses = [{"error": "private-token"}]
            with self.assertLogs("zavafinance.finance.search", level="WARNING") as logs:
                self.assertFalse(await self.search())
            self.assertNotIn("private-token", str(logs.output))
        self.credential.failure = ClientAuthenticationError("private-token")
        with self.assertLogs("zavafinance.finance.search", level="WARNING") as logs:
            self.assertFalse(await self.search())
        self.assertNotIn("private-token", str(logs.output))

    async def test_timeout_returns_uncertainty_and_caller_cancellation_propagates(self):
        self.delay = 60
        self.assertFalse(await self.search(settings=replace(SETTINGS, resolver_timeout_seconds=0.01)))
        self.failure = asyncio.CancelledError()
        self.delay = 0
        with self.assertRaises(asyncio.CancelledError):
            await self.search()

    async def test_unconfigured_search_never_acquires_token(self):
        self.assertFalse(await self.search(settings=Settings()))
        self.assertFalse(self.credential.scopes)
        self.assertFalse(self.requests)

    async def test_unexpected_programming_failure_propagates(self):
        self.failure = RuntimeError("bug")
        with self.assertRaises(RuntimeError):
            await self.search()

    async def test_choose_restricts_json_to_supplied_ids(self):
        for answer, expected in (
            ('{"ids":["candidate-one"]}', ["candidate-one"]), ('{"ids":[]}', []),
            ('{"ids":["company"]}', None), ('{"ids":["candidate-one","candidate-one"]}', None),
            ('{"ids":["candidate-one"],"finance":42}', None), ("not json", None),
            ("null", None), ("[]", None), ("{}", None), ('{"ids":null}', None),
            ('{"ids":[42]}', None), ('{"ids":[""]}', None), (None, None),
        ):
            seen = []
            async def rerank(raw_term, field, candidates):
                seen.append((raw_term, field, candidates))
                return answer
            self.assertEqual(expected, await AzureResolverSearch(SETTINGS, self.credential, rerank).choose(
                "earnings", "kpi", CANDIDATES))
            self.assertEqual([("earnings", "kpi", CANDIDATES)], seen)
        self.assertFalse(self.credential.scopes)

    async def test_choose_service_error_timeout_cancellation_and_programming_failure(self):
        async def fail(*args):
            raise ClientAuthenticationError("private-token")
        with self.assertLogs("zavafinance.finance.search", level="WARNING") as logs:
            self.assertIsNone(await AzureResolverSearch(SETTINGS, self.credential, fail).choose("earnings", "kpi", CANDIDATES))
        self.assertNotIn("private-token", str(logs.output))
        async def delay(*args):
            await asyncio.sleep(60)
        self.assertIsNone(await AzureResolverSearch(
            replace(SETTINGS, resolver_timeout_seconds=0.01), self.credential, delay).choose("earnings", "kpi", CANDIDATES))
        for failure in (asyncio.CancelledError(), RuntimeError("bug"), ValueError("programming error")):
            async def fail(*args):
                raise failure
            with self.assertRaises(type(failure)):
                await AzureResolverSearch(SETTINGS, self.credential, fail).choose("earnings", "kpi", CANDIDATES)
