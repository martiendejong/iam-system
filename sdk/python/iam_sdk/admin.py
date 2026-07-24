import logging

import httpx
from typing import Optional, List, Dict, Any

from .auth import RetryTransport
from .models import (
    Device,
    DeviceRegistrationResult,
    DeviceStatistics,
    User,
    UserListResponse,
    Tenant,
    Role,
    Policy,
    PolicyEvaluationResult,
    PolicySimulationResult,
    TelemetryQueryResult,
    TelemetryAggregationResult,
)

logger = logging.getLogger("iam_sdk")


class IamAdminClient:
    """IAM System admin client for managing users, devices, tenants, and policies.

    Requires a valid access token obtained via IamClient.login(). Transient
    connection failures and 5xx responses are retried with exponential
    backoff via `RetryTransport`.

    Usage:
        async with IamClient("https://localhost:5161") as auth:
            login = await auth.login("admin@example.com", "password")

        async with IamAdminClient("https://localhost:5161", login.access_token) as admin:
            devices = await admin.list_devices(tenant_id="...")
            users = await admin.list_users()
    """

    def __init__(
        self,
        base_url: str,
        access_token: str,
        transport: Optional[httpx.AsyncBaseTransport] = None,
    ):
        self.base_url = base_url.rstrip("/")
        self._client = httpx.AsyncClient(
            base_url=self.base_url,
            headers={"Authorization": f"Bearer {access_token}"},
            transport=transport or RetryTransport(),
        )

    # ==================================================================
    # Devices
    # ==================================================================

    async def list_devices(
        self, tenant_id: Optional[str] = None
    ) -> List[Device]:
        """List devices, optionally filtered by tenant.

        Args:
            tenant_id: Filter by tenant UUID. If None, requires a
                different listing strategy (by-type, etc.).

        Returns:
            List of Device models.
        """
        if tenant_id:
            response = await self._client.get(
                f"/api/devices/by-tenant/{tenant_id}"
            )
        else:
            # Fall back to statistics to discover device counts;
            # the API requires a tenant filter for listing.
            response = await self._client.get(
                "/api/devices/by-tenant/00000000-0000-0000-0000-000000000000"
            )

        response.raise_for_status()
        data = response.json()

        devices = []
        items = data if isinstance(data, list) else data.get("items", data)
        if isinstance(items, list):
            for item in items:
                devices.append(
                    Device(
                        id=str(item.get("id", "")),
                        device_id=item.get("deviceId", ""),
                        name=item.get("name", ""),
                        device_type=item.get("deviceType", ""),
                        authentication_method=item.get(
                            "authenticationMethod", ""
                        ),
                        tenant_id=str(item.get("tenantId", "")),
                        tenant_name=item.get("tenantName"),
                        resource_path=item.get("resourcePath", ""),
                        is_active=item.get("isActive", True),
                        is_online=item.get("isOnline", False),
                        last_seen_at=item.get("lastSeenAt"),
                    )
                )
        return devices

    async def list_devices_by_type(
        self, device_type: str, tenant_id: Optional[str] = None
    ) -> List[Device]:
        """List devices filtered by device type.

        Args:
            device_type: The device type string (e.g. ``sensor``,
                ``thermostat``).
            tenant_id: Optional tenant filter.

        Returns:
            List of Device models.
        """
        params: Dict[str, str] = {}
        if tenant_id:
            params["tenantId"] = tenant_id

        response = await self._client.get(
            f"/api/devices/by-type/{device_type}", params=params
        )
        response.raise_for_status()
        data = response.json()

        devices = []
        items = data if isinstance(data, list) else []
        for item in items:
            devices.append(
                Device(
                    id=str(item.get("id", "")),
                    device_id=item.get("deviceId", ""),
                    name=item.get("name", ""),
                    device_type=item.get("deviceType", ""),
                    is_active=item.get("isActive", True),
                    is_online=item.get("isOnline", False),
                    last_seen_at=item.get("lastSeenAt"),
                    resource_path=item.get("resourcePath", ""),
                    tenant_id=str(item.get("tenantId", "")),
                    tenant_name=item.get("tenantName"),
                )
            )
        return devices

    async def get_device(self, device_id: str) -> Device:
        """Get a device by its internal UUID.

        Args:
            device_id: The device's internal GUID.

        Returns:
            Device model.

        Raises:
            httpx.HTTPStatusError: If the device is not found (404).
        """
        response = await self._client.get(f"/api/devices/{device_id}")
        response.raise_for_status()
        item = response.json()

        return Device(
            id=str(item.get("id", "")),
            device_id=item.get("deviceId", ""),
            name=item.get("name", ""),
            device_type=item.get("deviceType", ""),
            authentication_method=item.get("authenticationMethod", ""),
            tenant_id=str(item.get("tenantId", "")),
            tenant_name=item.get("tenantName"),
            resource_path=item.get("resourcePath", ""),
            is_active=item.get("isActive", True),
            is_online=item.get("isOnline", False),
            last_seen_at=item.get("lastSeenAt"),
            last_authenticated_at=item.get("lastAuthenticatedAt"),
            is_provisioned=item.get("isProvisioned"),
            provisioned_at=item.get("provisionedAt"),
            metadata=item.get("metadata"),
            tags=item.get("tags"),
            created_at=item.get("createdAt"),
            updated_at=item.get("updatedAt"),
        )

    async def get_device_by_device_id(self, device_id: str) -> Device:
        """Get a device by its human-readable device ID string.

        Args:
            device_id: The human-readable device ID (e.g. ``sensor-001``).

        Returns:
            Device model.
        """
        response = await self._client.get(
            f"/api/devices/by-device-id/{device_id}"
        )
        response.raise_for_status()
        item = response.json()

        return Device(
            id=str(item.get("id", "")),
            device_id=item.get("deviceId", ""),
            name=item.get("name", ""),
            device_type=item.get("deviceType", ""),
            authentication_method=item.get("authenticationMethod", ""),
            tenant_id=str(item.get("tenantId", "")),
            tenant_name=item.get("tenantName"),
            resource_path=item.get("resourcePath", ""),
            is_active=item.get("isActive", True),
            is_online=item.get("isOnline", False),
            last_seen_at=item.get("lastSeenAt"),
            metadata=item.get("metadata"),
            tags=item.get("tags"),
            created_at=item.get("createdAt"),
        )

    async def register_device(
        self,
        device_id: str,
        name: str,
        device_type: str,
        tenant_id: str,
        resource_path: str,
        authentication_method: str = "certificate",
        permissions: Optional[List[str]] = None,
        metadata: Optional[str] = None,
        tags: Optional[List[str]] = None,
    ) -> DeviceRegistrationResult:
        """Register a new IoT device.

        Args:
            device_id: Human-readable device identifier.
            name: Display name for the device.
            device_type: Type classification (e.g. ``sensor``).
            tenant_id: Tenant UUID to assign the device to.
            resource_path: Resource path for authorization.
            authentication_method: ``certificate`` or ``hmac``.
            permissions: Optional list of permission strings.
            metadata: Optional JSON metadata string.
            tags: Optional list of tag strings.

        Returns:
            DeviceRegistrationResult. For HMAC devices, includes the
            ``shared_secret`` (returned only once).
        """
        payload: Dict[str, Any] = {
            "deviceId": device_id,
            "name": name,
            "deviceType": device_type,
            "tenantId": tenant_id,
            "resourcePath": resource_path,
            "authenticationMethod": authentication_method,
            "permissions": permissions or [],
        }
        if metadata is not None:
            payload["metadata"] = metadata
        if tags is not None:
            payload["tags"] = tags

        response = await self._client.post("/api/devices", json=payload)
        response.raise_for_status()
        data = response.json()

        return DeviceRegistrationResult(
            id=str(data.get("id", "")),
            device_id=data.get("deviceId", ""),
            name=data.get("name", ""),
            device_type=data.get("deviceType", ""),
            authentication_method=data.get("authenticationMethod", ""),
            tenant_id=str(data.get("tenantId", "")),
            resource_path=data.get("resourcePath", ""),
            is_active=data.get("isActive", True),
            created_at=data.get("createdAt"),
            shared_secret=data.get("sharedSecret"),
        )

    async def update_device(
        self, device_id: str, **kwargs: Any
    ) -> Dict[str, Any]:
        """Update a device by its internal UUID.

        Args:
            device_id: The device's internal GUID.
            **kwargs: Fields to update (name, deviceType, resourcePath,
                permissions, metadata, tags, isActive).

        Returns:
            Dict with the updated device fields.
        """
        payload: Dict[str, Any] = {}
        field_map = {
            "name": "name",
            "device_type": "deviceType",
            "resource_path": "resourcePath",
            "permissions": "permissions",
            "metadata": "metadata",
            "tags": "tags",
            "is_active": "isActive",
        }
        for py_key, api_key in field_map.items():
            if py_key in kwargs:
                payload[api_key] = kwargs[py_key]

        response = await self._client.put(
            f"/api/devices/{device_id}", json=payload
        )
        response.raise_for_status()
        result: Dict[str, Any] = response.json()
        return result

    async def deactivate_device(self, device_id: str) -> None:
        """Deactivate a device and revoke all its certificates.

        Args:
            device_id: The device's internal GUID.

        Raises:
            httpx.HTTPStatusError: If the device is not found (404).
        """
        response = await self._client.post(
            f"/api/devices/{device_id}/deactivate"
        )
        response.raise_for_status()

    async def get_device_statistics(
        self, tenant_id: Optional[str] = None
    ) -> DeviceStatistics:
        """Get device statistics, optionally filtered by tenant.

        Returns:
            DeviceStatistics with counts by status and type.
        """
        params: Dict[str, str] = {}
        if tenant_id:
            params["tenantId"] = tenant_id

        response = await self._client.get(
            "/api/devices/statistics", params=params
        )
        response.raise_for_status()
        data = response.json()

        return DeviceStatistics(
            total_devices=data.get("totalDevices", 0),
            active_devices=data.get("activeDevices", 0),
            online_devices=data.get("onlineDevices", 0),
            certificate_devices=data.get("certificateDevices", 0),
            hmac_devices=data.get("hmacDevices", 0),
            devices_by_type=data.get("devicesByType", {}),
        )

    # ==================================================================
    # Users
    # ==================================================================

    async def list_users(
        self, page: int = 1, page_size: int = 20
    ) -> UserListResponse:
        """List all users (SuperAdmin only).

        Args:
            page: Page number (1-based).
            page_size: Number of items per page (max 100).

        Returns:
            UserListResponse with paginated user list.
        """
        response = await self._client.get(
            "/api/users",
            params={"page": page, "pageSize": page_size},
        )
        response.raise_for_status()
        data = response.json()

        return UserListResponse(
            total_count=data.get("totalCount", 0),
            page=data.get("page", 1),
            page_size=data.get("pageSize", 20),
            total_pages=data.get("totalPages", 0),
            items=data.get("items", []),
        )

    async def get_user(self, user_id: str) -> User:
        """Get a specific user by UUID.

        Args:
            user_id: The user's GUID.

        Returns:
            User model.

        Raises:
            httpx.HTTPStatusError: If the user is not found (404).
        """
        response = await self._client.get(f"/api/users/{user_id}")
        response.raise_for_status()
        data = response.json()

        return User(
            id=str(data.get("id", "")),
            email=data.get("email", ""),
            first_name=data.get("firstName"),
            last_name=data.get("lastName"),
            email_confirmed=data.get("emailConfirmed"),
            two_factor_enabled=data.get("twoFactorEnabled"),
            is_active=data.get("isActive", True),
            created_at=data.get("createdAt"),
            last_login_at=data.get("lastLoginAt"),
            roles=data.get("roles", []),
        )

    async def create_user(
        self,
        email: str,
        password: str,
        first_name: str,
        last_name: str,
    ) -> Dict[str, Any]:
        """Register a new user via the auth/register endpoint.

        Args:
            email: Email address for the new user.
            password: Password for the new user.
            first_name: First name.
            last_name: Last name.

        Returns:
            Dict with ``message`` and ``userId``.
        """
        response = await self._client.post(
            "/api/auth/register",
            json={
                "email": email,
                "password": password,
                "firstName": first_name,
                "lastName": last_name,
            },
        )
        response.raise_for_status()
        result: Dict[str, Any] = response.json()
        return result

    async def deactivate_user(self, user_id: str) -> None:
        """Deactivate a user (SuperAdmin only).

        Revokes all refresh tokens for the user.

        Args:
            user_id: The user's GUID.
        """
        response = await self._client.post(
            f"/api/users/{user_id}/deactivate"
        )
        response.raise_for_status()

    async def activate_user(self, user_id: str) -> None:
        """Reactivate a user (SuperAdmin only).

        Args:
            user_id: The user's GUID.
        """
        response = await self._client.post(
            f"/api/users/{user_id}/activate"
        )
        response.raise_for_status()

    async def assign_role(
        self,
        user_id: str,
        role_id: str,
        tenant_id: Optional[str] = None,
        expires_at: Optional[str] = None,
    ) -> Dict[str, Any]:
        """Assign a role to a user (SuperAdmin only).

        Args:
            user_id: The user's GUID.
            role_id: The role's GUID.
            tenant_id: Optional tenant scope for the role.
            expires_at: Optional ISO 8601 expiration timestamp.

        Returns:
            Dict with role assignment details.
        """
        payload: Dict[str, Any] = {"roleId": role_id}
        if tenant_id:
            payload["tenantId"] = tenant_id
        if expires_at:
            payload["expiresAt"] = expires_at

        response = await self._client.post(
            f"/api/users/{user_id}/roles", json=payload
        )
        response.raise_for_status()
        result: Dict[str, Any] = response.json()
        return result

    async def remove_role(self, user_id: str, role_id: str) -> None:
        """Remove a role from a user (SuperAdmin only).

        Args:
            user_id: The user's GUID.
            role_id: The role's GUID.
        """
        response = await self._client.delete(
            f"/api/users/{user_id}/roles/{role_id}"
        )
        response.raise_for_status()

    # ==================================================================
    # Tenants
    # ==================================================================

    async def list_tenants(
        self,
        parent_id: Optional[str] = None,
        tenant_type: Optional[str] = None,
    ) -> List[Tenant]:
        """List tenants with optional filters.

        Args:
            parent_id: Filter by parent tenant UUID.
            tenant_type: Filter by type (e.g. ``Building``, ``Floor``).

        Returns:
            List of Tenant models.
        """
        params: Dict[str, str] = {}
        if parent_id:
            params["parentId"] = parent_id
        if tenant_type:
            params["type"] = tenant_type

        response = await self._client.get("/api/tenants", params=params)
        response.raise_for_status()
        data = response.json()

        tenants = []
        items = data if isinstance(data, list) else data.get("items", data)
        if isinstance(items, list):
            for item in items:
                tenants.append(
                    Tenant(
                        id=str(item.get("id", "")),
                        name=item.get("name", ""),
                        type=item.get("type"),
                        parent_tenant_id=(
                            str(item["parentTenantId"])
                            if item.get("parentTenantId")
                            else None
                        ),
                        parent_tenant_name=item.get("parentTenantName"),
                        child_count=item.get("childCount", 0),
                        metadata=item.get("metadata"),
                        settings=item.get("settings"),
                        is_active=item.get("isActive", True),
                        created_at=item.get("createdAt"),
                    )
                )
        return tenants

    async def get_tenant(self, tenant_id: str) -> Tenant:
        """Get a specific tenant by UUID.

        Args:
            tenant_id: The tenant's GUID.

        Returns:
            Tenant model.
        """
        response = await self._client.get(f"/api/tenants/{tenant_id}")
        response.raise_for_status()
        item = response.json()

        return Tenant(
            id=str(item.get("id", "")),
            name=item.get("name", ""),
            type=item.get("type"),
            parent_tenant_id=(
                str(item["parentTenantId"])
                if item.get("parentTenantId")
                else None
            ),
            parent_tenant_name=item.get("parentTenantName"),
            child_count=item.get("childCount", 0),
            metadata=item.get("metadata"),
            settings=item.get("settings"),
            is_active=item.get("isActive", True),
            created_at=item.get("createdAt"),
            updated_at=item.get("updatedAt"),
        )

    async def create_tenant(
        self,
        name: str,
        tenant_type: str,
        parent_tenant_id: Optional[str] = None,
        metadata: Optional[Dict[str, Any]] = None,
        settings: Optional[Dict[str, Any]] = None,
    ) -> Tenant:
        """Create a new tenant (Building, Floor, Room, etc.).

        Args:
            name: Tenant display name.
            tenant_type: Tenant type string.
            parent_tenant_id: Optional parent tenant UUID.
            metadata: Optional metadata dictionary.
            settings: Optional settings dictionary.

        Returns:
            The newly created Tenant.
        """
        payload: Dict[str, Any] = {
            "name": name,
            "type": tenant_type,
        }
        if parent_tenant_id:
            payload["parentTenantId"] = parent_tenant_id
        if metadata:
            payload["metadata"] = metadata
        if settings:
            payload["settings"] = settings

        response = await self._client.post("/api/tenants", json=payload)
        response.raise_for_status()
        item = response.json()

        return Tenant(
            id=str(item.get("id", "")),
            name=item.get("name", ""),
            type=item.get("type"),
            parent_tenant_id=(
                str(item["parentTenantId"])
                if item.get("parentTenantId")
                else None
            ),
            metadata=item.get("metadata"),
            settings=item.get("settings"),
            is_active=item.get("isActive", True),
            created_at=item.get("createdAt"),
        )

    async def update_tenant(
        self, tenant_id: str, **kwargs: Any
    ) -> Dict[str, Any]:
        """Update a tenant.

        Args:
            tenant_id: The tenant's GUID.
            **kwargs: Fields to update (name, type, metadata, settings,
                is_active).

        Returns:
            Dict with updated tenant fields.
        """
        payload: Dict[str, Any] = {}
        field_map = {
            "name": "name",
            "tenant_type": "type",
            "metadata": "metadata",
            "settings": "settings",
            "is_active": "isActive",
        }
        for py_key, api_key in field_map.items():
            if py_key in kwargs:
                payload[api_key] = kwargs[py_key]

        response = await self._client.put(
            f"/api/tenants/{tenant_id}", json=payload
        )
        response.raise_for_status()
        result: Dict[str, Any] = response.json()
        return result

    async def delete_tenant(self, tenant_id: str) -> None:
        """Delete a tenant (only if it has no children or role assignments).

        Args:
            tenant_id: The tenant's GUID.

        Raises:
            httpx.HTTPStatusError: 400 if tenant has children or assignments.
        """
        response = await self._client.delete(f"/api/tenants/{tenant_id}")
        response.raise_for_status()

    async def get_tenant_hierarchy(
        self, tenant_id: str
    ) -> Dict[str, Any]:
        """Get a tenant with its full child hierarchy (up to 3 levels).

        Args:
            tenant_id: The root tenant GUID.

        Returns:
            Nested dict with children at each level.
        """
        response = await self._client.get(
            f"/api/tenants/{tenant_id}/hierarchy"
        )
        response.raise_for_status()
        result: Dict[str, Any] = response.json()
        return result

    async def get_building_structure(
        self, building_id: str
    ) -> Dict[str, Any]:
        """Get a building structure (Building -> Floors -> Rooms).

        Args:
            building_id: The building tenant GUID.

        Returns:
            Nested dict with floors and rooms.
        """
        response = await self._client.get(
            f"/api/tenants/buildings/{building_id}/structure"
        )
        response.raise_for_status()
        result: Dict[str, Any] = response.json()
        return result

    # ==================================================================
    # Roles
    # ==================================================================

    async def list_roles(self) -> List[Role]:
        """List all roles.

        Returns:
            List of Role models.
        """
        response = await self._client.get("/api/roles")
        response.raise_for_status()
        data = response.json()

        roles = []
        items = data if isinstance(data, list) else data.get("items", data)
        if isinstance(items, list):
            for item in items:
                roles.append(
                    Role(
                        id=str(item.get("id", "")),
                        name=item.get("name", ""),
                        description=item.get("description"),
                        is_system_role=item.get("isSystemRole", False),
                        tenant_id=(
                            str(item["tenantId"])
                            if item.get("tenantId")
                            else None
                        ),
                        permissions=item.get("permissions"),
                        created_at=item.get("createdAt"),
                    )
                )
        return roles

    async def get_role(self, role_id: str) -> Role:
        """Get a specific role by UUID.

        Args:
            role_id: The role's GUID.

        Returns:
            Role model.
        """
        response = await self._client.get(f"/api/roles/{role_id}")
        response.raise_for_status()
        item = response.json()

        return Role(
            id=str(item.get("id", "")),
            name=item.get("name", ""),
            description=item.get("description"),
            is_system_role=item.get("isSystemRole", False),
            tenant_id=(
                str(item["tenantId"]) if item.get("tenantId") else None
            ),
            permissions=item.get("permissions"),
            created_at=item.get("createdAt"),
            user_count=item.get("userCount"),
        )

    async def create_role(
        self,
        name: str,
        description: Optional[str] = None,
        tenant_id: Optional[str] = None,
        permissions: Optional[List[str]] = None,
    ) -> Role:
        """Create a custom role.

        Args:
            name: Role name.
            description: Optional description.
            tenant_id: Optional tenant scope.
            permissions: Optional list of permission strings.

        Returns:
            The newly created Role.
        """
        payload: Dict[str, Any] = {"name": name}
        if description:
            payload["description"] = description
        if tenant_id:
            payload["tenantId"] = tenant_id
        if permissions:
            payload["permissions"] = permissions

        response = await self._client.post("/api/roles", json=payload)
        response.raise_for_status()
        item = response.json()

        return Role(
            id=str(item.get("id", "")),
            name=item.get("name", ""),
            description=item.get("description"),
            is_system_role=item.get("isSystemRole", False),
            tenant_id=(
                str(item["tenantId"]) if item.get("tenantId") else None
            ),
            permissions=item.get("permissions"),
            created_at=item.get("createdAt"),
        )

    async def delete_role(self, role_id: str) -> None:
        """Delete a custom role (SuperAdmin only, system roles cannot be deleted).

        Args:
            role_id: The role's GUID.
        """
        response = await self._client.delete(f"/api/roles/{role_id}")
        response.raise_for_status()

    # ==================================================================
    # Policies
    # ==================================================================

    async def list_policies(
        self,
        tenant_id: Optional[str] = None,
        role_id: Optional[str] = None,
        user_id: Optional[str] = None,
        resource: Optional[str] = None,
        is_active: Optional[bool] = True,
    ) -> List[Policy]:
        """List policies with optional filters.

        Args:
            tenant_id: Filter by tenant UUID.
            role_id: Filter by role UUID.
            user_id: Filter by user UUID.
            resource: Filter by resource path prefix.
            is_active: Filter by active status (default True).

        Returns:
            List of Policy models.
        """
        params: Dict[str, Any] = {}
        if tenant_id:
            params["tenantId"] = tenant_id
        if role_id:
            params["roleId"] = role_id
        if user_id:
            params["userId"] = user_id
        if resource:
            params["resource"] = resource
        if is_active is not None:
            params["isActive"] = str(is_active).lower()

        response = await self._client.get("/api/policies", params=params)
        response.raise_for_status()
        data = response.json()

        policies = []
        items = data if isinstance(data, list) else data.get("items", data)
        if isinstance(items, list):
            for item in items:
                policies.append(
                    Policy(
                        id=str(item.get("id", "")),
                        name=item.get("name", ""),
                        description=item.get("description"),
                        tenant_id=(
                            str(item["tenantId"])
                            if item.get("tenantId")
                            else None
                        ),
                        tenant_name=item.get("tenantName"),
                        inheritance_scope=item.get(
                            "inheritanceScope", "Self"
                        ),
                        role_id=(
                            str(item["roleId"])
                            if item.get("roleId")
                            else None
                        ),
                        role_name=item.get("roleName"),
                        user_id=(
                            str(item["userId"])
                            if item.get("userId")
                            else None
                        ),
                        resource=item.get("resource", ""),
                        action=item.get("action", ""),
                        effect=item.get("effect", "Allow"),
                        priority=item.get("priority", 0),
                        time_constraints=item.get("timeConstraints"),
                        conditions=item.get("conditions"),
                        expires_at=item.get("expiresAt"),
                        is_active=item.get("isActive", True),
                        created_at=item.get("createdAt"),
                    )
                )
        return policies

    async def get_policy(self, policy_id: str) -> Policy:
        """Get a specific policy by UUID.

        Args:
            policy_id: The policy's GUID.

        Returns:
            Policy model.
        """
        response = await self._client.get(f"/api/policies/{policy_id}")
        response.raise_for_status()
        item = response.json()

        return Policy(
            id=str(item.get("id", "")),
            name=item.get("name", ""),
            description=item.get("description"),
            tenant_id=(
                str(item["tenantId"]) if item.get("tenantId") else None
            ),
            tenant_name=item.get("tenantName"),
            inheritance_scope=item.get("inheritanceScope", "Self"),
            role_id=(
                str(item["roleId"]) if item.get("roleId") else None
            ),
            role_name=item.get("roleName"),
            user_id=(
                str(item["userId"]) if item.get("userId") else None
            ),
            resource=item.get("resource", ""),
            action=item.get("action", ""),
            effect=item.get("effect", "Allow"),
            priority=item.get("priority", 0),
            time_constraints=item.get("timeConstraints"),
            conditions=item.get("conditions"),
            expires_at=item.get("expiresAt"),
            is_active=item.get("isActive", True),
            created_at=item.get("createdAt"),
        )

    async def create_policy(
        self,
        name: str,
        tenant_id: str,
        resource: str,
        action: str,
        effect: str = "Allow",
        description: Optional[str] = None,
        inheritance_scope: str = "Self",
        role_id: Optional[str] = None,
        user_id: Optional[str] = None,
        priority: int = 0,
        time_constraints: Optional[Dict[str, Any]] = None,
        conditions: Optional[Dict[str, Any]] = None,
        expires_at: Optional[str] = None,
    ) -> Policy:
        """Create a new policy.

        Args:
            name: Policy name.
            tenant_id: Tenant UUID to scope the policy.
            resource: Resource path the policy applies to.
            action: Action the policy governs.
            effect: ``Allow`` or ``Deny``.
            description: Optional description.
            inheritance_scope: ``Self``, ``Children``, or ``Descendants``.
            role_id: Optional role UUID the policy applies to.
            user_id: Optional user UUID the policy applies to.
            priority: Priority (higher = evaluated first).
            time_constraints: Optional time constraint dict.
            conditions: Optional conditions dict.
            expires_at: Optional ISO 8601 expiration.

        Returns:
            The newly created Policy.
        """
        payload: Dict[str, Any] = {
            "name": name,
            "tenantId": tenant_id,
            "resource": resource,
            "action": action,
            "effect": effect,
            "inheritanceScope": inheritance_scope,
            "priority": priority,
        }
        if description:
            payload["description"] = description
        if role_id:
            payload["roleId"] = role_id
        if user_id:
            payload["userId"] = user_id
        if time_constraints:
            payload["timeConstraints"] = time_constraints
        if conditions:
            payload["conditions"] = conditions
        if expires_at:
            payload["expiresAt"] = expires_at

        response = await self._client.post("/api/policies", json=payload)
        response.raise_for_status()
        item = response.json()

        return Policy(
            id=str(item.get("id", "")),
            name=item.get("name", ""),
            description=item.get("description"),
            tenant_id=(
                str(item["tenantId"]) if item.get("tenantId") else None
            ),
            resource=item.get("resource", ""),
            action=item.get("action", ""),
            effect=item.get("effect", "Allow"),
            created_at=item.get("createdAt"),
        )

    async def evaluate_policy(
        self,
        tenant_id: str,
        resource: str,
        action: str,
        device_id: Optional[str] = None,
        device_health: Optional[str] = None,
        location: Optional[str] = None,
        custom_attributes: Optional[Dict[str, Any]] = None,
    ) -> PolicyEvaluationResult:
        """Evaluate policy access for the current authenticated user.

        Args:
            tenant_id: Tenant context UUID.
            resource: Resource path to check.
            action: Action to check.
            device_id: Optional device context.
            device_health: Optional device health status.
            location: Optional location context.
            custom_attributes: Optional additional attributes.

        Returns:
            PolicyEvaluationResult with allow/deny decision.
        """
        payload: Dict[str, Any] = {
            "tenantId": tenant_id,
            "resource": resource,
            "action": action,
        }
        if device_id:
            payload["deviceId"] = device_id
        if device_health:
            payload["deviceHealth"] = device_health
        if location:
            payload["location"] = location
        if custom_attributes:
            payload["customAttributes"] = custom_attributes

        response = await self._client.post(
            "/api/policies/evaluate", json=payload
        )
        response.raise_for_status()
        data = response.json()

        return PolicyEvaluationResult(
            is_allowed=data.get("isAllowed", False),
            reason=data.get("reason"),
            matched_policy=data.get("matchedPolicy"),
            evaluated_policy_count=data.get("evaluatedPolicyCount", 0),
            evaluation_time_ms=data.get("evaluationTimeMs"),
        )

    async def simulate_policy(
        self,
        tenant_id: str,
        resource: str,
        action: str,
        effect: str,
        inheritance_scope: str = "Self",
        role_id: Optional[str] = None,
        user_id: Optional[str] = None,
    ) -> PolicySimulationResult:
        """Simulate the impact of a new policy before creating it.

        Args:
            tenant_id: Tenant UUID.
            resource: Resource path.
            action: Action.
            effect: ``Allow`` or ``Deny``.
            inheritance_scope: Scope for inheritance simulation.
            role_id: Optional role UUID.
            user_id: Optional user UUID.

        Returns:
            PolicySimulationResult with affected counts and summary.
        """
        payload: Dict[str, Any] = {
            "tenantId": tenant_id,
            "resource": resource,
            "action": action,
            "effect": effect,
            "inheritanceScope": inheritance_scope,
        }
        if role_id:
            payload["roleId"] = role_id
        if user_id:
            payload["userId"] = user_id

        response = await self._client.post(
            "/api/policies/simulate", json=payload
        )
        response.raise_for_status()
        data = response.json()

        return PolicySimulationResult(
            affected_tenant_count=data.get("affectedTenantCount", 0),
            affected_user_count=data.get("affectedUserCount", 0),
            summary=data.get("summary"),
            affected_tenants=[
                str(t) for t in data.get("affectedTenants", [])
            ],
        )

    async def get_effective_policies(
        self, tenant_id: str
    ) -> List[Dict[str, Any]]:
        """Get effective policies for a tenant, including inherited ones.

        Args:
            tenant_id: The tenant UUID.

        Returns:
            List of policy dicts with an ``isInherited`` flag.
        """
        response = await self._client.get(
            f"/api/policies/tenant/{tenant_id}/effective"
        )
        response.raise_for_status()
        data = response.json()
        return data if isinstance(data, list) else []

    async def delete_policy(self, policy_id: str) -> None:
        """Delete a policy (SuperAdmin only).

        Args:
            policy_id: The policy's GUID.

        Raises:
            httpx.HTTPStatusError: 400 if the policy is inherited by
                other policies.
        """
        response = await self._client.delete(f"/api/policies/{policy_id}")
        response.raise_for_status()

    # ==================================================================
    # Telemetry
    # ==================================================================

    async def query_telemetry(
        self,
        device_id: Optional[str] = None,
        metric_name: Optional[str] = None,
        tenant_id: Optional[str] = None,
        start_time: Optional[str] = None,
        end_time: Optional[str] = None,
        limit: int = 1000,
        order_by: str = "timestamp_desc",
    ) -> TelemetryQueryResult:
        """Query telemetry records with filters.

        Args:
            device_id: Filter by device ID.
            metric_name: Filter by metric name.
            tenant_id: Filter by tenant UUID.
            start_time: ISO 8601 start time (default: last 24h).
            end_time: ISO 8601 end time (default: now).
            limit: Max records to return (default 1000).
            order_by: Sort order (``timestamp_desc`` or ``timestamp_asc``).

        Returns:
            TelemetryQueryResult with records and metadata.
        """
        params: Dict[str, Any] = {"limit": limit, "orderBy": order_by}
        if device_id:
            params["deviceId"] = device_id
        if metric_name:
            params["metricName"] = metric_name
        if tenant_id:
            params["tenantId"] = tenant_id
        if start_time:
            params["startTime"] = start_time
        if end_time:
            params["endTime"] = end_time

        response = await self._client.get(
            "/api/telemetry/query", params=params
        )
        response.raise_for_status()
        data = response.json()

        return TelemetryQueryResult(
            records=data.get("records", []),
            total_count=data.get("totalCount", 0),
            earliest_timestamp=data.get("earliestTimestamp"),
            latest_timestamp=data.get("latestTimestamp"),
        )

    async def aggregate_telemetry(
        self,
        metric_name: str,
        device_id: Optional[str] = None,
        tenant_id: Optional[str] = None,
        aggregation: str = "avg",
        interval: str = "1h",
        start_time: Optional[str] = None,
        end_time: Optional[str] = None,
        group_by: Optional[str] = None,
    ) -> TelemetryAggregationResult:
        """Get aggregated telemetry data over time intervals.

        Args:
            metric_name: The metric to aggregate.
            device_id: Optional device filter.
            tenant_id: Optional tenant filter.
            aggregation: ``avg``, ``min``, ``max``, ``sum``, or ``count``.
            interval: ``1m``, ``5m``, ``15m``, ``1h``, or ``1d``.
            start_time: ISO 8601 start time.
            end_time: ISO 8601 end time.
            group_by: Optional group key (``device_id``, ``device_type``).

        Returns:
            TelemetryAggregationResult with time-bucketed values.
        """
        params: Dict[str, Any] = {
            "metricName": metric_name,
            "aggregation": aggregation,
            "interval": interval,
        }
        if device_id:
            params["deviceId"] = device_id
        if tenant_id:
            params["tenantId"] = tenant_id
        if start_time:
            params["startTime"] = start_time
        if end_time:
            params["endTime"] = end_time
        if group_by:
            params["groupBy"] = group_by

        response = await self._client.get(
            "/api/telemetry/aggregate", params=params
        )
        response.raise_for_status()
        data = response.json()

        return TelemetryAggregationResult(
            metric_name=data.get("metricName", metric_name),
            aggregation=data.get("aggregation", aggregation),
            interval=data.get("interval", interval),
            buckets=data.get("buckets", []),
        )

    async def get_telemetry_metrics(
        self, device_id: Optional[str] = None
    ) -> List[str]:
        """List available metric names.

        Args:
            device_id: Optional filter by device.

        Returns:
            List of metric name strings.
        """
        params: Dict[str, str] = {}
        if device_id:
            params["deviceId"] = device_id

        response = await self._client.get(
            "/api/telemetry/metrics", params=params
        )
        response.raise_for_status()
        data: Dict[str, Any] = response.json()
        metrics: List[str] = data.get("metrics", [])
        return metrics

    async def get_telemetry_statistics(
        self, tenant_id: Optional[str] = None
    ) -> Dict[str, Any]:
        """Get telemetry storage statistics.

        Args:
            tenant_id: Optional tenant filter.

        Returns:
            Dict with total records, unique devices/metrics, etc.
        """
        params: Dict[str, str] = {}
        if tenant_id:
            params["tenantId"] = tenant_id

        response = await self._client.get(
            "/api/telemetry/statistics", params=params
        )
        response.raise_for_status()
        result: Dict[str, Any] = response.json()
        return result

    # ==================================================================
    # Lifecycle
    # ==================================================================

    async def close(self) -> None:
        """Close the underlying HTTP client."""
        await self._client.aclose()

    async def __aenter__(self) -> "IamAdminClient":
        return self

    async def __aexit__(self, *args: object) -> None:
        await self.close()
