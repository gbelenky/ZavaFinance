"""Host-level checks that do not contact a finance service or acquire user tokens."""

import ctypes.util
import importlib
import logging
import os
import sys


def enforce_telemetry_privacy() -> None:
    # Core 2.1.0's host defaults content capture to true, despite its helper's false default.
    os.environ["OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"] = "false"
    # The pinned token client logs encoded OAuth state at INFO.
    logging.getLogger("microsoft_agents.hosting.core.connector.client.user_token").setLevel(logging.WARNING)


def verify_sql_dependencies() -> None:
    if sys.platform == "linux":
        missing = [
            name for name in ("ltdl", "krb5", "gssapi_krb5")
            if ctypes.util.find_library(name) is None
        ]
        if missing:
            raise RuntimeError(
                "The Linux runtime is missing required SQL driver libraries: "
                + ", ".join(missing)
                + ". Use a runtime with the documented mssql-python prerequisites."
            )

    importlib.import_module("mssql_python.ddbc_bindings")
    driver = importlib.import_module("mssql_python")
    provider = driver.get_native_provider_info()
    path = provider.get("driver_path")
    if provider.get("error") or not isinstance(path, str) or not os.path.isfile(path):
        raise RuntimeError("The SQL driver's bundled native provider could not be resolved.")

    # The extension loads its provider lazily; explicitly load it before advertising readiness.
    if sys.platform == "linux":
        ctypes.CDLL(path, mode=os.RTLD_NOW | os.RTLD_LOCAL)
    else:
        ctypes.CDLL(path)
