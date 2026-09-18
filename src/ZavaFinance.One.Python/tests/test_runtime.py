import os
import logging
import unittest
from unittest.mock import MagicMock, patch

from zavafinance.runtime import enforce_telemetry_privacy, verify_sql_dependencies


class RuntimeDependencyTests(unittest.TestCase):
    def test_finance_content_capture_is_disabled_even_if_enabled_in_environment(self):
        with patch.dict(os.environ, {"OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT": "true"}):
            enforce_telemetry_privacy()
            self.assertEqual("false", os.environ["OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"])

    def test_encoded_oauth_state_logging_is_disabled_without_suppressing_errors(self):
        token_logger = logging.getLogger("microsoft_agents.hosting.core.connector.client.user_token")
        previous = token_logger.level
        try:
            token_logger.setLevel(logging.INFO)
            enforce_telemetry_privacy()
            self.assertEqual(logging.WARNING, token_logger.level)
        finally:
            token_logger.setLevel(previous)

    def test_native_driver_imports_on_this_machine(self):
        verify_sql_dependencies()

    def test_unresolved_native_provider_fails_before_readiness(self):
        driver = MagicMock()
        driver.get_native_provider_info.return_value = {"driver_path": None}
        with patch("zavafinance.runtime.importlib.import_module", return_value=driver):
            with self.assertRaisesRegex(RuntimeError, "native provider could not be resolved"):
                verify_sql_dependencies()

    def test_native_provider_load_failure_is_not_swallowed(self):
        with patch("zavafinance.runtime.ctypes.CDLL", side_effect=OSError("missing linked library")):
            with self.assertRaisesRegex(OSError, "missing linked library"):
                verify_sql_dependencies()

    def test_linux_missing_libraries_fail_before_readiness(self):
        with patch("zavafinance.runtime.sys.platform", "linux"):
            with patch("zavafinance.runtime.ctypes.util.find_library", return_value=None):
                with self.assertRaisesRegex(RuntimeError, "ltdl, krb5, gssapi_krb5"):
                    verify_sql_dependencies()


if __name__ == "__main__":
    unittest.main()
