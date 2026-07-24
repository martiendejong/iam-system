# IAM System Python SDK

Python SDK for the IAM System -- Identity and Access Management for IoT-enabled
buildings, real-time telemetry streaming, and Jupyter/pandas-based data science
workflows.

## Installation

Using Poetry (this package's own build system):

```bash
poetry install
poetry install --extras jupyter  # adds pandas for iam_sdk.jupyter
```

Or with pip, against a built wheel / editable checkout:

```bash
pip install .
pip install ".[jupyter]"
```

## Quick Start

### User Authentication

```python
import asyncio
from iam_sdk import IamClient

async def main():
    async with IamClient("https://localhost:5161") as client:
        # Login
        response = await client.login("admin@example.com", "Password123!")
        print(f"Logged in as {response.email}")
        print(f"Token: {response.access_token[:20]}...")

        # Get current user profile
        user = await client.get_current_user()
        print(f"User: {user.first_name} {user.last_name}")

        # Refresh token
        refreshed = await client.refresh()
        print(f"New token: {refreshed.access_token[:20]}...")

        # Logout
        await client.logout()
        print(f"Authenticated: {client.is_authenticated}")

asyncio.run(main())
```

### Device Authentication (IoT)

```python
import asyncio
from iam_sdk import IamDeviceClient, TelemetryRecord
from datetime import datetime, timezone

async def main():
    async with IamDeviceClient("https://localhost:5161", "sensor-001") as device:
        # Authenticate with HMAC (constrained devices)
        auth = await device.authenticate_with_hmac("my-shared-secret")
        print(f"Authenticated: {auth.success}")
        print(f"Permissions: {auth.permissions}")

        # Or authenticate with certificate (capable devices)
        # auth = await device.authenticate_with_certificate(cert_pem)

        # Check authorization
        result = await device.authorize("building/floor1/room101", "read")
        print(f"Allowed: {result.allowed}")

        # Check MQTT topic authorization
        mqtt = await device.authorize_mqtt("devices/sensor-001/telemetry", "publish")
        print(f"MQTT allowed: {mqtt.allowed}")

        # Send heartbeat
        await device.heartbeat()

        # Send telemetry data
        await device.send_telemetry([
            TelemetryRecord(
                device_id="sensor-001",
                metric_name="temperature",
                numeric_value=22.5,
                unit="celsius",
                timestamp=datetime.now(timezone.utc),
            ),
            TelemetryRecord(
                device_id="sensor-001",
                metric_name="humidity",
                numeric_value=45.2,
                unit="percent",
            ),
        ])
        print("Telemetry sent")

asyncio.run(main())
```

### Admin Operations

```python
import asyncio
from iam_sdk import IamClient, IamAdminClient

async def main():
    # First, authenticate as admin
    async with IamClient("https://localhost:5161") as auth:
        login = await auth.login("admin@example.com", "Password123!")

    # Then use admin client with the access token
    async with IamAdminClient("https://localhost:5161", login.access_token) as admin:

        # --- Users ---
        users = await admin.list_users(page=1, page_size=10)
        print(f"Total users: {users.total_count}")

        user = await admin.get_user(users.items[0]["id"])
        print(f"User: {user.email}")

        # Create a new user
        result = await admin.create_user(
            email="newuser@example.com",
            password="SecurePass123!",
            first_name="New",
            last_name="User",
        )
        print(f"Created user: {result['userId']}")

        # --- Tenants ---
        tenants = await admin.list_tenants()
        print(f"Root tenants: {len(tenants)}")

        building = await admin.create_tenant(
            name="HQ Building",
            tenant_type="Building",
            metadata={"address": "123 Main St"},
        )
        print(f"Created tenant: {building.name} ({building.id})")

        # Get building structure
        structure = await admin.get_building_structure(building.id)

        # --- Devices ---
        device = await admin.register_device(
            device_id="thermostat-101",
            name="Office Thermostat",
            device_type="thermostat",
            tenant_id=building.id,
            resource_path=f"buildings/{building.id}/devices/thermostat-101",
            authentication_method="hmac",
            permissions=["telemetry.publish", "status.report"],
        )
        print(f"Registered: {device.device_id}, secret: {device.shared_secret}")

        devices = await admin.list_devices(tenant_id=building.id)
        print(f"Devices in building: {len(devices)}")

        stats = await admin.get_device_statistics(tenant_id=building.id)
        print(f"Online: {stats.online_devices}/{stats.total_devices}")

        # --- Roles ---
        roles = await admin.list_roles()
        print(f"Available roles: {[r.name for r in roles]}")

        custom_role = await admin.create_role(
            name="Device Manager",
            description="Can manage IoT devices",
            permissions=["devices.read", "devices.create", "devices.update"],
        )

        # --- Policies ---
        policy = await admin.create_policy(
            name="Allow sensor read",
            tenant_id=building.id,
            resource="buildings/*/sensors/*",
            action="read",
            effect="Allow",
            inheritance_scope="Descendants",
            role_id=custom_role.id,
        )
        print(f"Created policy: {policy.name}")

        # Evaluate access
        evaluation = await admin.evaluate_policy(
            tenant_id=building.id,
            resource="buildings/hq/sensors/temp-001",
            action="read",
        )
        print(f"Access allowed: {evaluation.is_allowed}")

        # Simulate before creating
        simulation = await admin.simulate_policy(
            tenant_id=building.id,
            resource="buildings/*",
            action="write",
            effect="Deny",
            inheritance_scope="Descendants",
        )
        print(f"Would affect {simulation.affected_tenant_count} tenants")

        # --- Telemetry ---
        telemetry = await admin.query_telemetry(
            device_id="sensor-001",
            metric_name="temperature",
            start_time="2026-01-01T00:00:00Z",
            end_time="2026-12-31T23:59:59Z",
        )
        print(f"Telemetry records: {telemetry.total_count}")

        aggregated = await admin.aggregate_telemetry(
            metric_name="temperature",
            device_id="sensor-001",
            aggregation="avg",
            interval="1h",
        )
        print(f"Buckets: {len(aggregated.buckets)}")

        metrics = await admin.get_telemetry_metrics(device_id="sensor-001")
        print(f"Available metrics: {metrics}")

asyncio.run(main())
```

### Real-Time Telemetry Streaming

`TelemetryClient` connects to the IAM System's SignalR telemetry hub
(`/hubs/telemetry`) and bridges it into `asyncio`, so you can consume live
events with a normal `async for` loop:

```python
import asyncio
from iam_sdk import IamClient, TelemetryClient

async def main():
    async with IamClient("https://localhost:5161") as auth:
        login = await auth.login("admin@example.com", "Password123!")

    live = TelemetryClient("https://localhost:5161", login.access_token)
    await live.connect()
    live.subscribe_to_device("sensor-001")
    live.subscribe_to_tenant("building-hq")

    async for message in live.stream():
        print(message)  # {"deviceId": ..., "metricName": ..., "numericValue": ...}

    await live.disconnect()

asyncio.run(main())
```

Callback style is also supported (`on_telemetry`, `on_device_status_changed`,
`on_device_command`), and reconnection after a dropped connection is handled
automatically by `signalrcore`'s built-in reconnect policy
(`max_reconnect_attempts` / `reconnect_interval_seconds` on the constructor).

### Jupyter Notebook Integration

Install the `jupyter` extra (`pip install iam-sdk[jupyter]`) for pandas-based
helpers under `iam_sdk.jupyter`:

- **`IAMContext`** -- provision and authenticate a device inline in a
  notebook cell, with automatic deactivation on exit.
- **`TelemetryQuery`** -- wraps `IamAdminClient.query_telemetry` /
  `aggregate_telemetry` with `.to_dataframe()` for direct pandas output.
- **`device_status_table(devices)`** / **`telemetry_chart(records)`** --
  rich display helpers that render as tables/plots in notebook output.

```python
from iam_sdk.jupyter import IAMContext, TelemetryQuery, device_status_table

async with IAMContext(base_url, admin_token, tenant_id="building-hq") as ctx:
    registration, device = await ctx.provision_device("sensor-042")
    await device.send_telemetry([...])

query = TelemetryQuery(admin)
frame = await query.query(device_id="sensor-001", metric_name="temperature")
df = frame.to_dataframe()
```

See `examples/notebooks/` for runnable examples: `device_provisioning.ipynb`,
`telemetry_analysis.ipynb`, `policy_management.ipynb`.

### Reliability & Observability

- **Automatic token refresh**: `IamClient` proactively refreshes the access
  token ~30 seconds before it expires (`TokenCache`), so long-running scripts
  and notebooks don't need to manage token lifetime manually.
- **Retry with backoff**: every HTTP client (`IamClient`, `IamDeviceClient`,
  `IamAdminClient`) retries connection failures and 5xx responses with
  exponential backoff + jitter via `RetryTransport` (tenacity-backed). 4xx
  responses are never retried.
- **Logging**: the SDK logs through the standard `logging` module under the
  `iam_sdk` logger (and `iam_sdk.telemetry` / `iam_sdk.jupyter` for those
  subsystems) -- configure verbosity the usual way:
  ```python
  import logging
  logging.getLogger("iam_sdk").setLevel(logging.DEBUG)
  ```
- **Typed exceptions**: `IamAuthError` (bad credentials / expired refresh
  token), `IamApiError` (non-auth API failures, carries `status_code` and
  `response_body`), and `TelemetryConnectionError` let callers branch on
  failure type instead of parsing `httpx` exceptions directly.

## API Reference

### IamClient

| Method | Description |
|--------|-------------|
| `login(email, password)` | Authenticate and get access token |
| `refresh()` | Refresh the access token |
| `logout()` | Invalidate tokens |
| `get_current_user()` | Get authenticated user profile |
| `is_authenticated` | Check if token is stored |
| `headers` | Get Authorization header dict |

### IamDeviceClient

| Method | Description |
|--------|-------------|
| `authenticate_with_certificate(pem)` | X.509 certificate auth |
| `authenticate_with_hmac(secret)` | HMAC-SHA256 auth |
| `authorize(resource, action)` | Check device permission |
| `authorize_mqtt(topic, action)` | Check MQTT permission |
| `heartbeat()` | Report device is alive |
| `get_claims()` | Get all permissions for caching |
| `send_telemetry(records)` | Batch ingest telemetry |

### IamAdminClient

| Category | Methods |
|----------|---------|
| **Users** | `list_users`, `get_user`, `create_user`, `deactivate_user`, `activate_user`, `assign_role`, `remove_role` |
| **Devices** | `list_devices`, `list_devices_by_type`, `get_device`, `get_device_by_device_id`, `register_device`, `update_device`, `deactivate_device`, `get_device_statistics` |
| **Tenants** | `list_tenants`, `get_tenant`, `create_tenant`, `update_tenant`, `delete_tenant`, `get_tenant_hierarchy`, `get_building_structure` |
| **Roles** | `list_roles`, `get_role`, `create_role`, `delete_role` |
| **Policies** | `list_policies`, `get_policy`, `create_policy`, `evaluate_policy`, `simulate_policy`, `get_effective_policies`, `delete_policy` |
| **Telemetry** | `query_telemetry`, `aggregate_telemetry`, `get_telemetry_metrics`, `get_telemetry_statistics` |

### TelemetryClient

| Method | Description |
|--------|-------------|
| `connect()` / `disconnect()` | Open/close the SignalR connection |
| `subscribe_to_device(id)` / `unsubscribe_from_device(id)` | Device-scoped telemetry |
| `subscribe_to_tenant(id)` / `unsubscribe_from_tenant(id)` | Tenant-scoped telemetry |
| `subscribe_to_device_type(type)` | Device-type-scoped telemetry (no unsubscribe -- server limitation) |
| `stream()` | Async iterator over incoming `TelemetryReceived` messages |
| `on_telemetry`, `on_device_status_changed`, `on_device_command` | Register callbacks |
| `publish_telemetry`, `report_device_status`, `send_device_command` | Push events (edge-gateway use cases) |

### iam_sdk.jupyter

| Name | Description |
|------|-------------|
| `IAMContext` | Inline device provisioning with automatic cleanup |
| `TelemetryQuery` | `.query()` / `.aggregate()` returning `.to_dataframe()`-capable results |
| `device_status_table(devices)` | pandas DataFrame of device status |
| `telemetry_chart(records)` | Line chart of telemetry records |

## Testing

```bash
poetry install --extras jupyter
poetry run pytest
poetry run mypy
```

## Requirements

- Python 3.10+
- httpx >= 0.25.0
- pydantic >= 2.0.0
- tenacity >= 8.2.0
- signalrcore >= 0.9.5
- pandas >= 2.0.0 (optional, `jupyter` extra)
