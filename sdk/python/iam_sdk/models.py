from pydantic import BaseModel
from typing import Optional, List, Dict, Any
from datetime import datetime


# ============================================================
# Authentication Models
# ============================================================

class LoginRequest(BaseModel):
    email: str
    password: str


class LoginResponse(BaseModel):
    access_token: str
    refresh_token: Optional[str] = None
    expires_in: int = 0
    user_id: str = ""
    email: str = ""
    roles: List[str] = []


class UserInfo(BaseModel):
    id: str
    email: str
    first_name: Optional[str] = None
    last_name: Optional[str] = None


# ============================================================
# Device Authentication Models
# ============================================================

class DeviceAuthResponse(BaseModel):
    success: bool = True
    access_token: Optional[str] = None
    token_type: str = "Bearer"
    expires_in: int = 0
    device_id: Optional[str] = None
    permissions: List[str] = []
    error: Optional[str] = None


class DeviceAuthorizeResponse(BaseModel):
    allowed: bool
    matched_permission: Optional[str] = None
    reason: Optional[str] = None


class DeviceClaimsResponse(BaseModel):
    device_id: str
    device_type: Optional[str] = None
    resource_path: Optional[str] = None
    permissions: List[str] = []
    metadata: Optional[Dict[str, Any]] = None
    cache_ttl_seconds: int = 300


# ============================================================
# Device Models
# ============================================================

class Device(BaseModel):
    id: str
    device_id: str
    name: str
    device_type: str
    authentication_method: str = "certificate"
    tenant_id: str = ""
    tenant_name: Optional[str] = None
    resource_path: str = ""
    permissions: List[str] = []
    is_active: bool = True
    is_online: bool = False
    last_seen_at: Optional[datetime] = None
    last_authenticated_at: Optional[datetime] = None
    is_provisioned: Optional[bool] = None
    provisioned_at: Optional[datetime] = None
    metadata: Optional[Any] = None
    tags: Optional[Any] = None
    created_at: Optional[datetime] = None
    updated_at: Optional[datetime] = None


class DeviceRegistrationResult(BaseModel):
    id: str
    device_id: str
    name: str
    device_type: str
    authentication_method: str
    tenant_id: str
    resource_path: str
    is_active: bool = True
    created_at: Optional[datetime] = None
    shared_secret: Optional[str] = None


class DeviceStatistics(BaseModel):
    total_devices: int = 0
    active_devices: int = 0
    online_devices: int = 0
    certificate_devices: int = 0
    hmac_devices: int = 0
    devices_by_type: Dict[str, int] = {}


# ============================================================
# Telemetry Models
# ============================================================

class TelemetryRecord(BaseModel):
    device_id: str
    metric_name: str
    numeric_value: Optional[float] = None
    string_value: Optional[str] = None
    json_value: Optional[str] = None
    unit: Optional[str] = None
    tenant_id: Optional[str] = None
    device_type: Optional[str] = None
    tags: Optional[str] = None
    timestamp: Optional[datetime] = None


class TelemetryQueryResult(BaseModel):
    records: List[Dict[str, Any]] = []
    total_count: int = 0
    earliest_timestamp: Optional[datetime] = None
    latest_timestamp: Optional[datetime] = None


class TelemetryAggregationResult(BaseModel):
    metric_name: str
    aggregation: str
    interval: str
    buckets: List[Dict[str, Any]] = []


# ============================================================
# User Models
# ============================================================

class User(BaseModel):
    id: str
    email: str
    first_name: Optional[str] = None
    last_name: Optional[str] = None
    email_confirmed: Optional[bool] = None
    two_factor_enabled: Optional[bool] = None
    is_active: bool = True
    created_at: Optional[datetime] = None
    last_login_at: Optional[datetime] = None
    roles: Any = []


class UserListResponse(BaseModel):
    total_count: int = 0
    page: int = 1
    page_size: int = 20
    total_pages: int = 0
    items: List[Dict[str, Any]] = []


# ============================================================
# Tenant Models
# ============================================================

class Tenant(BaseModel):
    id: str
    name: str
    type: Optional[str] = None
    parent_tenant_id: Optional[str] = None
    parent_tenant_name: Optional[str] = None
    child_count: int = 0
    metadata: Optional[Dict[str, Any]] = None
    settings: Optional[Dict[str, Any]] = None
    is_active: bool = True
    created_at: Optional[datetime] = None
    updated_at: Optional[datetime] = None


# ============================================================
# Role Models
# ============================================================

class Role(BaseModel):
    id: str
    name: str
    description: Optional[str] = None
    is_system_role: bool = False
    tenant_id: Optional[str] = None
    permissions: Optional[List[str]] = None
    created_at: Optional[datetime] = None
    user_count: Optional[int] = None


# ============================================================
# Policy Models
# ============================================================

class Policy(BaseModel):
    id: str
    name: str
    description: Optional[str] = None
    tenant_id: Optional[str] = None
    tenant_name: Optional[str] = None
    inheritance_scope: str = "Self"
    role_id: Optional[str] = None
    role_name: Optional[str] = None
    user_id: Optional[str] = None
    resource: str = ""
    action: str = ""
    effect: str = "Allow"
    priority: int = 0
    time_constraints: Optional[Any] = None
    conditions: Optional[Any] = None
    expires_at: Optional[datetime] = None
    is_active: bool = True
    created_at: Optional[datetime] = None


class PolicyEvaluationResult(BaseModel):
    is_allowed: bool
    reason: Optional[str] = None
    matched_policy: Optional[Dict[str, Any]] = None
    evaluated_policy_count: int = 0
    evaluation_time_ms: Optional[float] = None


class PolicySimulationResult(BaseModel):
    affected_tenant_count: int = 0
    affected_user_count: int = 0
    summary: Optional[str] = None
    affected_tenants: List[str] = []
