import os
import unittest
from dataclasses import replace
from unittest.mock import patch

from zavafinance.config import Settings


class ConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.settings = Settings(
            project_endpoint="https://example.services.ai.azure.com/api/projects/finance",
            obo_tenant_id="11111111-1111-1111-1111-111111111111",
            obo_client_id="22222222-2222-2222-2222-222222222222",
            obo_audience="22222222-2222-2222-2222-222222222222",
            session_key_salt="a-separate-test-only-python-salt",
            obo_client_secret="test-only-credential-not-a-real-secret",
        )

    def test_python_state_is_separate(self):
        self.settings.validate()
        self.assertEqual("zavafinance-one-python-sessions", self.settings.session_store_name)
        self.assertEqual("gpt-5.4-mini", self.settings.model_deployment)
        self.assertTrue(self.settings.reasoning_enabled)

    def test_secrets_not_in_representation(self):
        self.assertNotIn(self.settings.session_key_salt, repr(self.settings))
        self.assertNotIn(self.settings.obo_client_secret, repr(self.settings))

    def test_reject_invalid_security_or_half_configured_services(self):
        for change in (
            {"session_key_salt": ""},
            {"obo_tenant_id": "not-a-guid"},
            {"obo_managed_identity_client_id": "not-a-guid"},
            {"obo_audience": ""},
            {"project_endpoint": "http://example.test"},
            {"project_endpoint": "https://user:password@example.test"},
            {"search_endpoint": "https://example.search.windows.net"},
            {"sql_endpoint": "example.datawarehouse.fabric.microsoft.com"},
            {"fabric_workspace_id": "workspace"},
            {"max_candidates": 26},
            {"clarification_ttl_seconds": 0},
        ):
            with self.subTest(change=tuple(change)):
                with self.assertRaises(ValueError):
                    replace(self.settings, **change).validate()

    def test_dotnet_and_python_environment_names(self):
        with patch.dict(os.environ, {
            "FOUNDRY_PROJECT_ENDPOINT": self.settings.project_endpoint,
            "Obo__TenantId": self.settings.obo_tenant_id,
            "Obo__ClientId": self.settings.obo_client_id,
            "Obo__Audience": self.settings.obo_audience,
            "Obo__ManagedIdentityClientId": "33333333-3333-3333-3333-333333333333",
            "PYTHON_SESSION_KEY_SALT": self.settings.session_key_salt,
            "ONE_SESSION_KEY_SALT": "must-never-be-used",
        }, clear=True):
            settings = Settings.from_env()
            self.assertEqual(self.settings.session_key_salt, settings.session_key_salt)
            self.assertEqual("33333333-3333-3333-3333-333333333333", settings.obo_managed_identity_client_id)
            os.environ["ModelReasoningEnabled"] = "guess"
            with self.assertRaises(ValueError):
                Settings.from_env()


if __name__ == "__main__":
    unittest.main()
