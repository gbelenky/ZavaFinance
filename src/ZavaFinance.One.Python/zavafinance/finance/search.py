"""Runtime-identity hybrid retrieval; only IDs leave Search, never its privileged text."""

import asyncio
import inspect
import json
import logging
import math
from typing import Awaitable, Callable, Sequence
from urllib.parse import quote

from ..config import Settings
from .resolver import ResolverCandidateId, ResolverEntity, ResolverRelease

logger = logging.getLogger(__name__)
Rerank = Callable[[str, str, Sequence[ResolverEntity]], Awaitable[str | None]]


class _ResponseError(ValueError):
    pass


def _array(parent, name: str) -> list:
    if not isinstance(parent, dict) or not isinstance(parent.get(name), list):
        raise _ResponseError("Expected resolver response array.")
    return parent[name]


def _string(parent, name: str) -> str:
    if not isinstance(parent, dict) or not isinstance(parent.get(name), str):
        raise _ResponseError("Expected resolver response string.")
    return parent[name]


class AzureResolverSearch:
    def __init__(self, settings: Settings, credential, rerank: Rerank | None = None):
        self.settings, self.credential, self.rerank = settings, credential, rerank

    async def _post(self, http, url: str, scope: str, body: dict):
        if inspect.iscoroutinefunction(self.credential.get_token):
            token = await self.credential.get_token(scope)
        else:
            token = await asyncio.to_thread(self.credential.get_token, scope)
            if inspect.isawaitable(token):
                token = await token
        response = await http.post(url, headers={"Authorization": f"Bearer {token.token}"}, json=body)
        response.raise_for_status()
        return response.json()

    async def search(self, raw_term: str, field: str, release: ResolverRelease) -> Sequence[ResolverCandidateId]:
        import httpx
        from azure.core.exceptions import AzureError
        release.validate()
        if not self.settings.search_endpoint or not self.settings.embedding_endpoint:
            return ()
        try:
            async with asyncio.timeout(self.settings.resolver_timeout_seconds):
                async with httpx.AsyncClient(timeout=self.settings.resolver_timeout_seconds, follow_redirects=False) as http:
                    embedding = await self._post(
                        http, self.settings.embedding_endpoint.rstrip("/") + "/openai/deployments/"
                        + quote(release.embedding_deployment, safe="") + "/embeddings?api-version=2024-10-21",
                        "https://cognitiveservices.azure.com/.default",
                        {"input": raw_term, "dimensions": release.embedding_dimensions})
                    data = _array(embedding, "data")
                    if len(data) != 1:
                        raise _ResponseError("Expected one query embedding.")
                    vector = _array(data[0], "embedding")
                    if (len(vector) != release.embedding_dimensions
                            or any(type(value) not in (float, int) or not math.isfinite(value)
                                   or abs(value) > 3.4028234663852886e38 for value in vector)):
                        raise _ResponseError("Invalid query embedding.")
                    kind_filter = "entity_kind eq 'org'" if field == "org" else "(entity_kind eq 'kpi' or entity_kind eq 'kpi_group')"
                    version = release.catalog_version.replace("'", "''")
                    result = await self._post(
                        http, self.settings.search_endpoint.rstrip("/") + "/indexes/"
                        + quote(release.search_index, safe="") + "/docs/search?api-version=2024-07-01",
                        "https://search.azure.com/.default", {
                            "search": raw_term, "queryType": "semantic",
                            "semanticConfiguration": self.settings.semantic_configuration,
                            "searchFields": "canonical_name,aliases,definition,hierarchy_path",
                            "filter": f"catalog_version eq '{version}' and {kind_filter}",
                            "select": "entity_id,catalog_version", "top": self.settings.max_candidates,
                            "vectorFilterMode": "preFilter",
                            "vectorQueries": [{"kind": "vector", "vector": vector, "fields": "content_vector", "k": 50}],
                        })
                    return tuple(ResolverCandidateId(_string(item, "entity_id"), _string(item, "catalog_version"))
                                 for item in _array(result, "value"))
        except (httpx.HTTPError, AzureError, _ResponseError, json.JSONDecodeError, OverflowError, TimeoutError) as exc:
            logger.warning("Resolver retrieval unavailable. ErrorType=%s", type(exc).__name__)
            return ()

    async def choose(self, raw_term: str, field: str, candidates: Sequence[ResolverEntity]) -> Sequence[str] | None:
        if self.rerank is None:
            return None
        import httpx
        from azure.core.exceptions import AzureError
        from openai import OpenAIError
        try:
            async with asyncio.timeout(self.settings.resolver_timeout_seconds):
                answer = await self.rerank(raw_term, field, candidates)
        except (httpx.HTTPError, AzureError, OpenAIError, TimeoutError) as exc:
            logger.warning("Resolver candidate choice unavailable. ErrorType=%s", type(exc).__name__)
            return None
        if answer is None:
            return None
        try:
            result = json.loads(answer)
            ids = _array(result, "ids")
            if set(result) != {"ids"} or any(not isinstance(item, str) for item in ids):
                raise _ResponseError("Invalid candidate choice fields.")
            allowed = {candidate.id for candidate in candidates}
            if len(set(ids)) != len(ids) or any(item not in allowed for item in ids):
                raise _ResponseError("Invalid candidate choice identifiers.")
            return ids
        except (_ResponseError, json.JSONDecodeError, TypeError) as exc:
            logger.warning("Resolver candidate choice unavailable. ErrorType=%s", type(exc).__name__)
            return None
