import json
import unittest

from microsoft_agents.activity import Activity

from zavafinance.cards import (
    CONTENT_TYPE, SUBMISSION_QUESTION, create_card, create_fallback_text, create_message,
    invoke_response, read_submission,
)
from zavafinance.contracts import (
    ClarificationOption, ClarificationPrompt, ClarificationSubmission, FinanceReply, ProtocolError,
)


def prompt():
    return ClarificationPrompt("r1", "org", "Which organization?",
                               [ClarificationOption("o1", "East", "Eastern division"),
                                ClarificationOption("o2", "West")], "v1")


def selection():
    return create_card(prompt())["body"][2]["selectAction"]["data"]


class CardTests(unittest.TestCase):
    def test_attachment_only_clickable_rows_and_descriptions(self):
        reply = FinanceReply("Choose one.", prompt())
        activity = create_message(reply)
        self.assertIsNone(activity.text)
        self.assertEqual(len(activity.attachments), 1)
        self.assertEqual(CONTENT_TYPE, activity.attachments[0].content_type)
        row = activity.attachments[0].content["body"][2]
        self.assertEqual("1. East", row["items"][0]["text"])
        self.assertEqual("Eastern division", row["items"][1]["text"])
        self.assertEqual("none", row["selectAction"]["associatedInputs"])
        self.assertEqual("Action.Submit", row["selectAction"]["type"])
        self.assertIn("1. East — Eastern division", create_fallback_text(reply))

    def test_text_reply(self):
        activity = create_message(FinanceReply("42"))
        self.assertEqual("42", activity.text)
        self.assertFalse(activity.attachments)

    def test_all_submission_transports(self):
        expected = ClarificationSubmission("r1", "o1", "v1")
        shapes = [
            Activity(type="message", value=selection()),
            Activity(type="message", value=json.dumps(selection())),
            Activity(type="invoke", name="task/submit", value={"data": selection()}),
            Activity(type="invoke", name="adaptiveCard/action",
                     value={"action": {"type": "Action.Execute", "data": selection()}}),
        ]
        for activity in shapes:
            with self.subTest(name=activity.name):
                self.assertEqual(expected, read_submission(activity))
        self.assertIsNone(read_submission(Activity(type="message", text="1")))
        self.assertIsNone(read_submission(Activity(type="message", text=SUBMISSION_QUESTION)))

    def test_rejects_stuffed_ids_wrong_actions_and_duplicates(self):
        bad_values = [{**selection(), "resolvedOrgId": "all"}, {**selection(), "schema": "other"},
                      {**selection(), "optionId": ["o1"]},
                      json.dumps(selection()).replace('"r1"', '"r1","requestId":"evil"')]
        for bad in bad_values:
            with self.subTest(value=bad), self.assertRaises(ProtocolError):
                read_submission(Activity(type="message", value=bad))
        for action in ("Action.OpenUrl", None):
            with self.assertRaises(ProtocolError):
                read_submission(Activity(type="invoke", name="adaptiveCard/action",
                                         value={"action": {"type": action, "data": selection()}}))
        with self.assertRaises(ProtocolError):
            read_submission(Activity(type="invoke", name="adaptiveCard/action",
                                     value={"action": {"type": "Action.Execute", "verb": "admin", "data": selection()}}))

    def test_size_limits(self):
        with self.assertRaises(ProtocolError):
            read_submission(Activity(type="message", value=" " * 8193))
        p = ClarificationPrompt("r", "org", "Pick",
                                [ClarificationOption(str(i), "x" * 200, "d" * 1000) for i in range(25)], "v1")
        with self.assertRaises(ProtocolError):
            create_card(p)
        escaped = ClarificationPrompt("r", "org", "Pick",
                                      [ClarificationOption(str(i), "\u00e9" * 200) for i in range(10)], "v")
        with self.assertRaises(ProtocolError):
            create_card(escaped)

    def test_protocol_acks(self):
        for status in (200, 400, 401, 503):
            ack = invoke_response("adaptiveCard/action", status).value
            self.assertEqual(200, ack["status"])
            self.assertEqual(status, ack["body"]["statusCode"])
            ack = invoke_response("task/submit", status).value
            self.assertEqual(status, ack["status"])
            self.assertEqual({"task": None}, ack["body"])
