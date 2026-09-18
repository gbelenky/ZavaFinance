"""Validated environment configuration, with secrets excluded from representations."""

import os
from dataclasses import dataclass, field
from urllib.parse import urlsplit
from uuid import UUID


@dataclass(frozen=True)
class Settings:
    project_endpoint: str = ""
    model_deployment: str = "gpt-5.4-mini"
    reasoning_enabled: bool = True
    session_key_salt: str = field(default="", repr=False)
    session_store_name: str = "zavafinance-one-python-sessions"
    session_ttl_seconds: int = 30 * 86400
    max_history_messages: int = 20
    obo_tenant_id: str = ""
    obo_client_id: str = ""
    obo_audience: str = ""
    obo_client_secret: str = field(default="", repr=False)
    obo_managed_identity_client_id: str = ""
    oauth_connection_name: str = "mcs"
    copilot_environment_id: str = ""
    copilot_schema_name: str = ""
    subagent_timeout_seconds: int = 180
    sql_endpoint: str = ""
    database: str = ""
    sql_timeout_seconds: int = 20
    fabric_workspace_id: str = ""
    fabric_data_agent_id: str = ""
    data_agent_timeout_seconds: int = 300
    search_endpoint: str = ""
    embedding_endpoint: str = ""
    semantic_configuration: str = "resolver-semantic"
    resolver_timeout_seconds: int = 20
    clarification_ttl_seconds: int = 900
    max_candidates: int = 8

    @classmethod
    def from_env(cls) -> "Settings":
        def env(name: str, alternate: str, default: str = "") -> str:
            return os.environ.get(name, os.environ.get(alternate, default))

        reasoning = env("ModelReasoningEnabled", "MODEL_REASONING_ENABLED", "true").lower()
        if reasoning not in ("true", "false"):
            raise ValueError("MODEL_REASONING_ENABLED must be true or false.")
        settings = cls(
            project_endpoint=env("FOUNDRY_PROJECT_ENDPOINT", "AZURE_AI_PROJECT_ENDPOINT"),
            model_deployment=env("ModelDeployment", "AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt-5.4-mini"),
            reasoning_enabled=reasoning == "true",
            session_key_salt=env("PYTHON_SESSION_KEY_SALT", "Orchestrator__SessionKeySalt"),
            obo_tenant_id=env("OBO_TENANT_ID", "Obo__TenantId"),
            obo_client_id=env("OBO_CLIENT_ID", "Obo__ClientId"),
            obo_audience=env("OBO_AUDIENCE", "Obo__Audience"),
            obo_client_secret=env("OBO_CLIENT_SECRET", "Obo__ClientSecret"),
            obo_managed_identity_client_id=env("OBO_MANAGED_IDENTITY_CLIENT_ID", "Obo__ManagedIdentityClientId"),
            copilot_environment_id=env("COPILOT_STUDIO_ENVIRONMENT_ID", "CopilotStudioAgent__EnvironmentId"),
            copilot_schema_name=env("COPILOT_STUDIO_SCHEMA_NAME", "CopilotStudioAgent__SchemaName"),
            sql_endpoint=env("FABRIC_SQL_ENDPOINT", "Fabric__SqlEndpoint"),
            database=env("FABRIC_DATABASE", "Fabric__Database"),
            fabric_workspace_id=env("FABRIC_WORKSPACE_ID", "Fabric__WorkspaceId"),
            fabric_data_agent_id=env("FABRIC_DATA_AGENT_ID", "Fabric__DataAgentId"),
            search_endpoint=env("RESOLVER_SEARCH_ENDPOINT", "Resolver__SearchEndpoint"),
            embedding_endpoint=env("RESOLVER_EMBEDDING_ENDPOINT", "Resolver__EmbeddingEndpoint"),
            semantic_configuration=env("RESOLVER_SEMANTIC_CONFIGURATION", "Resolver__SemanticConfigurationName", "resolver-semantic"),
        )
        settings.validate()
        return settings

    def validate(self) -> None:
        if not self.session_key_salt.strip():
            raise ValueError("A separate PYTHON_SESSION_KEY_SALT is required.")
        for name in ("obo_tenant_id", "obo_client_id"):
            try:
                UUID(getattr(self, name))
            except ValueError as exc:
                raise ValueError(f"{name} must be a tenant-specific application GUID.") from exc
        if self.obo_managed_identity_client_id:
            try:
                UUID(self.obo_managed_identity_client_id)
            except ValueError as exc:
                raise ValueError("obo_managed_identity_client_id must be a managed identity client GUID.") from exc
        if not self.obo_audience.strip() or not self.model_deployment.strip():
            raise ValueError("OBO audience and model deployment are required.")
        self._https(self.project_endpoint, "project_endpoint")
        if bool(self.search_endpoint) != bool(self.embedding_endpoint):
            raise ValueError("Search and embedding endpoints must be configured together.")
        if self.search_endpoint:
            self._https(self.search_endpoint, "search_endpoint")
            self._https(self.embedding_endpoint, "embedding_endpoint")
            if not self.semantic_configuration.strip():
                raise ValueError("Resolver semantic configuration is required.")
        if bool(self.sql_endpoint) != bool(self.database):
            raise ValueError("SQL endpoint and database must be configured together.")
        if bool(self.fabric_workspace_id) != bool(self.fabric_data_agent_id):
            raise ValueError("Fabric workspace and data-agent IDs must be configured together.")
        if not 1 <= self.max_candidates <= 25 or self.max_history_messages < 3:
            raise ValueError("Candidate or history limit is outside its supported range.")
        if not 0 < self.resolver_timeout_seconds <= 120 or not 0 < self.clarification_ttl_seconds <= 86400:
            raise ValueError("Resolver limits are outside their supported ranges.")

    @staticmethod
    def _https(value: str, name: str) -> None:
        uri = urlsplit(value)
        if uri.scheme != "https" or not uri.hostname or uri.username or uri.password or uri.fragment:
            raise ValueError(f"{name} must be an HTTPS service endpoint without credentials.")
