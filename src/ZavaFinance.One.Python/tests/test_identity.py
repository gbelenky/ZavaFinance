import json
import time
import unittest
from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import jwt
from azure.core.exceptions import ClientAuthenticationError
from cryptography.hazmat.primitives.asymmetric import rsa
from microsoft_agents.activity import Activity

from zavafinance.config import Settings
from zavafinance.contracts import CallerIdentity, IdentityError
from zavafinance.identity import OboTokenProvider, SessionKeyProvider, UserAssertionValidator, assert_payload_agrees

TENANT = "11111111-1111-1111-1111-111111111111"
USER = "22222222-2222-2222-2222-222222222222"
CLIENT = "33333333-3333-3333-3333-333333333333"


def settings():
    return Settings(obo_tenant_id=TENANT, obo_client_id=CLIENT, obo_audience=CLIENT,
                    session_key_salt="python-only-test-salt", obo_client_secret="synthetic-test-secret")


class AssertionTests(unittest.IsolatedAsyncioTestCase):
    @classmethod
    def setUpClass(cls):
        cls.key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        cls.other = rsa.generate_private_key(public_exponent=65537, key_size=2048)

    def jwk(self, key=None, kid="key1"):
        return {**json.loads(jwt.algorithms.RSAAlgorithm.to_jwk((key or self.key).public_key())),
                "kid": kid, "alg": "RS256", "use": "sig"}

    def token(self, *, key=None, kid="key1", remove=(), **changes):
        now = int(time.time())
        claims = {"iss": f"https://login.microsoftonline.com/{TENANT}/v2.0", "aud": CLIENT,
                  "tid": TENANT, "oid": USER, "exp": now + 600, "nbf": now - 10, "iat": now,
                  "scp": "access_as_user"}
        claims.update(changes)
        for field in remove:
            claims.pop(field)
        return jwt.encode(claims, key or self.key, algorithm="RS256", headers={"kid": kid})

    async def asyncSetUp(self):
        self.keys = [self.jwk()]
        self.fetches = []

        async def fetch(url):
            self.fetches.append(url)
            if "openid-configuration" in url:
                return {"issuer": f"https://login.microsoftonline.com/{TENANT}/v2.0",
                        "jwks_uri": "https://login.microsoftonline.com/common/discovery/v2.0/keys"}
            return {"keys": self.keys}

        self.validator = UserAssertionValidator(settings(), metadata_fetcher=fetch)

    async def test_valid_signed_assertion_and_cached_keys(self):
        for _ in range(2):
            self.assertEqual(CallerIdentity(TENANT, USER), await self.validator.validate(self.token()))
        self.assertEqual(2, len(self.fetches))

    async def test_supported_audience_and_issuer_aliases(self):
        for audience in (CLIENT, f"api://{CLIENT}", f"api://botid-{CLIENT}"):
            for issuer in (f"https://login.microsoftonline.com/{TENANT}/v2.0",
                           f"https://sts.windows.net/{TENANT}/"):
                with self.subTest(audience=audience, issuer=issuer):
                    self.assertEqual(USER, (await self.validator.validate(self.token(aud=audience, iss=issuer))).user_object_id)

    async def test_rejects_claim_mismatch_lifetime_missing_and_app_only(self):
        changes = [
            {"aud": "other"}, {"iss": "https://attacker.invalid"}, {"tid": CLIENT}, {"oid": "not-guid"},
            {"oid": ""}, {"exp": time.time() - 121}, {"nbf": time.time() + 121},
            {"scp": ""}, {"scp": ["access_as_user"]}, {"idtyp": "app"},
        ]
        for change in changes:
            with self.subTest(change=change), self.assertRaises(IdentityError):
                await self.validator.validate(self.token(**change))
        for missing in ("exp", "iss", "aud", "tid", "oid", "scp"):
            with self.subTest(missing=missing), self.assertRaises(IdentityError):
                await self.validator.validate(self.token(remove=(missing,)))

    async def test_clock_skew(self):
        result = await self.validator.validate(self.token(exp=time.time() - 60))
        self.assertEqual(USER, result.user_object_id)

    async def test_invalid_signature_and_unsigned_or_malformed_tokens(self):
        for token in (self.token(key=self.other), jwt.encode({"oid": USER}, "", algorithm="none"),
                      "not.a.token", "", "x" * 131_073):
            with self.subTest(token=token[:16]), self.assertRaises(IdentityError):
                await self.validator.validate(token)

    async def test_unknown_kid_rollover(self):
        await self.validator.validate(self.token())
        self.keys = [self.jwk(self.other, "key2")]
        self.assertEqual(USER, (await self.validator.validate(self.token(key=self.other, kid="key2"))).user_object_id)
        self.assertEqual(4, len(self.fetches))

    async def test_same_kid_signature_rollover(self):
        await self.validator.validate(self.token())
        self.keys = [self.jwk(self.other)]
        self.assertEqual(USER, (await self.validator.validate(self.token(key=self.other))).user_object_id)

    async def test_unknown_keys_refresh_is_throttled(self):
        await self.validator.validate(self.token())
        for i in range(3):
            with self.assertRaises(IdentityError):
                await self.validator.validate(self.token(kid=f"unknown-{i}"))
        self.assertEqual(4, len(self.fetches))

    async def test_metadata_failure_and_untrusted_key_location_fail_closed(self):
        for value in (TimeoutError(), {"issuer": f"https://login.microsoftonline.com/{TENANT}/v2.0",
                                     "jwks_uri": "https://attacker.invalid/keys"}):
            fetch = AsyncMock(side_effect=value) if isinstance(value, Exception) else AsyncMock(return_value=value)
            validator = UserAssertionValidator(settings(), metadata_fetcher=fetch)
            with self.assertRaises(IdentityError):
                await validator.validate(self.token())
            self.assertEqual(1, fetch.await_count)


class IdentityPartitionTests(unittest.TestCase):
    def test_session_hmac_partition_and_unambiguous_boundaries(self):
        provider = SessionKeyProvider("python-salt")
        caller = CallerIdentity(TENANT, USER)
        key = provider.create(caller, "conversation")
        self.assertRegex(key, r"^[A-Z2-7]{52}$")
        self.assertEqual(key, provider.create(caller, "conversation"))
        self.assertNotEqual(key, SessionKeyProvider("dotnet-salt").create(caller, "conversation"))
        self.assertNotEqual(key, provider.create(caller, "other"))
        self.assertNotEqual(key, provider.create(CallerIdentity(TENANT, CLIENT), "conversation"))
        self.assertNotEqual(provider.create(CallerIdentity("a", "bc"), "d"),
                            provider.create(CallerIdentity("ab", "c"), "d"))

    def test_payload_can_only_confirm_never_supply_identity(self):
        caller = CallerIdentity(TENANT, USER)
        assert_payload_agrees(Activity(type="message"), caller)
        activity = Activity.model_validate({"type": "message", "conversation": {"id": "c", "tenantId": TENANT},
                                            "from": {"aadObjectId": USER}})
        assert_payload_agrees(activity, caller)
        for field in ("tenant", "oid"):
            activity.conversation.tenant_id = CLIENT if field == "tenant" else TENANT
            activity.from_property.aad_object_id = CLIENT if field == "oid" else USER
            with self.assertRaises(IdentityError):
                assert_payload_agrees(activity, caller)


class OboTests(unittest.IsolatedAsyncioTestCase):
    async def test_secret_path_is_delegated_request_local_and_closed(self):
        credential = SimpleNamespace(get_token=AsyncMock(return_value=SimpleNamespace(token="delegated")),
                                     close=AsyncMock())
        with patch("zavafinance.identity.OnBehalfOfCredential", return_value=credential) as factory, \
                patch("zavafinance.identity.ManagedIdentityCredential") as mi:
            provider = OboTokenProvider(settings(), "synthetic-user-assertion")
            async with provider:
                self.assertEqual("delegated", await provider.get_token(["https://database.windows.net/.default"]))
            self.assertEqual("synthetic-user-assertion", factory.call_args.kwargs["user_assertion"])
            self.assertEqual("synthetic-test-secret", factory.call_args.kwargs["client_secret"])
            mi.assert_not_called()
            await provider.close()
            await provider.aclose()
            credential.close.assert_awaited_once()
            self.assertEqual("", provider._assertion)
            with self.assertRaises(IdentityError):
                await provider.get_token(["scope"])

    async def test_federated_path_authenticates_obo_not_finance(self):
        mi = SimpleNamespace(get_token=AsyncMock(return_value=SimpleNamespace(token="federation")),
                             close=AsyncMock())
        credential = SimpleNamespace(get_token=AsyncMock(return_value=SimpleNamespace(token="delegated")),
                                     close=AsyncMock())
        with patch("zavafinance.identity.ManagedIdentityCredential", return_value=mi), \
                patch("zavafinance.identity.OnBehalfOfCredential", return_value=credential) as factory:
            async with OboTokenProvider(replace(settings(), obo_client_secret=""), "user") as provider:
                await provider.get_token(["finance-scope"])
                self.assertEqual("federation", factory.call_args.kwargs["client_assertion_func"]())
                self.assertEqual("user", factory.call_args.kwargs["user_assertion"])
            mi.get_token.assert_awaited_once_with("api://AzureADTokenExchange/.default")
            credential.get_token.assert_awaited_once_with("finance-scope")
            mi.close.assert_awaited_once()

    async def test_secret_failure_does_not_downgrade_to_managed_identity(self):
        credential = SimpleNamespace(get_token=AsyncMock(side_effect=ClientAuthenticationError(message="denied")),
                                     close=AsyncMock())
        with patch("zavafinance.identity.OnBehalfOfCredential", return_value=credential), \
                patch("zavafinance.identity.ManagedIdentityCredential") as mi:
            async with OboTokenProvider(settings(), "user") as provider:
                with self.assertRaises(ClientAuthenticationError):
                    await provider.get_token(["scope"])
            mi.assert_not_called()

    async def test_missing_assertion_and_empty_exchange_rejected(self):
        with self.assertRaises(IdentityError):
            OboTokenProvider(settings(), "")
        credential = SimpleNamespace(get_token=AsyncMock(return_value=SimpleNamespace(token="")), close=AsyncMock())
        with patch("zavafinance.identity.OnBehalfOfCredential", return_value=credential):
            async with OboTokenProvider(settings(), "user") as provider:
                with self.assertRaises(ValueError):
                    await provider.get_token("scope")
                with self.assertRaises(ClientAuthenticationError):
                    await provider.get_token(["scope"])
