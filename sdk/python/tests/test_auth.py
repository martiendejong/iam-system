import json
import time

import httpx
import pytest

from iam_sdk import IamClient
from iam_sdk.auth import TokenCache
from iam_sdk.device import IamDeviceClient
from iam_sdk.exceptions import IamAuthError


class FakeAuthServer:
    """Minimal fake of the IAM API's auth endpoints for MockTransport."""

    def __init__(self):
        self.refresh_token = "rt-1"
        self.refresh_count = 0

    def handle(self, request: httpx.Request) -> httpx.Response:
        if request.url.path == "/api/auth/login" and request.method == "POST":
            body = json.loads(request.content)
            if body["email"] == "admin@example.com" and body["password"] == "correct":
                return httpx.Response(
                    200,
                    json={
                        "accessToken": "at-1",
                        "expiresIn": 900,
                        "user": {"id": "u1", "email": body["email"], "roles": ["Admin"]},
                    },
                    headers={"set-cookie": f"refreshToken={self.refresh_token}; HttpOnly; Path=/"},
                )
            return httpx.Response(401, json={"error": "invalid credentials"})

        if request.url.path == "/api/auth/refresh" and request.method == "POST":
            self.refresh_count += 1
            new_rt = f"rt-{self.refresh_count + 1}"
            self.refresh_token = new_rt
            return httpx.Response(
                200,
                json={"accessToken": f"at-{self.refresh_count + 1}", "expiresIn": 900},
                headers={"set-cookie": f"refreshToken={new_rt}; HttpOnly; Path=/"},
            )

        if request.url.path == "/api/users/me" and request.method == "GET":
            if request.headers.get("authorization") != "Bearer at-2" and request.headers.get("authorization") != "Bearer at-1":
                return httpx.Response(401)
            return httpx.Response(
                200,
                json={"id": "u1", "email": "admin@example.com", "firstName": "Ada", "lastName": "Min"},
            )

        return httpx.Response(404)


@pytest.mark.asyncio
async def test_login_success_stores_token_and_refresh_cookie():
    server = FakeAuthServer()
    client = IamClient(transport=httpx.MockTransport(server.handle))
    try:
        response = await client.login("admin@example.com", "correct")
        assert response.access_token == "at-1"
        assert response.email == "admin@example.com"
        assert client.is_authenticated
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_login_invalid_credentials_raises_iam_auth_error():
    server = FakeAuthServer()
    client = IamClient(transport=httpx.MockTransport(server.handle))
    try:
        with pytest.raises(IamAuthError):
            await client.login("admin@example.com", "wrong-password")
        assert not client.is_authenticated
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_refresh_rotates_access_token():
    server = FakeAuthServer()
    client = IamClient(transport=httpx.MockTransport(server.handle))
    try:
        await client.login("admin@example.com", "correct")
        first_token = client.access_token
        refreshed = await client.refresh()
        assert refreshed.access_token != first_token
        assert client.access_token == refreshed.access_token
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_proactive_refresh_before_expiry():
    """get_current_user() should transparently refresh an about-to-expire token."""
    server = FakeAuthServer()
    client = IamClient(transport=httpx.MockTransport(server.handle))
    try:
        await client.login("admin@example.com", "correct")
        # Force the cached token to look like it's about to expire.
        client._tokens._expires_at = time.monotonic()
        user = await client.get_current_user()
        assert user.email == "admin@example.com"
        assert server.refresh_count == 1
        assert client.access_token == "at-2"
    finally:
        await client.close()


@pytest.mark.asyncio
async def test_refresh_without_prior_login_raises():
    client = IamClient(transport=httpx.MockTransport(FakeAuthServer().handle))
    try:
        with pytest.raises(IamAuthError):
            await client.refresh()
    finally:
        await client.close()


def test_token_cache_expiry_semantics():
    cache = TokenCache(refresh_margin_seconds=5)
    assert cache.is_expiring_soon() is True  # no token set yet

    cache.set("tok", expires_in=100)
    assert cache.is_expiring_soon() is False

    cache.set("tok", expires_in=3)  # inside the 5s margin
    assert cache.is_expiring_soon() is True

    cache.clear()
    assert cache.token is None
    assert cache.is_expiring_soon() is True


def test_token_cache_no_expiry_never_expires():
    cache = TokenCache()
    cache.set("tok", expires_in=0)
    assert cache.is_expiring_soon() is False


class FakeDeviceAuthServer:
    def handle(self, request: httpx.Request) -> httpx.Response:
        if request.url.path == "/api/device-auth/hmac" and request.method == "POST":
            body = json.loads(request.content)
            if body["deviceId"] == "sensor-001":
                return httpx.Response(
                    200,
                    json={
                        "accessToken": "device-token",
                        "tokenType": "Bearer",
                        "expiresIn": 3600,
                        "deviceId": "sensor-001",
                        "permissions": ["telemetry.publish"],
                    },
                )
            return httpx.Response(401, json={"error": "unknown device"})
        return httpx.Response(404)


@pytest.mark.asyncio
async def test_device_hmac_auth_success():
    server = FakeDeviceAuthServer()
    device = IamDeviceClient(
        "https://localhost:5161", "sensor-001", transport=httpx.MockTransport(server.handle)
    )
    try:
        result = await device.authenticate_with_hmac("shared-secret")
        assert result.success
        assert result.access_token == "device-token"
        assert device.is_authenticated
    finally:
        await device.close()


@pytest.mark.asyncio
async def test_device_hmac_auth_failure_does_not_raise():
    server = FakeDeviceAuthServer()
    device = IamDeviceClient(
        "https://localhost:5161", "unknown-device", transport=httpx.MockTransport(server.handle)
    )
    try:
        result = await device.authenticate_with_hmac("shared-secret")
        assert not result.success
        assert not device.is_authenticated
    finally:
        await device.close()
