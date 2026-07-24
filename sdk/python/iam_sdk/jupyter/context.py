"""Inline device provisioning for notebook cells."""

import logging
from typing import Any, Dict, List, Optional, Tuple

from ..admin import IamAdminClient
from ..device import IamDeviceClient
from ..models import DeviceRegistrationResult

logger = logging.getLogger("iam_sdk.jupyter")


class IAMContext:
    """Context manager for provisioning and authenticating IoT devices
    inline from a notebook cell, with automatic cleanup on exit.

    Usage:
        async with IAMContext(base_url, admin_token, tenant_id) as ctx:
            reg, device = await ctx.provision_device("sensor-042", device_type="sensor")
            await device.send_telemetry([...])
        # sensor-042 is deactivated automatically here
    """

    def __init__(
        self,
        base_url: str,
        admin_access_token: str,
        tenant_id: Optional[str] = None,
        cleanup_on_exit: bool = True,
    ):
        self.base_url = base_url
        self.tenant_id = tenant_id
        self.cleanup_on_exit = cleanup_on_exit
        self._admin = IamAdminClient(base_url, admin_access_token)
        self._provisioned: List[Tuple[str, IamDeviceClient]] = []

    async def provision_device(
        self,
        device_id: str,
        device_type: str = "sensor",
        name: Optional[str] = None,
        authentication_method: str = "hmac",
        permissions: Optional[List[str]] = None,
        resource_path: Optional[str] = None,
        metadata: Optional[str] = None,
        tags: Optional[List[str]] = None,
    ) -> Tuple[DeviceRegistrationResult, IamDeviceClient]:
        """Register a device and return an already-authenticated client for it.

        Args:
            device_id: Human-readable device identifier.
            device_type: Type classification (e.g. ``sensor``).
            name: Display name; defaults to `device_id`.
            authentication_method: ``hmac`` (default, auto-authenticates
                using the returned shared secret) or ``certificate``
                (caller must call `authenticate_with_certificate` on the
                returned client themselves).
            permissions: Optional list of permission strings.
            resource_path: Optional resource path; defaults to a path
                scoped under `tenant_id`.
            metadata: Optional JSON metadata string.
            tags: Optional list of tag strings.

        Returns:
            A tuple of (registration result, authenticated device client).
        """
        if not self.tenant_id:
            raise ValueError("tenant_id is required to provision a device")

        registration = await self._admin.register_device(
            device_id=device_id,
            name=name or device_id,
            device_type=device_type,
            tenant_id=self.tenant_id,
            resource_path=resource_path or f"tenants/{self.tenant_id}/devices/{device_id}",
            authentication_method=authentication_method,
            permissions=permissions or [],
            metadata=metadata,
            tags=tags,
        )

        device = IamDeviceClient(self.base_url, device_id)
        if authentication_method == "hmac" and registration.shared_secret:
            await device.authenticate_with_hmac(registration.shared_secret)

        self._provisioned.append((registration.id, device))
        logger.debug("iam_sdk.jupyter: provisioned device %s", device_id)
        return registration, device

    async def __aenter__(self) -> "IAMContext":
        return self

    async def __aexit__(self, *args: Any) -> None:
        if self.cleanup_on_exit:
            for internal_id, device in self._provisioned:
                try:
                    await self._admin.deactivate_device(internal_id)
                except Exception:
                    logger.warning(
                        "iam_sdk.jupyter: failed to deactivate device %s during cleanup",
                        internal_id,
                        exc_info=True,
                    )
                await device.close()
        await self._admin.close()
