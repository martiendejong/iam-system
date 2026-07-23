"""Async real-time telemetry streaming client.

Connects to the IAM System's SignalR telemetry hub (`/hubs/telemetry`)
using `signalrcore`. `signalrcore` itself is callback-based and runs its
websocket loop on a background thread; this client bridges that into
asyncio via an `asyncio.Queue` so callers can consume telemetry with a
normal `async for` loop.
"""

import asyncio
import logging
from typing import Any, AsyncIterator, Callable, Dict, List, Optional

from signalrcore.hub_connection_builder import HubConnectionBuilder

from .exceptions import TelemetryConnectionError

logger = logging.getLogger("iam_sdk.telemetry")

TelemetryCallback = Callable[[Dict[str, Any]], None]


class TelemetryClient:
    """Real-time telemetry client for the IAM System SignalR hub.

    Usage (streaming):
        client = TelemetryClient("https://localhost:5161", access_token)
        await client.connect()
        client.subscribe_to_device("sensor-001")
        async for message in client.stream():
            print(message)

    Usage (callback style):
        client.on_telemetry(lambda msg: print(msg))
        await client.connect()
        client.subscribe_to_tenant("building-hq")
    """

    def __init__(
        self,
        base_url: str,
        access_token: str,
        max_reconnect_attempts: int = 5,
        reconnect_interval_seconds: int = 5,
    ):
        self.base_url = base_url.rstrip("/")
        hub_url = f"{self.base_url}/hubs/telemetry"

        self._connection = (
            HubConnectionBuilder()
            .with_url(
                hub_url,
                options={
                    "headers": {"Authorization": f"Bearer {access_token}"},
                    "verify_ssl": False,
                },
            )
            .with_automatic_reconnect(
                {
                    "type": "raw",
                    "keep_alive_interval": 10,
                    "reconnect_interval": reconnect_interval_seconds,
                    "max_attempts": max_reconnect_attempts,
                }
            )
            .build()
        )

        self._connected = False
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        self._queue: Optional["asyncio.Queue[Dict[str, Any]]"] = None
        self._telemetry_callbacks: List[TelemetryCallback] = []
        self._status_callbacks: List[TelemetryCallback] = []
        self._command_callbacks: List[TelemetryCallback] = []

        self._connection.on_open(self._on_open)
        self._connection.on_close(self._on_close)
        self._connection.on_error(self._on_error)
        self._connection.on("TelemetryReceived", self._dispatch(self._telemetry_callbacks, self._queue_put))
        self._connection.on("DeviceStatusChanged", self._dispatch(self._status_callbacks))
        self._connection.on("DeviceCommand", self._dispatch(self._command_callbacks))

    # ------------------------------------------------------------------
    # Connection lifecycle
    # ------------------------------------------------------------------

    async def connect(self) -> None:
        """Open the SignalR connection (runs the blocking start() in a thread)."""
        self._loop = asyncio.get_running_loop()
        self._queue = asyncio.Queue()
        try:
            await self._loop.run_in_executor(None, self._connection.start)
        except Exception as exc:  # signalrcore raises plain Exception subclasses
            raise TelemetryConnectionError(f"Failed to connect to telemetry hub: {exc}") from exc

    async def disconnect(self) -> None:
        """Close the SignalR connection."""
        if self._loop is not None:
            await self._loop.run_in_executor(None, self._connection.stop)
        self._connected = False

    @property
    def is_connected(self) -> bool:
        return self._connected

    # ------------------------------------------------------------------
    # Subscriptions (client -> server hub methods)
    # ------------------------------------------------------------------

    def subscribe_to_device(self, device_id: str) -> None:
        self._connection.send("SubscribeToDevice", [device_id])

    def unsubscribe_from_device(self, device_id: str) -> None:
        self._connection.send("UnsubscribeFromDevice", [device_id])

    def subscribe_to_tenant(self, tenant_id: str) -> None:
        self._connection.send("SubscribeToTenant", [tenant_id])

    def unsubscribe_from_tenant(self, tenant_id: str) -> None:
        self._connection.send("UnsubscribeFromTenant", [tenant_id])

    def subscribe_to_device_type(self, device_type: str) -> None:
        """Subscribe to all telemetry for a device type.

        Note: the server hub has no matching `UnsubscribeFromDeviceType`
        method -- this subscription can only be dropped by disconnecting.
        """
        self._connection.send("SubscribeToDeviceType", [device_type])

    # ------------------------------------------------------------------
    # Publishing (for edge-gateway / simulator use cases)
    # ------------------------------------------------------------------

    def publish_telemetry(self, message: Dict[str, Any]) -> None:
        """Publish a telemetry message. Device tokens may only publish
        for their own `deviceId` -- the hub rejects mismatches."""
        self._connection.send("PublishTelemetry", [message])

    def report_device_status(self, message: Dict[str, Any]) -> None:
        self._connection.send("ReportDeviceStatus", [message])

    def send_device_command(self, command: Dict[str, Any]) -> None:
        self._connection.send("SendDeviceCommand", [command])

    # ------------------------------------------------------------------
    # Consuming events
    # ------------------------------------------------------------------

    def on_telemetry(self, callback: TelemetryCallback) -> None:
        """Register a callback invoked (on the SignalR thread) for every
        `TelemetryReceived` event."""
        self._telemetry_callbacks.append(callback)

    def on_device_status_changed(self, callback: TelemetryCallback) -> None:
        self._status_callbacks.append(callback)

    def on_device_command(self, callback: TelemetryCallback) -> None:
        self._command_callbacks.append(callback)

    async def stream(self) -> AsyncIterator[Dict[str, Any]]:
        """Async-iterate telemetry messages as they arrive.

        Requires `connect()` to have been called first.
        """
        if self._queue is None:
            raise TelemetryConnectionError("stream() called before connect()")
        while True:
            message = await self._queue.get()
            yield message

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _dispatch(
        self,
        callbacks: List[TelemetryCallback],
        extra: Optional[Callable[[Dict[str, Any]], None]] = None,
    ) -> Callable[[List[Any]], None]:
        def handler(args: List[Any]) -> None:
            message = args[0] if args else {}
            for callback in callbacks:
                try:
                    callback(message)
                except Exception:
                    logger.exception("iam_sdk: telemetry callback raised")
            if extra is not None:
                extra(message)

        return handler

    def _queue_put(self, message: Dict[str, Any]) -> None:
        if self._loop is not None and self._queue is not None:
            self._loop.call_soon_threadsafe(self._queue.put_nowait, message)

    def _on_open(self) -> None:
        self._connected = True
        logger.info("iam_sdk: telemetry connection opened")

    def _on_close(self) -> None:
        self._connected = False
        logger.info("iam_sdk: telemetry connection closed")

    def _on_error(self, error: Any) -> None:
        logger.warning("iam_sdk: telemetry connection error: %r", error)
