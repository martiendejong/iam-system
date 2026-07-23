import httpx
import pytest

pd = pytest.importorskip("pandas")

from iam_sdk.admin import IamAdminClient
from iam_sdk.jupyter.display import device_status_table
from iam_sdk.jupyter.query import TelemetryQuery
from iam_sdk.models import Device


class FakeTelemetryServer:
    def handle(self, request: httpx.Request) -> httpx.Response:
        if request.url.path == "/api/telemetry/query" and request.method == "GET":
            return httpx.Response(
                200,
                json={
                    "records": [
                        {"deviceId": "sensor-001", "metricName": "temperature", "numericValue": 21.0, "timestamp": "2026-01-01T00:00:00Z"},
                        {"deviceId": "sensor-001", "metricName": "temperature", "numericValue": 22.0, "timestamp": "2026-01-01T01:00:00Z"},
                    ],
                    "totalCount": 2,
                },
            )
        if request.url.path == "/api/telemetry/aggregate" and request.method == "GET":
            return httpx.Response(
                200,
                json={
                    "metricName": "temperature",
                    "aggregation": "avg",
                    "interval": "1h",
                    "buckets": [
                        {"bucket": "2026-01-01T00:00:00Z", "value": 21.0},
                        {"bucket": "2026-01-01T01:00:00Z", "value": 22.0},
                    ],
                },
            )
        return httpx.Response(404)


@pytest.mark.asyncio
async def test_telemetry_query_to_dataframe():
    admin = IamAdminClient(
        "https://localhost:5161", "admin-token", transport=httpx.MockTransport(FakeTelemetryServer().handle)
    )
    try:
        query = TelemetryQuery(admin)
        frame = await query.query(device_id="sensor-001", metric_name="temperature")
        df = frame.to_dataframe()

        assert isinstance(df, pd.DataFrame)
        assert len(df) == 2
        assert list(df["numericValue"]) == [21.0, 22.0]
    finally:
        await admin.close()


@pytest.mark.asyncio
async def test_telemetry_aggregate_to_dataframe():
    admin = IamAdminClient(
        "https://localhost:5161", "admin-token", transport=httpx.MockTransport(FakeTelemetryServer().handle)
    )
    try:
        query = TelemetryQuery(admin)
        frame = await query.aggregate(metric_name="temperature", device_id="sensor-001")
        df = frame.to_dataframe()

        assert isinstance(df, pd.DataFrame)
        assert len(df) == 2
        assert df.attrs["metric_name"] == "temperature"
        assert list(df["value"]) == [21.0, 22.0]
    finally:
        await admin.close()


def test_device_status_table_returns_dataframe():
    devices = [
        Device(
            id="1",
            device_id="sensor-001",
            name="Lobby Sensor",
            device_type="sensor",
            tenant_name="HQ",
            is_online=True,
            is_active=True,
        ),
        Device(
            id="2",
            device_id="sensor-002",
            name="Roof Sensor",
            device_type="sensor",
            tenant_name="HQ",
            is_online=False,
            is_active=True,
        ),
    ]

    df = device_status_table(devices)

    assert isinstance(df, pd.DataFrame)
    assert list(df["device_id"]) == ["sensor-001", "sensor-002"]
    assert list(df["online"]) == [True, False]
