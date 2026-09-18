import logging
from dataclasses import asdict, dataclass, replace
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal, localcontext
from typing import Callable
from uuid import uuid4

from ..config import Settings
from ..contracts import ClarificationOption, ClarificationPrompt, ClarificationSubmission, SessionState
from ..wire import validate_prompt
from .calculations import KpiCatalog, KpiDefinition, KpiUnit, rounded
from .clarification import ClarificationSelection
from .periods import FinancePeriod, FinancePeriodParser
from .resolver import (
    CatalogChangedException, EntityResolution, ResolutionStatus, ResolverCatalog,
    ResolverEntity, ResolverRelease, ResolverSearch, StatementResolver, in_field,
)

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class OrganizationScope:
    kind: str
    code: str
    name: str
    catalog_version: str | None = None
    release: ResolverRelease | None = None


@dataclass(frozen=True)
class StatementResult:
    kpi: KpiDefinition
    organization: OrganizationScope
    period: FinancePeriod
    value: Decimal | None
    prior_value: Decimal | None
    currency: str = "USD"


class SourceFooter:
    lakehouse = "Zava finance lakehouse"
    data_agent = "Zava finance data agent (Microsoft Fabric)"
    knowledge_base = "KPIpedia (Copilot Studio)"

    @staticmethod
    def append(answer: str, source: str) -> str:
        if not answer.strip() or "_Source:" in answer:
            return answer
        return f"{answer.rstrip()}\n\n_Source: {source}._"


class StatementTool:
    def __init__(self, query, state: SessionState, today: date, *, search: ResolverSearch | None = None,
                 session_key: str, options: Settings, clock: Callable[[], datetime] | None = None):
        self.query, self.state, self.today = query, state, today
        self.session_key, self.options = session_key, options
        self.clock = clock or (lambda: datetime.now(timezone.utc))
        self.resolver = StatementResolver(query, search) if query is not None and callable(getattr(query, "load_catalog", None)) else None

    async def execute(self, kpi: str | None = None, org: str = "", date_range: str = "") -> str:
        self.state.pending_clarification = None
        return await self._execute({
            "kpi": self.state.last_kpi_name if not kpi or not kpi.strip() else kpi,
            "org": org, "date_range": date_range, "as_of_date": self.today.isoformat(),
            "selected_kpi_id": None, "selected_organization_id": None,
        }, None)

    async def continue_(self, submission: ClarificationSubmission) -> str:
        pending = self.state.pending_clarification
        self.state.reply_clarification = None
        if (not isinstance(pending, dict) or pending.get("owner_session_key") != self.session_key
                or pending.get("request_id") != submission.request_id
                or pending.get("catalog_version") != submission.catalog_version
                or not isinstance(pending.get("candidate_ids"), list)
                or submission.option_id not in pending["candidate_ids"]):
            return "That selection does not belong to your pending question. Please ask the statement again."
        self.state.pending_clarification = None
        try:
            expiry = datetime.fromisoformat(pending["expires_at_utc"])
            if expiry.tzinfo is None:
                raise ValueError("Missing expiry timezone")
            if expiry <= self.clock():
                return "That clarification has expired. Please ask the statement again."
            release = ResolverRelease(**pending["release"])
            release.validate()
            if release.catalog_version != pending["catalog_version"] or pending["field"] not in ("kpi", "org"):
                raise ValueError("Invalid binding")
            arguments = dict(pending["arguments"])
            if (not all(isinstance(arguments.get(key), str) for key in ("kpi", "org", "date_range", "as_of_date"))
                    or any(arguments.get(key) is not None and not isinstance(arguments[key], str)
                           for key in ("selected_kpi_id", "selected_organization_id"))):
                raise ValueError("Incomplete pending arguments")
            date.fromisoformat(arguments["as_of_date"])
        except (TypeError, ValueError, KeyError):
            return "The finance catalogue binding has changed. Please ask the statement again."
        arguments["selected_kpi_id" if pending["field"] == "kpi" else "selected_organization_id"] = submission.option_id
        return await self._execute(arguments, release)

    async def _execute(self, arguments: dict, expected_release: ResolverRelease | None) -> str:
        self.state.reply_clarification = None
        kpi_term, org_term, date_range = arguments["kpi"], arguments["org"], arguments["date_range"]
        if not kpi_term or not kpi_term.strip():
            return "Which KPI would you like a statement for?"
        if not org_term or not org_term.strip():
            return f"Which organization should I report {kpi_term} for? You can name a region, a department, or the whole company."
        period = FinancePeriodParser.parse(date_range, date.fromisoformat(arguments["as_of_date"]))
        if period is None:
            return (f"Which period should I report {kpi_term} for, for example 'Q3 2026'?"
                    if not date_range or not date_range.strip() else
                    f"I could not interpret '{date_range}' as a period. Try 'Q3 2026', 'November 2025' or 'January to March 2026'.")
        if self.query is None:
            return "The finance warehouse is not configured in this environment."
        if self.resolver is None:
            return "The finance resolver catalogue is not configured in this environment."
        try:
            snapshot = await self.query.load_catalog()
            snapshot.release.validate()
            if expected_release is not None and snapshot.release != expected_release:
                raise CatalogChangedException()
            kpi = await self._resolve(snapshot, kpi_term, arguments.get("selected_kpi_id"), "kpi")
            if kpi.status != ResolutionStatus.RESOLVED:
                return self._resolution_reply(snapshot, kpi, "kpi", arguments)
            metric = kpi.entity
            if metric.kind == "kpi_group":
                members = [entity for entity in snapshot.entities if entity.kind == "kpi"
                           and entity.is_reportable and KpiCatalog.by_code(entity.kpi_code)
                           and self._is_descendant(entity, metric.id, snapshot)]
                return (self._resolution_reply(snapshot, EntityResolution(ResolutionStatus.CLARIFY, members), "kpi", arguments)
                        if members else "That KPI group has no supported reportable KPI. Please name one KPI.")
            calculation = KpiCatalog.by_code(metric.kpi_code)
            if not metric.is_reportable or calculation is None:
                return "That KPI does not have a supported statement calculation. Please choose another KPI."
            calculation = replace(calculation, name=metric.name)
            arguments = {**arguments, "selected_kpi_id": metric.id}
            org = await self._resolve(snapshot, org_term, arguments.get("selected_organization_id"), "org")
            if org.status != ResolutionStatus.RESOLVED:
                return self._resolution_reply(snapshot, org, "org", arguments)
            organization = org.entity
            if not organization.is_reportable:
                return "That organization is not a reportable scope. Please specify a reportable organization."
            if snapshot.release != await self.query.get_release():
                raise CatalogChangedException()
            result = await self.query.get_statement(
                calculation, OrganizationScope("org", organization.id, organization.hierarchy_path or organization.name,
                                               snapshot.version, snapshot.release), period)
            self.state.last_kpi_name = metric.name
            self.state.pending_clarification = None
            return self.render(result)
        except CatalogChangedException:
            self.state.pending_clarification = None
            return "The finance catalogue has changed. Please ask the statement again so I can resolve the current choices."
        except TimeoutError:
            return f"The finance warehouse did not respond in time for {kpi_term}."
        except Exception as exc:
            logger.error("get_statement failed. ErrorType=%s", type(exc).__name__)
            return f"I could not retrieve {kpi_term} from the finance warehouse."

    async def _resolve(self, snapshot: ResolverCatalog, term: str, selected_id: str | None, field: str) -> EntityResolution:
        if selected_id is None:
            return await self.resolver.resolve(snapshot, term, field)
        selected = [entity for entity in snapshot.entities if entity.id == selected_id and in_field(entity, field)]
        return EntityResolution(ResolutionStatus.RESOLVED if len(selected) == 1 else ResolutionStatus.NO_MATCH, selected)

    def _resolution_reply(self, snapshot: ResolverCatalog, resolution: EntityResolution, field: str, arguments: dict) -> str:
        term = arguments["kpi"] if field == "kpi" else arguments["org"]
        if resolution.status == ResolutionStatus.NO_MATCH:
            return f"I could not find an authorized {field} match for '{term}'. Please use a catalogue name or ID."
        if len(resolution.candidates) > 25:
            return f"'{term}' has too many possible {field} matches. Please add a full name, region, or catalogue ID."
        message = f"Please confirm which {field} you mean by '{term}'."
        options = [ClarificationOption(
            entity.id, self._limit(entity.name, 200),
            self._limit(" — ".join(value for value in (entity.hierarchy_path, entity.definition) if value.strip()), 1000))
            for entity in resolution.candidates]
        request_id = uuid4().hex
        prompt = ClarificationPrompt(request_id, field, message, options, snapshot.version)
        validate_prompt(prompt)
        self.state.pending_clarification = {
            "request_id": request_id, "field": field, "catalog_version": snapshot.version,
            "arguments": dict(arguments), "candidate_ids": [option.id for option in options],
            "expires_at_utc": (self.clock() + timedelta(seconds=self.options.clarification_ttl_seconds)).isoformat(),
            "owner_session_key": self.session_key, "release": asdict(snapshot.release),
            "candidate_label_hashes": [ClarificationSelection.hash_label(option.label) for option in options],
        }
        self.state.reply_clarification = prompt
        return message + "\n\n" + "\n".join(
            f"{index}. {option.label}" + (f" — {option.description}" if option.description else "")
            for index, option in enumerate(options, 1)) + "\n\nReply with the option number or select a choice."

    @staticmethod
    def _limit(text: str, count: int) -> str | None:
        if not text.strip():
            return None
        encoded = text.encode("utf-16-le")
        return text if len(encoded) // 2 <= count else encoded[:(count - 1) * 2].decode("utf-16-le") + "…"

    @staticmethod
    def _is_descendant(entity: ResolverEntity, group_id: str, snapshot: ResolverCatalog) -> bool:
        seen = set()
        parent = entity.parent_id
        by_id = {candidate.id: candidate for candidate in snapshot.entities}
        while parent is not None and parent not in seen:
            seen.add(parent)
            if parent == group_id:
                return True
            parent = by_id[parent].parent_id if parent in by_id else None
        return False

    @staticmethod
    def render(result: StatementResult) -> str:
        text = f"**{result.kpi.name} — {result.organization.name} — {result.period.label}**\n\n"
        if result.value is None:
            return text + "No data is available for that combination."
        with localcontext() as context:
            context.prec = 29
            text += f"- {result.period.label}: {_format(result.value, result.kpi, result.currency)}"
            if result.prior_value is not None:
                text += f"\n- Prior period: {_format(result.prior_value, result.kpi, result.currency)}"
                change = _change(result.value, result.prior_value, result.kpi)
                if change is not None:
                    text += f"\n- Change: {change}"
        return SourceFooter.append(text, f"{SourceFooter.lakehouse}. {result.kpi.name} = {result.kpi.formula}")


def _format(value: Decimal, kpi: KpiDefinition, currency: str) -> str:
    if kpi.unit == KpiUnit.PERCENT:
        return f"{rounded(value, 2):,.2f}%"
    if kpi.unit == KpiUnit.COUNT:
        return f"{rounded(value, 0):,.0f} FTE"
    if kpi.unit == KpiUnit.DAYS:
        return f"{rounded(value, 1):,.1f} days"
    return (f"{rounded(value / 1_000_000, 1):,.1f} M {currency}" if abs(value) >= 1_000_000
            else f"{rounded(value, 0):,.0f} {currency}")


def _change(current: Decimal, prior: Decimal, kpi: KpiDefinition) -> str | None:
    if kpi.unit == KpiUnit.PERCENT:
        points = rounded(current - prior, 2)
        return f"{abs(points):,.2f} pp {'up' if points >= 0 else 'down'} versus prior period"
    if prior == 0:
        return None
    change = rounded((current - prior) / abs(prior) * 100, 1)
    return f"{abs(change):,.1f}% {'up' if change >= 0 else 'down'} versus prior period"
