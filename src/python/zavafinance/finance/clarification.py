import hashlib
import re
from typing import Any

from ..contracts import ClarificationSubmission
from .resolver import normalize

ORDINALS = ("first", "second", "third", "fourth", "fifth", "sixth", "seventh",
            "eighth", "ninth", "tenth")
SELECTION = re.compile(
    r"(?:the\s+)?(?:option\s+)?([0-9]{1,3}(?:st|nd|rd|th)?|"
    + "|".join(ORDINALS) + r")(?:\s+(?:one|option))?"
)


class ClarificationSelection:
    @staticmethod
    def is_reset(text: str) -> bool:
        return text.strip().lower() in ("reset", "/reset", "start over", "cancel", "never mind")

    @staticmethod
    def hash_label(text: str) -> str:
        return hashlib.sha256(normalize(text).encode("utf-8")).hexdigest().upper()

    @staticmethod
    def try_read(text: str, pending: dict[str, Any] | None) -> tuple[bool, ClarificationSubmission | None]:
        match = SELECTION.fullmatch(text.strip().rstrip(".!").lower())
        ids = pending.get("candidate_ids", []) if isinstance(pending, dict) else []
        if not isinstance(ids, list) or any(not isinstance(item, str) for item in ids):
            return bool(match), None
        index = None
        if match:
            choice = match[1]
            number = ORDINALS.index(choice) + 1 if choice in ORDINALS else int(re.match(r"[0-9]+", choice)[0])
            if 1 <= number <= len(ids):
                index = number - 1
        else:
            if pending is None:
                return False, None
            if text.strip() in ids:
                index = ids.index(text.strip())
            else:
                hashes = pending.get("candidate_label_hashes")
                if not isinstance(hashes, list) or len(hashes) != len(ids):
                    return False, None
                matches = [i for i, label in enumerate(hashes) if label == ClarificationSelection.hash_label(text)]
                if not matches:
                    return False, None
                if len(matches) == 1:
                    index = matches[0]
        if index is None or not pending or not isinstance(pending.get("request_id"), str) or not isinstance(pending.get("catalog_version"), str):
            return True, None
        return True, ClarificationSubmission(pending["request_id"], ids[index], pending["catalog_version"])
