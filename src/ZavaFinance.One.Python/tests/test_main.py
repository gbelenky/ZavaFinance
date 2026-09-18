import unittest
from contextlib import ExitStack, asynccontextmanager
from dataclasses import replace
from unittest.mock import AsyncMock, MagicMock, patch

import main as entrypoint
from zavafinance.config import Settings


class CompositionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.settings = Settings(
            project_endpoint="https://example.services.ai.azure.com/api/projects/finance",
            obo_tenant_id="11111111-1111-1111-1111-111111111111",
            obo_client_id="22222222-2222-2222-2222-222222222222",
            obo_audience="22222222-2222-2222-2222-222222222222",
            session_key_salt="a-separate-test-only-python-salt",
        )

    def fixtures(self, settings):
        self.events = []
        self.credential = object()

        @asynccontextmanager
        async def credential_context(**kwargs):
            self.events.append("credential.open")
            try:
                yield self.credential
            finally:
                self.events.append("credential.close")

        async def run_host():
            self.events.append("host.run")

        def client(name):
            value = MagicMock()
            value.aclose = AsyncMock(side_effect=lambda: self.events.append(name + ".close"))
            return value

        stack = ExitStack()
        self.addCleanup(stack.close)
        self.model = client("model")
        self.store = client("store")
        self.search = client("search")
        self.host = MagicMock(run_async=AsyncMock(side_effect=run_host))
        values = {
            "DefaultAzureCredential": MagicMock(side_effect=credential_context),
            "ModelRouter": MagicMock(return_value=self.model),
            "FoundrySessionStore": MagicMock(return_value=self.store),
            "AzureResolverSearch": MagicMock(return_value=self.search),
            "FabricStatementQuery": MagicMock(),
            "CopilotStudioClient": MagicMock(),
            "FabricDataAgentClient": MagicMock(),
            "Orchestrator": MagicMock(),
            "create_activity_host": MagicMock(return_value=self.host),
            "load_dotenv": MagicMock(),
            "enforce_telemetry_privacy": MagicMock(
                side_effect=lambda: self.events.append("privacy")
            ),
            "verify_sql_dependencies": MagicMock(),
        }
        stack.enter_context(patch.multiple(entrypoint, **values))
        stack.enter_context(patch.object(entrypoint.Settings, "from_env", return_value=settings))
        return values

    async def test_composes_tools_and_closes_resources_after_host(self):
        settings = replace(
            self.settings,
            sql_endpoint="example.datawarehouse.fabric.microsoft.com",
            database="finance",
            search_endpoint="https://example.search.windows.net",
            embedding_endpoint="https://example.openai.azure.com",
        )
        mocks = self.fixtures(settings)
        await entrypoint.main()
        self.assertEqual(
            ["privacy", "credential.open", "host.run", "store.close",
             "model.close", "credential.close"],
            self.events,
        )
        mocks["verify_sql_dependencies"].assert_called_once_with()
        mocks["AzureResolverSearch"].assert_called_once_with(
            settings, self.credential, rerank=self.model.rerank
        )
        kwargs = mocks["Orchestrator"].call_args.kwargs
        tokens = object()
        for key, factory in (
            ("query_factory", "FabricStatementQuery"),
            ("copilot_factory", "CopilotStudioClient"),
            ("data_agent_factory", "FabricDataAgentClient"),
        ):
            kwargs[key](tokens)
            mocks[factory].assert_called_once_with(settings, tokens)
        mocks["create_activity_host"].assert_called_once_with(
            settings, mocks["Orchestrator"].return_value
        )
        self.host.run_async.assert_awaited_once_with()

    async def test_unconfigured_sql_and_search_are_not_initialized(self):
        mocks = self.fixtures(self.settings)
        await entrypoint.main()
        mocks["verify_sql_dependencies"].assert_not_called()
        mocks["AzureResolverSearch"].assert_not_called()
        self.assertIsNone(mocks["Orchestrator"].call_args.kwargs["search"])
        self.assertIsNone(mocks["Orchestrator"].call_args.kwargs["query_factory"](object()))
        mocks["FabricStatementQuery"].assert_not_called()

    async def test_native_driver_failure_prevents_readiness(self):
        mocks = self.fixtures(replace(self.settings, sql_endpoint="example", database="finance"))
        mocks["verify_sql_dependencies"].side_effect = RuntimeError("native driver unavailable")
        with self.assertRaisesRegex(RuntimeError, "native driver unavailable"):
            await entrypoint.main()
        mocks["DefaultAzureCredential"].assert_not_called()
        mocks["create_activity_host"].assert_not_called()

    async def test_partial_startup_failure_closes_already_created_clients(self):
        mocks = self.fixtures(self.settings)
        mocks["FoundrySessionStore"].side_effect = RuntimeError("state configuration invalid")
        with self.assertRaisesRegex(RuntimeError, "state configuration invalid"):
            await entrypoint.main()
        self.assertEqual(
            ["privacy", "credential.open", "model.close", "credential.close"],
            self.events,
        )
        mocks["create_activity_host"].assert_not_called()


if __name__ == "__main__":
    unittest.main()
