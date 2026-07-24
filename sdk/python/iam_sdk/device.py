import hashlib
import hmac
import logging
import time
import uuid
import httpx
from typing import Any, Dict, Optional, List

from .auth import RetryTransport
from .models import (
    DeviceAuthResponse,
    DeviceAuthorizeResponse,
    DeviceClaimsResponse,
    TelemetryRecord,
)

logger = logging.getLogger("iam_sdk")


class IamDeviceClient:
    """IAM System device authentication client for IoT devices.

    Supports certificate-based and HMAC-SHA256 authentication, resource
    authorization, MQTT topic authorization, heartbeat reporting, and
    telemetry ingestion. Transient connection failures and 5xx responses
    are retried with exponential backoff via `RetryTransport`.

    Usage:
        async with IamDeviceClient("https://localhost:5161", "sensor-001") as device:
            auth = await device.authenticate_with_hmac("my-shared-secret")
            result = await device.authorize("building/floor1/room101", "read")
            await device.heartbeat()
    """

    def __init__(
        self,
        base_url: str,
        device_id: str,
        transport: Optional[httpx.AsyncBaseTransport] = None,
    ):
        self.base_url = base_url.rstrip("/")
        self.device_id = device_id
        self._client = httpx.AsyncClient(
            base_url=self.base_url, transport=transport or RetryTransport()
        )
        self._access_token: Optional[str] = None

    # ------------------------------------------------------------------
    # Authentication
    # ------------------------------------------------------------------

    async def authenticate_with_certificate(
        self, certificate_pem: str
    ) -> DeviceAuthResponse:
        """Authenticate using an X.509 certificate PEM.

        The certificate PEM is typically extracted from mTLS by an edge
        gateway and forwarded to the IAM API.

        Args:
            certificate_pem: PEM-encoded certificate string.

        Returns:
            DeviceAuthResponse with access token and permissions.
        """
        response = await self._client.post(
            "/api/device-auth/certificate",
            json={
                "certificatePem": certificate_pem,
                "deviceId": self.device_id,
            },
        )

        if response.status_code == 401:
            data = response.json()
            return DeviceAuthResponse(
                success=False,
                error=data.get("error", "Authentication failed"),
            )

        response.raise_for_status()
        data = response.json()

        self._access_token = data.get("accessToken")
        logger.debug("iam_sdk: device %s authenticated via certificate", self.device_id)

        return DeviceAuthResponse(
            success=True,
            access_token=data.get("accessToken"),
            token_type=data.get("tokenType", "Bearer"),
            expires_in=data.get("expiresIn", 0),
            device_id=data.get("deviceId"),
            permissions=data.get("permissions", []),
        )

    async def authenticate_with_hmac(
        self, shared_secret: str
    ) -> DeviceAuthResponse:
        """Authenticate using HMAC-SHA256 with a shared secret.

        Constructs the password in the format ``timestamp:nonce:hmac``
        where the HMAC is computed over ``device_id:timestamp:nonce``
        using the shared secret.

        Args:
            shared_secret: The shared secret assigned during device
                registration.

        Returns:
            DeviceAuthResponse with access token and permissions.
        """
        timestamp = str(int(time.time()))
        nonce = uuid.uuid4().hex

        # Build the message: "deviceId:timestamp:nonce"
        message = f"{self.device_id}:{timestamp}:{nonce}"

        # Compute HMAC-SHA256
        signature = hmac.new(
            shared_secret.encode("utf-8"),
            message.encode("utf-8"),
            hashlib.sha256,
        ).hexdigest()

        # Password format expected by the API: "timestamp:nonce:hmac"
        password = f"{timestamp}:{nonce}:{signature}"

        response = await self._client.post(
            "/api/device-auth/hmac",
            json={
                "deviceId": self.device_id,
                "password": password,
            },
        )

        if response.status_code == 401:
            data = response.json()
            return DeviceAuthResponse(
                success=False,
                error=data.get("error", "Authentication failed"),
            )

        response.raise_for_status()
        data = response.json()

        self._access_token = data.get("accessToken")
        logger.debug("iam_sdk: device %s authenticated via HMAC", self.device_id)

        return DeviceAuthResponse(
            success=True,
            access_token=data.get("accessToken"),
            token_type=data.get("tokenType", "Bearer"),
            expires_in=data.get("expiresIn", 0),
            device_id=data.get("deviceId"),
            permissions=data.get("permissions", []),
        )

    # ------------------------------------------------------------------
    # Authorization
    # ------------------------------------------------------------------

    async def authorize(
        self, resource: str, action: str
    ) -> DeviceAuthorizeResponse:
        """Check whether this device is allowed to perform an action on a resource.

        Used by edge gateways to verify permissions before forwarding
        requests from devices.

        Args:
            resource: The resource path (e.g. ``building/floor1/room101``).
            action: The action to perform (e.g. ``read``, ``write``).

        Returns:
            DeviceAuthorizeResponse indicating whether access is allowed.
        """
        response = await self._client.post(
            "/api/device-auth/authorize",
            json={
                "deviceId": self.device_id,
                "resource": resource,
                "action": action,
            },
        )
        response.raise_for_status()
        data = response.json()

        return DeviceAuthorizeResponse(
            allowed=data.get("allowed", False),
            matched_permission=data.get("matchedPermission"),
            reason=data.get("reason"),
        )

    async def authorize_mqtt(
        self, topic: str, action: str
    ) -> DeviceAuthorizeResponse:
        """Check whether this device is allowed to publish/subscribe on an MQTT topic.

        Called by MQTT broker auth plugins (e.g. Mosquitto) to validate
        device permissions.

        Args:
            topic: The MQTT topic (e.g. ``devices/sensor-001/telemetry``).
            action: ``publish`` or ``subscribe``.

        Returns:
            DeviceAuthorizeResponse indicating whether access is allowed.
        """
        response = await self._client.post(
            "/api/device-auth/authorize-mqtt",
            json={
                "deviceId": self.device_id,
                "topic": topic,
                "action": action,
            },
        )
        response.raise_for_status()
        data = response.json()

        return DeviceAuthorizeResponse(
            allowed=data.get("allowed", False),
            matched_permission=data.get("matchedPermission"),
            reason=data.get("reason"),
        )

    # ------------------------------------------------------------------
    # Status & Telemetry
    # ------------------------------------------------------------------

    async def heartbeat(self) -> None:
        """Send a heartbeat to report this device is still alive.

        Lightweight endpoint for constrained devices.
        """
        response = await self._client.post(
            "/api/device-auth/heartbeat",
            json={"deviceId": self.device_id},
        )
        response.raise_for_status()

    async def get_claims(self) -> DeviceClaimsResponse:
        """Get device claims for edge gateway policy caching.

        Returns all permissions and metadata for local authorization
        decisions with a recommended cache TTL.

        Returns:
            DeviceClaimsResponse with permissions, metadata, and TTL.
        """
        response = await self._client.get(
            f"/api/device-auth/claims/{self.device_id}",
        )
        response.raise_for_status()
        data = response.json()

        return DeviceClaimsResponse(
            device_id=data.get("deviceId", ""),
            device_type=data.get("deviceType"),
            resource_path=data.get("resourcePath"),
            permissions=data.get("permissions", []),
            metadata=data.get("metadata"),
            cache_ttl_seconds=data.get("cacheTtlSeconds", 300),
        )

    async def send_telemetry(self, records: List[TelemetryRecord]) -> None:
        """Send a batch of telemetry records.

        Uses the batch ingest endpoint for efficiency (up to 1000
        records per request).

        Args:
            records: List of TelemetryRecord instances to ingest.

        Raises:
            httpx.HTTPStatusError: If the server rejects the batch.
        """
        payload = []
        for record in records:
            item: Dict[str, Any] = {
                "deviceId": record.device_id,
                "metricName": record.metric_name,
            }
            if record.numeric_value is not None:
                item["numericValue"] = record.numeric_value
            if record.string_value is not None:
                item["stringValue"] = record.string_value
            if record.json_value is not None:
                item["jsonValue"] = record.json_value
            if record.unit is not None:
                item["unit"] = record.unit
            if record.tenant_id is not None:
                item["tenantId"] = record.tenant_id
            if record.device_type is not None:
                item["deviceType"] = record.device_type
            if record.tags is not None:
                item["tags"] = record.tags
            if record.timestamp is not None:
                item["timestamp"] = record.timestamp.isoformat()
            payload.append(item)

        response = await self._client.post(
            "/api/telemetry/ingest/batch",
            json={"records": payload},
            headers=self._auth_headers,
        )
        response.raise_for_status()
        logger.debug(
            "iam_sdk: device %s sent %d telemetry record(s)",
            self.device_id,
            len(records),
        )

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    @property
    def is_authenticated(self) -> bool:
        """Check whether an access token is currently stored."""
        return self._access_token is not None

    @property
    def _auth_headers(self) -> Dict[str, str]:
        """Return Authorization header if authenticated."""
        if self._access_token:
            return {"Authorization": f"Bearer {self._access_token}"}
        return {}

    async def close(self) -> None:
        """Close the underlying HTTP client."""
        await self._client.aclose()

    async def __aenter__(self) -> "IamDeviceClient":
        return self

    async def __aexit__(self, *args: object) -> None:
        await self.close()
