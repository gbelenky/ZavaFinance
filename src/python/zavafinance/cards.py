"""Adaptive Card rows and strict transport-only selection parsing."""

import json

from microsoft_agents.activity import Activity, Attachment

from .contracts import ClarificationPrompt, ClarificationSubmission, FinanceReply, ProtocolError
from .wire import (
    SCHEMA, json_bytes, read_submission_data, require_fields, strict_json_loads,
    submission_data, validate_prompt, validate_reply,
)

SUBMIT_ACTION = "zavaFinanceClarification"
SUBMISSION_QUESTION = "I submitted a clarification choice."
CONTENT_TYPE = "application/vnd.microsoft.card.adaptive"
MAX_CARD_BYTES = 28_000
MAX_ACTIVITY_VALUE_BYTES = 8192


def create_fallback_text(reply: FinanceReply) -> str:
    validate_reply(reply)
    if reply.clarification is None:
        return reply.text
    prompt = reply.clarification
    rows = "\n".join(f"{i}. {o.label}" + (f" — {o.description}" if o.description else "")
                     for i, o in enumerate(prompt.options, 1))
    return f"{prompt.message}\n\n{rows}\n\nReply with the option number or select a choice. Type reset to start over."


def create_card(prompt: ClarificationPrompt) -> dict:
    validate_prompt(prompt)
    body = [
        {"type": "TextBlock", "text": prompt.message, "wrap": True, "weight": "Bolder"},
        {"type": "TextBlock", "text": "Choose an organization" if prompt.field == "org" else "Choose a KPI",
         "wrap": True},
    ]
    for index, option in enumerate(prompt.options, 1):
        title = f"{index}. {option.label}"
        items = [{"type": "TextBlock", "text": title, "wrap": True, "weight": "Bolder"}]
        if option.description:
            items.append({"type": "TextBlock", "text": option.description, "wrap": True,
                          "spacing": "Small", "isSubtle": True})
            title += f" — {option.description}"
        body.append({
            "type": "Container", "style": "emphasis", "spacing": "Small", "items": items,
            "selectAction": {
                "type": "Action.Submit", "title": title, "associatedInputs": "none",
                "data": {"schema": SCHEMA, "action": SUBMIT_ACTION,
                         **submission_data(ClarificationSubmission(prompt.request_id, option.id, prompt.catalog_version))},
            },
        })
    body.append({"type": "TextBlock", "wrap": True,
                 "text": "Click an option to use it, or reply with its number. Type reset to start over."})
    card = {"$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard", "version": "1.3", "body": body,
            "fallbackText": create_fallback_text(FinanceReply(prompt.message, prompt))}
    json_bytes(card, MAX_CARD_BYTES)
    if len(json.dumps(card, ensure_ascii=True).encode("utf-8")) > MAX_CARD_BYTES:
        raise ProtocolError("The serialized Adaptive Card exceeds the size limit.")
    return card


def create_message(reply: FinanceReply) -> Activity:
    validate_reply(reply)
    if reply.clarification is None:
        return Activity(type="message", text=reply.text)
    # MCS may not acknowledge mixed text/card callbacks with a single activity ID.
    return Activity(type="message", attachments=[
        Attachment(content_type=CONTENT_TYPE, content=create_card(reply.clarification)),
    ])


def is_supported_invoke(activity: Activity) -> bool:
    return activity.type == "invoke" and activity.name in ("adaptiveCard/action", "task/submit")


def read_submission(activity: Activity) -> ClarificationSubmission | None:
    if activity.value is None:
        return None
    if activity.type != "message" and not is_supported_invoke(activity):
        raise ProtocolError("Unsupported clarification activity.")
    raw = activity.value if isinstance(activity.value, str) else json_bytes(activity.value, MAX_ACTIVITY_VALUE_BYTES)
    data = strict_json_loads(raw, MAX_ACTIVITY_VALUE_BYTES, max_depth=8)
    if activity.type == "invoke" and activity.name == "adaptiveCard/action":
        if not isinstance(data, dict) or not isinstance(data.get("action"), dict):
            raise ProtocolError("Malformed adaptive card action.")
        action = data["action"]
        if action.get("type") not in ("Action.Execute", "Action.Submit") or action.get("verb") not in (None, SUBMIT_ACTION):
            raise ProtocolError("Unsupported clarification action.")
        data = action.get("data")
    elif activity.type == "invoke" and activity.name == "task/submit":
        data = data.get("data") if isinstance(data, dict) else None
    data = require_fields(data, {"schema", "action", "requestId", "optionId", "catalogVersion"})
    if data["schema"] != SCHEMA or data["action"] != SUBMIT_ACTION:
        raise ProtocolError("Unsupported clarification schema or action.")
    return read_submission_data({k: data[k] for k in ("requestId", "optionId", "catalogVersion")})


def invoke_response(name: str, status: int) -> Activity:
    if name not in ("adaptiveCard/action", "task/submit"):
        raise ProtocolError("Unsupported clarification invoke.")
    body = {"task": None}
    if name == "adaptiveCard/action":
        body = {
            "statusCode": status,
            "type": "application/vnd.microsoft.activity.message" if status == 200 else "application/vnd.microsoft.error",
            "value": "Request received." if status == 200 else {
                "code": {401: "Unauthorized", 503: "ServiceUnavailable"}.get(status, "BadRequest"),
                "message": "The choice could not be accepted. Please ask again.",
            },
        }
    return Activity(type="invokeResponse", value={"status": 200 if name == "adaptiveCard/action" else status, "body": body})
