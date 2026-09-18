"""Cryptographically verified user identity and request-local delegated credentials."""

import asyncio
import base64
import hashlib
import hmac
import struct
import time
from collections.abc import Awaitable, Callable, Sequence
from urllib.parse import urlsplit
from uuid import UUID

import aiohttp
import jwt
from azure.core.exceptions import ClientAuthenticationError
from azure.identity.aio import ManagedIdentityCredential, OnBehalfOfCredential

from .config import Settings
from .contracts import CallerIdentity, IdentityError

_VALIDATION_FAILURE = "The user assertion could not be validated."
_EXCHANGE_SCOPE = "api://AzureADTokenExchange/.default"


class SessionKeyProvider:
    def __init__(self, salt: str):
        if not isinstance(salt, str) or not salt.strip():
            raise ValueError("A separate Python session key salt is required.")
        self._salt = salt.encode("utf-8")

    def create(self, caller: CallerIdentity, conversation_id: str) -> str:
        if not isinstance(caller, CallerIdentity):
            raise IdentityError("A validated caller is required.")
        if not isinstance(conversation_id, str) or not conversation_id.strip():
            raise IdentityError("A conversation identifier is required.")
        fields = [caller.tenant_id.casefold(), caller.user_object_id.casefold(), conversation_id]
        message = b"zava-finance-one-python:v1"
        for value in fields:
            encoded = value.encode("utf-8", errors="strict")
            message += struct.pack(">I", len(encoded)) + encoded
        digest = hmac.new(self._salt, message, hashlib.sha256).digest()
        return base64.b32encode(digest).decode("ascii").rstrip("=")


def assert_payload_agrees(activity, caller: CallerIdentity) -> None:
    tenant = getattr(activity.conversation, "tenant_id", None)
    oid = getattr(activity.from_property, "aad_object_id", None)
    if isinstance(tenant, str) and tenant.strip() and tenant.casefold() != caller.tenant_id.casefold():
        raise IdentityError("Activity tenant does not match the validated user token.")
    if isinstance(oid, str) and oid.strip() and oid.casefold() != caller.user_object_id.casefold():
        raise IdentityError("Activity sender does not match the validated user token.")


class UserAssertionValidator:
    """Tenant-pinned OIDC metadata, bounded key cache, and a throttled rollover refresh."""

    def __init__(self, settings: Settings, *,
                 metadata_fetcher: Callable[[str], Awaitable[dict]] | None = None):
        self._tenant = str(UUID(settings.obo_tenant_id))
        values = [v.strip() for v in settings.obo_audience.split(";") if v.strip()]
        self._audiences = list(dict.fromkeys(a for v in values for a in (v, f"api://{v}", f"api://botid-{v}")))
        if not self._audiences:
            raise ValueError("An OBO audience is required.")
        self._issuers = [f"https://login.microsoftonline.com/{self._tenant}/v2.0",
                         f"https://sts.windows.net/{self._tenant}/"]
        self._metadata_url = f"https://login.microsoftonline.com/{self._tenant}/v2.0/.well-known/openid-configuration"
        self._fetcher = metadata_fetcher or self._fetch_json
        self._keys: dict[str, object] = {}
        self._expires = 0.0
        self._last_rollover = float("-inf")
        self._lock = asyncio.Lock()

    @staticmethod
    async def _fetch_json(url: str) -> dict:
        async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=15)) as session:
            async with session.get(url, allow_redirects=False) as response:
                response.raise_for_status()
                raw = await response.content.read(1_048_577)
                if len(raw) > 1_048_576:
                    raise IdentityError("Signing-key metadata is oversized.")
                from .wire import strict_json_loads
                result = strict_json_loads(raw, 1_048_576)
                if not isinstance(result, dict):
                    raise IdentityError("Signing-key metadata is invalid.")
                return result

    async def _refresh(self, *, rollover: bool = False) -> None:
        async with self._lock:
            now = time.monotonic()
            if rollover:
                if now - self._last_rollover < 30:
                    return
                self._last_rollover = now
            elif self._keys and now < self._expires:
                return
            try:
                metadata = await self._fetcher(self._metadata_url)
                uri = urlsplit(metadata["jwks_uri"])
                if (metadata.get("issuer") != self._issuers[0] or uri.scheme != "https"
                        or uri.hostname != "login.microsoftonline.com" or uri.port not in (None, 443)
                        or uri.username or uri.password or uri.fragment):
                    raise IdentityError("Untrusted signing-key metadata.")
                jwks = await self._fetcher(metadata["jwks_uri"])
                keys = {}
                for key in jwks["keys"]:
                    if key.get("kty") != "RSA" or key.get("use", "sig") != "sig" or key.get("alg", "RS256") != "RS256":
                        continue
                    kid = key.get("kid")
                    if not isinstance(kid, str) or not kid or kid in keys:
                        raise IdentityError("Invalid signing-key identifier.")
                    keys[kid] = jwt.PyJWK.from_dict(key, algorithm="RS256").key
                if not keys:
                    raise IdentityError("No usable signing keys.")
            except (aiohttp.ClientError, TimeoutError, ValueError, KeyError, TypeError, jwt.PyJWTError) as exc:
                raise IdentityError("The signing keys needed to verify the user assertion are unavailable.") from exc
            self._keys = keys
            self._expires = now + 6 * 3600

    async def validate(self, assertion: str) -> CallerIdentity:
        if not isinstance(assertion, str) or not assertion.strip() or len(assertion) > 131_072:
            raise IdentityError(_VALIDATION_FAILURE)
        try:
            header = jwt.get_unverified_header(assertion)
            kid = header.get("kid")
            if header.get("alg") != "RS256" or not isinstance(kid, str) or not kid:
                raise IdentityError(_VALIDATION_FAILURE)
            await self._refresh()
            if kid not in self._keys:
                await self._refresh(rollover=True)
            key = self._keys.get(kid)
            if key is None:
                raise IdentityError(_VALIDATION_FAILURE)
            try:
                claims = self._decode(assertion, key)
            except jwt.InvalidSignatureError:
                await self._refresh(rollover=True)
                claims = self._decode(assertion, self._keys.get(kid))
            tid, oid = claims.get("tid"), claims.get("oid")
            if not isinstance(tid, str) or str(UUID(tid)) != self._tenant or not isinstance(oid, str):
                raise IdentityError(_VALIDATION_FAILURE)
            # A service principal's oid is not a user. Delegated scope is mandatory.
            if claims.get("idtyp") == "app" or not isinstance(claims.get("scp"), str) or not claims["scp"].strip():
                raise IdentityError(_VALIDATION_FAILURE)
            return CallerIdentity(self._tenant, str(UUID(oid)))
        except (jwt.PyJWTError, ValueError, TypeError) as exc:
            raise IdentityError(_VALIDATION_FAILURE) from exc

    def _decode(self, assertion: str, key: object) -> dict:
        return jwt.decode(assertion, key=key, algorithms=["RS256"], audience=self._audiences,
                          issuer=self._issuers, leeway=120,
                          options={"require": ["exp", "iss", "aud", "tid", "oid"],
                                   "verify_signature": True, "verify_exp": True, "verify_nbf": True})


class OboTokenProvider:
    """One user, one turn. Managed identity authenticates OBO, never finance itself."""

    def __init__(self, settings: Settings, assertion: str):
        if not isinstance(assertion, str) or not assertion.strip():
            raise IdentityError("A delegated user assertion is required.")
        self._settings = settings
        self._assertion = assertion
        self._federated_assertion = ""
        self._managed_identity = None
        self._credential = None
        self._lock = asyncio.Lock()
        self._closed = False

    async def get_token(self, scopes: Sequence[str]) -> str:
        if self._closed:
            raise IdentityError("This turn's token provider is closed.")
        if isinstance(scopes, str) or not scopes or any(not isinstance(s, str) or not s.strip() for s in scopes):
            raise ValueError("At least one downstream scope is required.")
        async with self._lock:
            if self._closed:
                raise IdentityError("This turn's token provider is closed.")
            settings = self._settings
            if not settings.obo_client_secret:
                if self._managed_identity is None:
                    self._managed_identity = ManagedIdentityCredential(
                        client_id=getattr(settings, "obo_managed_identity_client_id", "") or None)
                token = await self._managed_identity.get_token(_EXCHANGE_SCOPE)
                if not token.token:
                    raise ClientAuthenticationError(message="OBO client federation returned no assertion.")
                self._federated_assertion = token.token
            if self._credential is None:
                auth = ({"client_secret": settings.obo_client_secret} if settings.obo_client_secret else
                        {"client_assertion_func": lambda: self._federated_assertion})
                self._credential = OnBehalfOfCredential(
                    tenant_id=settings.obo_tenant_id, client_id=settings.obo_client_id,
                    user_assertion=self._assertion, **auth)
            result = await self._credential.get_token(*scopes)
            if not result.token:
                raise ClientAuthenticationError(message="Delegated exchange returned no access token.")
            return result.token

    async def close(self) -> None:
        self._closed = True
        async with self._lock:
            try:
                if self._credential is not None:
                    await self._credential.close()
            finally:
                try:
                    if self._managed_identity is not None:
                        await self._managed_identity.close()
                finally:
                    self._credential = self._managed_identity = None
                    self._assertion = self._federated_assertion = ""

    async def aclose(self) -> None:
        """Close request-owned async credentials; compatible with AsyncExitStack callbacks."""
        await self.close()

    async def __aenter__(self):
        return self

    async def __aexit__(self, *_):
        await self.aclose()
