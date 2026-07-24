import json

import httpx
import pytest

from iam_sdk.admin import IamAdminClient


class FakeAdminServer:
    def __init__(self):
        self.users = {
            "u1": {"id": "u1", "email": "alice@example.com", "firstName": "Alice", "lastName": "A", "roles": []},
        }
        self.devices = {}
        self.policies = {}
        self._next_policy_id = 1

    def handle(self, request: httpx.Request) -> httpx.Response:
        path = request.url.path
        method = request.method

        if request.headers.get("authorization") != "Bearer admin-token":
            return httpx.Response(401, json={"error": "unauthorized"})

        if path == "/api/users" and method == "GET":
            return httpx.Response(
                200,
                json={
                    "totalCount": len(self.users),
                    "page": 1,
                    "pageSize": 20,
                    "totalPages": 1,
                    "items": list(self.users.values()),
                },
            )

        if path.startswith("/api/users/") and method == "GET":
            user_id = path.rsplit("/", 1)[-1]
            user = self.users.get(user_id)
            if not user:
                return httpx.Response(404)
            return httpx.Response(200, json=user)

        if path == "/api/devices" and method == "POST":
            body = json.loads(request.content)
            internal_id = f"dev-{len(self.devices) + 1}"
            record = {
                "id": internal_id,
                "deviceId": body["deviceId"],
                "name": body["name"],
                "deviceType": body["deviceType"],
                "authenticationMethod": body["authenticationMethod"],
                "tenantId": body["tenantId"],
                "resourcePath": body["resourcePath"],
                "isActive": True,
                "createdAt": "2026-01-01T00:00:00Z",
                "sharedSecret": "s3cr3t" if body["authenticationMethod"] == "hmac" else None,
            }
            self.devices[internal_id] = record
            return httpx.Response(200, json=record)

        if path == f"/api/devices/by-tenant/tenant-1" and method == "GET":
            items = [d for d in self.devices.values() if d["tenantId"] == "tenant-1"]
            return httpx.Response(200, json=items)

        if path == "/api/policies" and method == "POST":
            body = json.loads(request.content)
            policy_id = f"pol-{self._next_policy_id}"
            self._next_policy_id += 1
            record = {
                "id": policy_id,
                "name": body["name"],
                "tenantId": body["tenantId"],
                "resource": body["resource"],
                "action": body["action"],
                "effect": body.get("effect", "Allow"),
                "createdAt": "2026-01-01T00:00:00Z",
            }
            self.policies[policy_id] = record
            return httpx.Response(200, json=record)

        if path == "/api/policies/evaluate" and method == "POST":
            body = json.loads(request.content)
            allowed = any(
                p["resource"] == body["resource"] and p["action"] == body["action"] and p["effect"] == "Allow"
                for p in self.policies.values()
            )
            return httpx.Response(
                200,
                json={"isAllowed": allowed, "reason": "matched" if allowed else "no match", "evaluatedPolicyCount": len(self.policies)},
            )

        return httpx.Response(404)


def make_admin_client(server: FakeAdminServer) -> IamAdminClient:
    return IamAdminClient(
        "https://localhost:5161", "admin-token", transport=httpx.MockTransport(server.handle)
    )


@pytest.mark.asyncio
async def test_list_users():
    server = FakeAdminServer()
    admin = make_admin_client(server)
    try:
        result = await admin.list_users()
        assert result.total_count == 1
        assert result.items[0]["email"] == "alice@example.com"
    finally:
        await admin.close()


@pytest.mark.asyncio
async def test_get_user_not_found_raises_http_status_error():
    server = FakeAdminServer()
    admin = make_admin_client(server)
    try:
        with pytest.raises(httpx.HTTPStatusError):
            await admin.get_user("does-not-exist")
    finally:
        await admin.close()


@pytest.mark.asyncio
async def test_register_device_and_list_by_tenant():
    server = FakeAdminServer()
    admin = make_admin_client(server)
    try:
        registered = await admin.register_device(
            device_id="sensor-001",
            name="Lobby Sensor",
            device_type="sensor",
            tenant_id="tenant-1",
            resource_path="tenants/tenant-1/devices/sensor-001",
            authentication_method="hmac",
        )
        assert registered.shared_secret == "s3cr3t"

        devices = await admin.list_devices(tenant_id="tenant-1")
        assert len(devices) == 1
        assert devices[0].device_id == "sensor-001"
    finally:
        await admin.close()


@pytest.mark.asyncio
async def test_create_policy_and_evaluate():
    server = FakeAdminServer()
    admin = make_admin_client(server)
    try:
        policy = await admin.create_policy(
            name="Allow sensor read",
            tenant_id="tenant-1",
            resource="buildings/hq/sensors/*",
            action="read",
            effect="Allow",
        )
        assert policy.name == "Allow sensor read"

        evaluation = await admin.evaluate_policy(
            tenant_id="tenant-1", resource="buildings/hq/sensors/*", action="read"
        )
        assert evaluation.is_allowed is True

        denied = await admin.evaluate_policy(
            tenant_id="tenant-1", resource="buildings/hq/sensors/*", action="write"
        )
        assert denied.is_allowed is False
    finally:
        await admin.close()
