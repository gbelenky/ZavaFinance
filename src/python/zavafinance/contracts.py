"""Shared application contracts; tokens never belong to persisted finance state."""

from dataclasses import dataclass, field
from typing import Any, Protocol, Sequence


class IdentityError(ValueError):
    """The caller's identity could not be established or did not match."""


class ProtocolError(ValueError):
    """The activity or finance reply violates the wire contract."""


@dataclass(frozen=True)
class CallerIdentity:
    tenant_id: str
    user_object_id: str

    def __post_init__(self) -> None:
        if not self.tenant_id.strip() or not self.user_object_id.strip():
            raise IdentityError("Tenant and user identity are required.")
        object.__setattr__(self, "tenant_id", self.tenant_id.strip())
        object.__setattr__(self, "user_object_id", self.user_object_id.strip())


class TokenProvider(Protocol):
    async def get_token(self, scopes: Sequence[str]) -> str: ...


@dataclass(frozen=True)
class ClarificationOption:
    id: str
    label: str
    description: str | None = None


@dataclass(frozen=True)
class ClarificationPrompt:
    request_id: str
    field: str
    message: str
    options: list[ClarificationOption]
    catalog_version: str


@dataclass(frozen=True)
class ClarificationSubmission:
    request_id: str
    option_id: str
    catalog_version: str


@dataclass(frozen=True)
class FinanceReply:
    text: str
    clarification: ClarificationPrompt | None = None


@dataclass
class SessionState:
    history: list[dict[str, Any]] = field(default_factory=list)
    copilot_conversation_id: str | None = None
    last_kpi_name: str | None = None
    pending_clarification: dict[str, Any] | None = None
    reply_clarification: ClarificationPrompt | None = None


class SessionStore(Protocol):
    async def load(self, key: str) -> SessionState: ...

    async def save(self, key: str, state: SessionState) -> None: ...


class FinanceApplication(Protocol):
    async def run_reply(self, session_key: str, question: str, token_provider: TokenProvider,
                        submission: ClarificationSubmission | None = None) -> FinanceReply: ...
