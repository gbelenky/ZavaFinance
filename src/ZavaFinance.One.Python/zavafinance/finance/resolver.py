"""Caller-visible vocabulary and release-bound exact/confirmed resolution."""

import re
import unicodedata
from collections import defaultdict
from dataclasses import dataclass
from enum import StrEnum
from typing import Protocol, Sequence


class CatalogChangedException(ValueError):
    pass


@dataclass(frozen=True)
class ResolverEntity:
    catalog_version: str
    id: str
    kind: str
    name: str
    aliases: Sequence[str] = ()
    definition: str = ""
    parent_id: str | None = None
    hierarchy_path: str = ""
    kpi_code: str | None = None
    region_code: str | None = None
    department_code: str | None = None
    department_group: str | None = None
    is_reportable: bool = True


@dataclass(frozen=True)
class ResolverRelease:
    catalog_version: str
    search_index: str
    embedding_deployment: str
    embedding_dimensions: int

    def validate(self) -> None:
        if (not isinstance(self.catalog_version, str) or len(self.catalog_version) > 64
                or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9.-]*", self.catalog_version)
                or not isinstance(self.search_index, str)
                or not re.fullmatch(r"[a-z0-9][a-z0-9-]{1,126}[a-z0-9]", self.search_index)
                or not isinstance(self.embedding_deployment, str) or len(self.embedding_deployment) > 64
                or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]*", self.embedding_deployment)
                or type(self.embedding_dimensions) is not int or not 1 <= self.embedding_dimensions <= 3072):
            raise ValueError("The published resolver release has an invalid Search/embedding binding.")


@dataclass(frozen=True)
class ResolverCatalog:
    release: ResolverRelease
    entities: Sequence[ResolverEntity]

    @property
    def version(self) -> str:
        return self.release.catalog_version


@dataclass(frozen=True)
class ResolverCandidateId:
    id: str
    catalog_version: str


class ResolverCatalogSource(Protocol):
    async def get_release(self) -> ResolverRelease: ...
    async def load_catalog(self) -> ResolverCatalog: ...


class ResolverSearch(Protocol):
    async def search(self, raw_term: str, field: str, release: ResolverRelease) -> Sequence[ResolverCandidateId]: ...
    async def choose(self, raw_term: str, field: str, candidates: Sequence[ResolverEntity]) -> Sequence[str] | None: ...


class ResolutionStatus(StrEnum):
    RESOLVED = "resolved"
    CLARIFY = "clarify"
    NO_MATCH = "no_match"


@dataclass(frozen=True)
class EntityResolution:
    status: ResolutionStatus
    candidates: Sequence[ResolverEntity]

    @property
    def entity(self) -> ResolverEntity | None:
        return self.candidates[0] if self.status == ResolutionStatus.RESOLVED and len(self.candidates) == 1 else None


def normalize(text: str) -> str:
    result = []
    for char in unicodedata.normalize("NFKD", text).lower():
        category = unicodedata.category(char)
        if category == "Mn":
            continue
        if category.startswith("L") or category == "Nd":
            result.append(char)
        elif char == "%":
            result.append(" percent ")
        elif char == "&":
            result.append(" and ")
        else:
            result.append(" ")
    return " ".join("".join(result).split())


def in_field(entity: ResolverEntity, field: str) -> bool:
    return entity.kind == "org" if field == "org" else entity.kind in ("kpi", "kpi_group")


def _terms(entity: ResolverEntity) -> Sequence[str]:
    return (entity.name, *entity.aliases, entity.hierarchy_path, entity.kpi_code or "")


def _ordered(entities: Sequence[ResolverEntity]) -> list[ResolverEntity]:
    return sorted(entities, key=lambda e: (e.hierarchy_path, e.id))


def _exact(entities: Sequence[ResolverEntity]) -> EntityResolution:
    return EntityResolution(ResolutionStatus.RESOLVED if len(entities) == 1 else ResolutionStatus.CLARIFY,
                            _ordered(entities))


def _has_collision(candidates: Sequence[ResolverEntity]) -> bool:
    meanings: dict[str, set[str]] = defaultdict(set)
    for entity in candidates:
        for term in _terms(entity):
            normalized = normalize(term)
            if normalized:
                meanings[normalized].add(entity.id)
    return any(len(ids) > 1 for ids in meanings.values())


class StatementResolver:
    def __init__(self, catalog: ResolverCatalogSource, search: ResolverSearch | None = None):
        self.catalog, self.search_provider = catalog, search

    async def resolve(self, snapshot: ResolverCatalog, raw_term: str, field: str) -> EntityResolution:
        normalized = normalize(raw_term)
        if not normalized:
            return EntityResolution(ResolutionStatus.NO_MATCH, ())
        eligible = [entity for entity in snapshot.entities if in_field(entity, field)]
        ids = [entity for entity in eligible if entity.id.lower() == raw_term.strip().lower()]
        if ids:
            return _exact(ids)
        matches = [entity for entity in eligible if any(normalize(term) == normalized for term in _terms(entity))]
        if matches:
            return _exact(matches)
        if self.search_provider is None:
            return EntityResolution(ResolutionStatus.NO_MATCH, ())
        hits = await self.search_provider.search(raw_term, field, snapshot.release)
        if not hits:
            return EntityResolution(ResolutionStatus.NO_MATCH, ())
        # Search uses runtime identity. Reauthorize all IDs before exposing candidate text.
        fresh = await self.catalog.load_catalog()
        if fresh.release != snapshot.release:
            raise CatalogChangedException()
        allowed_ids = {hit.id for hit in hits if hit.catalog_version == fresh.version}
        candidates = _ordered([entity for entity in fresh.entities
                               if in_field(entity, field) and entity.id in allowed_ids])
        if not candidates:
            return EntityResolution(ResolutionStatus.NO_MATCH, ())
        if len(candidates) == 1 or _has_collision(candidates):
            return EntityResolution(ResolutionStatus.CLARIFY, candidates)
        choices = await self.search_provider.choose(raw_term, field, candidates)
        if choices is not None:
            if not choices:
                return EntityResolution(ResolutionStatus.NO_MATCH, ())
            allowed = {entity.id for entity in candidates}
            if (any(not isinstance(choice, str) for choice in choices)
                    or len(set(choices)) != len(choices) or any(choice not in allowed for choice in choices)):
                return EntityResolution(ResolutionStatus.CLARIFY, candidates)
            candidates = [entity for entity in candidates if entity.id in choices]
        return EntityResolution(ResolutionStatus.CLARIFY, candidates)
