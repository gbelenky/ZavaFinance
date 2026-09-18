"""Standalone native Activity host; all resources share one event loop."""

import asyncio
import os
from contextlib import AsyncExitStack

from azure.identity.aio import DefaultAzureCredential
from dotenv import load_dotenv

from zavafinance.activity import create_activity_host
from zavafinance.config import Settings
from zavafinance.finance import AzureResolverSearch, FabricStatementQuery
from zavafinance.integrations import CopilotStudioClient, FabricDataAgentClient
from zavafinance.model import ModelRouter
from zavafinance.orchestrator import Orchestrator
from zavafinance.runtime import enforce_telemetry_privacy, verify_sql_dependencies
from zavafinance.state import FoundrySessionStore


async def main() -> None:
    load_dotenv()
    enforce_telemetry_privacy()
    settings = Settings.from_env()
    if settings.sql_endpoint:
        verify_sql_dependencies()

    async with AsyncExitStack() as resources:
        credential = await resources.enter_async_context(
            DefaultAzureCredential(
                managed_identity_client_id=os.environ.get("FOUNDRY_AGENT_INSTANCE_CLIENT_ID"),
                exclude_interactive_browser_credential=True,
            )
        )
        model = ModelRouter(settings, credential)
        resources.push_async_callback(model.aclose)
        store = FoundrySessionStore(settings, credential)
        resources.push_async_callback(store.aclose)
        search = None
        if settings.search_endpoint:
            search = AzureResolverSearch(settings, credential, rerank=model.rerank)

        orchestrator = Orchestrator(
            settings,
            model,
            store,
            query_factory=lambda tokens: (
                FabricStatementQuery(settings, tokens) if settings.sql_endpoint else None
            ),
            search=search,
            copilot_factory=lambda tokens: CopilotStudioClient(settings, tokens),
            data_agent_factory=lambda tokens: FabricDataAgentClient(settings, tokens),
        )
        host = create_activity_host(settings, orchestrator)
        await host.run_async()


if __name__ == "__main__":
    asyncio.run(main())
