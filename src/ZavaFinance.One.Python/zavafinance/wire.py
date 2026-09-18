"""Strict, versioned finance wire contracts shared by cards and session state."""

import base64
import binascii
import json
import math
import re
import unicodedata
from typing import Any

from .contracts import (
    ClarificationOption, ClarificationPrompt, ClarificationSubmission, FinanceReply,
    ProtocolError,
)

SCHEMA = "zava-finance.v1"
REPLY_FORMAT = "zava-finance-v1"
MAX_PAYLOAD_BYTES = 524_288
MAX_SUBMISSION_BYTES = 1024
_IDENTIFIER = re.compile(r"[A-Za-z0-9._:/-]{1,128}\Z")


def _identifier(value: object) -> None:
    if not isinstance(value, str) or not _IDENTIFIER.fullmatch(value):
        raise ProtocolError("Invalid clarification identifier.")


def _text(value: object, maximum: int) -> None:
    if not isinstance(value, str) or not value.strip():
        raise ProtocolError("Missing finance text.")
    try:
        if len(value.encode("utf-16-le")) // 2 > maximum:
            raise ProtocolError("Finance text exceeds the size limit.")
        value.encode("utf-8", errors="strict")
    except UnicodeError as exc:
        raise ProtocolError("Invalid Unicode in finance text.") from exc
    if any(unicodedata.category(c) == "Cc" and c not in "\r\n\t" for c in value):
        raise ProtocolError("Invalid control character in finance text.")


def validate_submission(submission: ClarificationSubmission) -> None:
    if not isinstance(submission, ClarificationSubmission):
        raise ProtocolError("Missing clarification selection.")
    for value in (submission.request_id, submission.option_id, submission.catalog_version):
        _identifier(value)


def validate_prompt(prompt: ClarificationPrompt) -> None:
    if not isinstance(prompt, ClarificationPrompt):
        raise ProtocolError("Missing clarification prompt.")
    _identifier(prompt.request_id)
    _identifier(prompt.catalog_version)
    if prompt.field not in ("org", "kpi"):
        raise ProtocolError("Unsupported clarification field.")
    _text(prompt.message, 4000)
    if not isinstance(prompt.options, list) or not 1 <= len(prompt.options) <= 25:
        raise ProtocolError("Invalid clarification option count.")
    ids: set[str] = set()
    for option in prompt.options:
        if not isinstance(option, ClarificationOption):
            raise ProtocolError("Missing clarification option.")
        _identifier(option.id)
        if option.id in ids:
            raise ProtocolError("Duplicate clarification option ID.")
        ids.add(option.id)
        _text(option.label, 200)
        if option.description is not None:
            _text(option.description, 1000)


def validate_reply(reply: FinanceReply) -> None:
    if not isinstance(reply, FinanceReply):
        raise ProtocolError("Missing finance reply.")
    _text(reply.text, 100_000)
    if reply.clarification is not None:
        validate_prompt(reply.clarification)


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ProtocolError("Duplicate finance payload property.")
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    raise ProtocolError("Non-finite JSON numbers are unsupported.")


def json_bytes(value: Any, maximum: int = MAX_PAYLOAD_BYTES) -> bytes:
    try:
        raw = json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    except (ValueError, TypeError, UnicodeError, RecursionError) as exc:
        raise ProtocolError("Malformed finance payload.") from exc
    if len(raw) > maximum:
        raise ProtocolError("Finance payload exceeds the size limit.")
    return raw


def strict_json_loads(raw: str | bytes, maximum: int = MAX_PAYLOAD_BYTES, max_depth: int = 16) -> Any:
    try:
        encoded = raw.encode("utf-8", errors="strict") if isinstance(raw, str) else raw
        if not isinstance(encoded, bytes) or not encoded or len(encoded) > maximum:
            raise ProtocolError("Missing or oversized finance payload.")
        value = json.loads(encoded.decode("utf-8", errors="strict"), object_pairs_hook=_unique_object,
                           parse_constant=_reject_constant)
    except (ValueError, UnicodeError, RecursionError) as exc:
        raise ProtocolError("Malformed finance payload.") from exc
    pending = [(value, 0)]
    while pending:
        node, depth = pending.pop()
        if isinstance(node, (list, dict)):
            if depth >= max_depth:
                raise ProtocolError("Finance payload exceeds the nesting limit.")
            children = node.values() if isinstance(node, dict) else node
            pending.extend((child, depth + 1) for child in children)
            if isinstance(node, dict):
                pending.extend((key, depth + 1) for key in node)
        elif isinstance(node, str):
            try:
                node.encode("utf-8", errors="strict")
            except UnicodeError as exc:
                raise ProtocolError("Invalid Unicode in finance payload.") from exc
        elif isinstance(node, float) and not math.isfinite(node):
            raise ProtocolError("Non-finite JSON numbers are unsupported.")
    return value


def require_fields(value: Any, required: set[str], optional: set[str] | None = None) -> dict[str, Any]:
    if not isinstance(value, dict) or not required <= value.keys() or value.keys() - required - (optional or set()):
        raise ProtocolError("Invalid finance payload properties.")
    return value


def submission_data(submission: ClarificationSubmission) -> dict[str, str]:
    validate_submission(submission)
    return {"requestId": submission.request_id, "optionId": submission.option_id,
            "catalogVersion": submission.catalog_version}


def read_submission_data(value: Any) -> ClarificationSubmission:
    data = require_fields(value, {"requestId", "optionId", "catalogVersion"})
    result = ClarificationSubmission(data["requestId"], data["optionId"], data["catalogVersion"])
    validate_submission(result)
    return result


def serialize_reply(reply: FinanceReply) -> str:
    validate_reply(reply)
    prompt = reply.clarification
    data = None if prompt is None else {
        "requestId": prompt.request_id, "field": prompt.field, "message": prompt.message,
        "catalogVersion": prompt.catalog_version,
        "options": [{"id": option.id, "label": option.label, "description": option.description}
                    for option in prompt.options],
    }
    return json_bytes({"schema": SCHEMA, "text": reply.text, "clarification": data}).decode("utf-8")


def deserialize_reply(raw: str) -> FinanceReply:
    data = require_fields(strict_json_loads(raw), {"schema", "text", "clarification"})
    if data["schema"] != SCHEMA:
        raise ProtocolError("Unsupported finance reply schema.")
    prompt = None
    if data["clarification"] is not None:
        p = require_fields(data["clarification"], {"requestId", "field", "message", "catalogVersion", "options"})
        if not isinstance(p["options"], list):
            raise ProtocolError("Invalid clarification options.")
        options = []
        for option in p["options"]:
            option = require_fields(option, {"id", "label"}, {"description"})
            options.append(ClarificationOption(option["id"], option["label"], option.get("description")))
        prompt = ClarificationPrompt(p["requestId"], p["field"], p["message"], options, p["catalogVersion"])
    reply = FinanceReply(data["text"], prompt)
    validate_reply(reply)
    return reply


def encode_submission(submission: ClarificationSubmission) -> str:
    return base64.b64encode(json_bytes(submission_data(submission), MAX_SUBMISSION_BYTES)).decode("ascii")


def decode_submission(encoded: str) -> ClarificationSubmission:
    if not isinstance(encoded, str) or not encoded or len(encoded) > ((MAX_SUBMISSION_BYTES + 2) // 3) * 4:
        raise ProtocolError("Invalid clarification encoding.")
    try:
        raw = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error) as exc:
        raise ProtocolError("Invalid clarification encoding.") from exc
    return read_submission_data(strict_json_loads(raw, MAX_SUBMISSION_BYTES))
