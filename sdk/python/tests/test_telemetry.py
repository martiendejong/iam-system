import asyncio

import pytest

from iam_sdk import telemetry as telemetry_module
from iam_sdk.telemetry import TelemetryClient


class FakeHubConnection:
    """Stands in for signalrcore's HubConnection: synchronous, no network."""

    def __init__(self):
        self.handlers = {}
        self._open_cb = None
        self._close_cb = None
        self._error_cb = None
        self.sent = []
        self.started = False
        self.start_calls = 0

    def on_open(self, cb):
        self._open_cb = cb

    def on_close(self, cb):
        self._close_cb = cb

    def on_error(self, cb):
        self._error_cb = cb

    def on(self, event, cb):
        self.handlers[event] = cb

    def start(self):
        self.start_calls += 1
        self.started = True
        if self._open_cb:
            self._open_cb()

    def stop(self):
        self.started = False
        if self._close_cb:
            self._close_cb()

    def send(self, method, args):
        self.sent.append((method, args))

    # Test helpers -- simulate the server pushing an event.
    def emit(self, event, payload):
        self.handlers[event]([payload])


class FakeHubConnectionBuilder:
    """Stands in for signalrcore's HubConnectionBuilder fluent API."""

    def __init__(self):
        self.connection = FakeHubConnection()
        self.url = None
        self.options = None
        self.reconnect_config = None

    def with_url(self, url, options=None):
        self.url = url
        self.options = options
        return self

    def with_automatic_reconnect(self, config):
        self.reconnect_config = config
        return self

    def build(self):
        return self.connection


@pytest.fixture
def fake_builder(monkeypatch):
    builder = FakeHubConnectionBuilder()
    monkeypatch.setattr(telemetry_module, "HubConnectionBuilder", lambda: builder)
    return builder


@pytest.mark.asyncio
async def test_connect_sets_hub_url_and_auth_header(fake_builder):
    client = TelemetryClient("https://localhost:5161", "tok-123")
    assert fake_builder.url == "https://localhost:5161/hubs/telemetry"
    assert fake_builder.options["headers"]["Authorization"] == "Bearer tok-123"

    await client.connect()
    assert client.is_connected
    assert fake_builder.connection.start_calls == 1

    await client.disconnect()
    assert not client.is_connected


@pytest.mark.asyncio
async def test_subscribe_sends_expected_hub_methods(fake_builder):
    client = TelemetryClient("https://localhost:5161", "tok-123")
    await client.connect()

    client.subscribe_to_device("sensor-001")
    client.subscribe_to_tenant("building-hq")
    client.subscribe_to_device_type("thermostat")
    client.unsubscribe_from_device("sensor-001")

    assert ("SubscribeToDevice", ["sensor-001"]) in fake_builder.connection.sent
    assert ("SubscribeToTenant", ["building-hq"]) in fake_builder.connection.sent
    assert ("SubscribeToDeviceType", ["thermostat"]) in fake_builder.connection.sent
    assert ("UnsubscribeFromDevice", ["sensor-001"]) in fake_builder.connection.sent

    await client.disconnect()


@pytest.mark.asyncio
async def test_stream_yields_telemetry_received_events(fake_builder):
    client = TelemetryClient("https://localhost:5161", "tok-123")
    await client.connect()

    stream = client.stream()
    payload = {"deviceId": "sensor-001", "metricName": "temperature", "numericValue": 22.5}
    fake_builder.connection.emit("TelemetryReceived", payload)

    message = await asyncio.wait_for(stream.__anext__(), timeout=1)
    assert message == payload

    await client.disconnect()


@pytest.mark.asyncio
async def test_on_telemetry_callback_invoked(fake_builder):
    client = TelemetryClient("https://localhost:5161", "tok-123")
    received = []
    client.on_telemetry(received.append)
    await client.connect()

    payload = {"deviceId": "sensor-002", "metricName": "humidity", "numericValue": 41.0}
    fake_builder.connection.emit("TelemetryReceived", payload)

    # The callback runs synchronously inside emit(), no await needed.
    assert received == [payload]
    await client.disconnect()


@pytest.mark.asyncio
async def test_device_status_and_command_events_dispatch_to_own_callbacks(fake_builder):
    client = TelemetryClient("https://localhost:5161", "tok-123")
    statuses = []
    commands = []
    client.on_device_status_changed(statuses.append)
    client.on_device_command(commands.append)
    await client.connect()

    fake_builder.connection.emit("DeviceStatusChanged", {"deviceId": "sensor-001", "isOnline": True})
    fake_builder.connection.emit("DeviceCommand", {"deviceId": "sensor-001", "commandType": "reboot"})

    assert statuses == [{"deviceId": "sensor-001", "isOnline": True}]
    assert commands == [{"deviceId": "sensor-001", "commandType": "reboot"}]

    await client.disconnect()


@pytest.mark.asyncio
async def test_publish_and_report_send_expected_payloads(fake_builder):
    client = TelemetryClient("https://localhost:5161", "tok-123")
    await client.connect()

    client.publish_telemetry({"DeviceId": "sensor-001", "DataType": "temperature"})
    client.report_device_status({"DeviceId": "sensor-001", "IsOnline": True})
    client.send_device_command({"DeviceId": "sensor-001", "CommandType": "reboot"})

    methods_sent = [m for m, _ in fake_builder.connection.sent]
    assert methods_sent == ["PublishTelemetry", "ReportDeviceStatus", "SendDeviceCommand"]

    await client.disconnect()


@pytest.mark.asyncio
async def test_reconnect_after_disconnect(fake_builder):
    """Simulates a drop + reconnect cycle: disconnect then connect again."""
    client = TelemetryClient("https://localhost:5161", "tok-123")
    await client.connect()
    assert client.is_connected

    await client.disconnect()
    assert not client.is_connected

    await client.connect()
    assert client.is_connected
    assert fake_builder.connection.start_calls == 2

    await client.disconnect()


def test_automatic_reconnect_configured(fake_builder):
    TelemetryClient(
        "https://localhost:5161", "tok-123", max_reconnect_attempts=7, reconnect_interval_seconds=2
    )
    assert fake_builder.reconnect_config == {
        "type": "raw",
        "keep_alive_interval": 10,
        "reconnect_interval": 2,
        "max_attempts": 7,
    }
