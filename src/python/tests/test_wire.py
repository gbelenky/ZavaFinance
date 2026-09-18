import base64
import json
import unittest

from zavafinance.contracts import (
    ClarificationOption, ClarificationPrompt, ClarificationSubmission, FinanceReply, ProtocolError,
)
from zavafinance.wire import (
    decode_submission, deserialize_reply, encode_submission, serialize_reply,
    strict_json_loads, validate_prompt, validate_reply, validate_submission,
)


class WireTests(unittest.TestCase):
    def test_reply_roundtrip(self):
        prompt = ClarificationPrompt("request", "org", "Which organization?",
                                     [ClarificationOption("org:1", "East", "Eastern division")], "v1")
        reply = FinanceReply("Choose a row.", prompt)
        self.assertEqual(reply, deserialize_reply(serialize_reply(reply)))
        self.assertEqual(FinanceReply("42"), deserialize_reply(serialize_reply(FinanceReply("42"))))

    def test_submission_roundtrip(self):
        choice = ClarificationSubmission("req", "opt", "v1")
        self.assertEqual(choice, decode_submission(encode_submission(choice)))

    def test_required_and_unmapped_properties(self):
        valid = json.loads(serialize_reply(FinanceReply("answer")))
        for bad in ({**valid, "schema": "v2"}, {**valid, "admin": True},
                    {"schema": valid["schema"], "text": "answer"}, {**valid, "text": 42}):
            with self.subTest(bad=bad), self.assertRaises(ProtocolError):
                deserialize_reply(json.dumps(bad))

    def test_duplicates_invalid_json_size_depth(self):
        for bad in ('{"x":1,"x":2}', '{"a":{"x":1,"x":2}}', '{"x":NaN}',
                    '{"x":Infinity}', '{"x":1e999}', '"\\ud800"', b'"\xff"', "["):
            with self.subTest(bad=bad), self.assertRaises(ProtocolError):
                strict_json_loads(bad)
        with self.assertRaises(ProtocolError):
            strict_json_loads(" " * 1025, 1024)
        with self.assertRaises(ProtocolError):
            strict_json_loads("[" * 17 + "0" + "]" * 17)

    def test_identifiers_text_and_option_limits(self):
        for identifier in ("", " ", "x" * 129, "a;b", "\u00e9", "a\n"):
            with self.subTest(identifier=identifier), self.assertRaises(ProtocolError):
                validate_submission(ClarificationSubmission(identifier, "x", "v1"))
        for text in ("", " ", "\x00", "\ud800", "x" * 100_001, "\U0001f600" * 50_001):
            with self.subTest(length=len(text)), self.assertRaises(ProtocolError):
                validate_reply(FinanceReply(text))
        for options in ([], [ClarificationOption("x", "a")] * 2,
                        [ClarificationOption(str(i), "a") for i in range(26)],
                        [ClarificationOption("x", "label", "")]):
            with self.subTest(options=len(options)), self.assertRaises(ProtocolError):
                validate_prompt(ClarificationPrompt("r", "org", "Choose", options, "v"))

    def test_submission_header_is_strict(self):
        good = encode_submission(ClarificationSubmission("r", "o", "v"))
        for invalid in ("", good + "\n", "####", "a" * 2000,
                        base64.b64encode(b'{"requestId":"r","requestId":"b","optionId":"o","catalogVersion":"v"}').decode(),
                        base64.b64encode(b'{"requestId":"r","optionId":"o","catalogVersion":"v","resolvedId":1}').decode()):
            with self.subTest(value=invalid[:20]), self.assertRaises(ProtocolError):
                decode_submission(invalid)
