import asyncio
import json

import httpx
import pytest

from iam_sdk.device import IamDeviceClient


class FakeMultiDeviceServer:
    """Fake server tracking auth calls per device to prove concurrency
    doesn't cross-contaminate state between separate client instances."""

    def __init__(self):
        self.auth_calls = []

    def handle(self, request: httpx.Request) -> httpx.Response:
        if request.url.path == "/api/device-auth/hmac" and request.method == "POST":
            body = json.loads(request.content)
            self.auth_calls.append(body["deviceId"])
            return httpx.Response(
                200,
                json={
                    "accessToken": f"token-{body['deviceId']}",
                    "tokenType": "Bearer",
                    "expiresIn": 3600,
                    "deviceId": body["deviceId"],
                    "permissions": ["telemetry.publish"],
                },
            )
        if request.url.path == "/api/device-auth/heartbeat" and request.method == "POST":
            return httpx.Response(200, json={"ok": True})
        return httpx.Response(404)


@pytest.mark.asyncio
async def test_concurrent_device_authentication():
    server = FakeMultiDeviceServer()
    device_ids = [f"sensor-{i:03d}" for i in range(10)]
    clients = [
        IamDeviceClient(
            "https://localhost:5161", device_id, transport=httpx.MockTransport(server.handle)
        )
        for device_id in device_ids
    ]

    results = await asyncio.gather(
        *[client.authenticate_with_hmac("shared-secret") for client in clients]
    )

    assert all(result.success for result in results)
    assert sorted(server.auth_calls) == sorted(device_ids)
    assert {result.device_id for result in results} == set(device_ids)

    await asyncio.gather(*[client.close() for client in clients])


@pytest.mark.asyncio
async def test_concurrent_heartbeats_from_same_client():
    server = FakeMultiDeviceServer()
    device = IamDeviceClient(
        "https://localhost:5161", "sensor-001", transport=httpx.MockTransport(server.handle)
    )
    try:
        # Fire 20 heartbeats concurrently; none should raise.
        await asyncio.gather(*[device.heartbeat() for _ in range(20)])
    finally:
        await device.close()
