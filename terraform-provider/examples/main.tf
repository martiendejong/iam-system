terraform {
  required_providers {
    iam = {
      source  = "iam-system/iam"
      version = "~> 1.0"
    }
  }
}

# Configure the IAM provider.
# Authentication via API key or username/password.
# Values can also be set via environment variables:
#   IAM_BASE_URL, IAM_API_KEY, IAM_USERNAME, IAM_PASSWORD
provider "iam" {
  base_url = var.iam_base_url
  api_key  = var.iam_api_key
}

# ---------------------------------------------------------------------------
# Variables
# ---------------------------------------------------------------------------

variable "iam_base_url" {
  description = "Base URL of the IAM System API"
  type        = string
  default     = "https://iam.example.com"
}

variable "iam_api_key" {
  description = "API key for the IAM System"
  type        = string
  sensitive   = true
}

# ---------------------------------------------------------------------------
# Organization hierarchy: Acme Corp > Headquarters > Floors > Rooms
# ---------------------------------------------------------------------------

resource "iam_tenant" "acme" {
  name = "Acme Corporation"
  type = "Organization"
  metadata = jsonencode({
    industry = "Manufacturing"
    country  = "NL"
  })
}

resource "iam_tenant" "hq" {
  name             = "Headquarters"
  type             = "Building"
  parent_tenant_id = iam_tenant.acme.id
  metadata = jsonencode({
    address = "Keizersgracht 100, Amsterdam"
    floors  = 5
  })
  settings = jsonencode({
    default_access_hours = "08:00-18:00"
    timezone             = "Europe/Amsterdam"
  })
}

resource "iam_tenant" "floor_3" {
  name             = "Floor 3 - Engineering"
  type             = "Floor"
  parent_tenant_id = iam_tenant.hq.id
  metadata = jsonencode({
    floor_number = 3
    department   = "Engineering"
  })
}

resource "iam_tenant" "server_room" {
  name             = "Server Room 3A"
  type             = "Room"
  parent_tenant_id = iam_tenant.floor_3.id
  metadata = jsonencode({
    room_number = "3A"
    capacity    = 20
    type        = "restricted"
  })
}

# ---------------------------------------------------------------------------
# Roles
# ---------------------------------------------------------------------------

resource "iam_role" "building_manager" {
  name        = "Building Manager"
  description = "Can manage building operations including HVAC, lighting, and access control"
  tenant_id   = iam_tenant.hq.id
  permissions = [
    "buildings.read",
    "buildings.manage",
    "devices.read",
    "devices.control",
    "analytics.read",
    "settings.read",
    "settings.update",
  ]
}

resource "iam_role" "security_officer" {
  name        = "Security Officer"
  description = "Can manage access control and view security cameras"
  tenant_id   = iam_tenant.hq.id
  permissions = [
    "buildings.read",
    "devices.read",
    "devices.control",
    "analytics.read",
    "analytics.export",
  ]
}

resource "iam_role" "engineer" {
  name        = "Engineer"
  description = "Standard engineering floor access"
  tenant_id   = iam_tenant.floor_3.id
  permissions = [
    "buildings.read",
    "devices.read",
  ]
}

# ---------------------------------------------------------------------------
# Users
# ---------------------------------------------------------------------------

resource "iam_user" "admin" {
  email      = "admin@acme.example.com"
  password   = var.admin_password
  first_name = "Admin"
  last_name  = "User"

  roles {
    role_id   = iam_role.building_manager.id
    tenant_id = iam_tenant.hq.id
  }
}

resource "iam_user" "security" {
  email      = "security@acme.example.com"
  password   = var.security_password
  first_name = "Security"
  last_name  = "Officer"

  roles {
    role_id   = iam_role.security_officer.id
    tenant_id = iam_tenant.hq.id
  }
}

variable "admin_password" {
  description = "Password for the admin user"
  type        = string
  sensitive   = true
}

variable "security_password" {
  description = "Password for the security user"
  type        = string
  sensitive   = true
}

# ---------------------------------------------------------------------------
# IoT Devices
# ---------------------------------------------------------------------------

resource "iam_device" "temp_sensor" {
  device_id             = "temp-sensor-floor3-001"
  name                  = "Temperature Sensor Floor 3 Entrance"
  device_type           = "sensor"
  authentication_method = "certificate"
  tenant_id             = iam_tenant.floor_3.id
  resource_path         = "acme:hq:floor-3:sensor:temp-001"
  permissions = [
    "acme:hq:floor-3:sensor:temp-001:telemetry:write",
    "acme:hq:floor-3:sensor:temp-001:status:write",
  ]
  metadata = jsonencode({
    manufacturer = "Honeywell"
    model        = "T6 Pro"
    firmware     = "2.1.0"
  })
  tags = ["sensor", "temperature", "floor-3", "critical"]
}

resource "iam_device" "hvac_unit" {
  device_id             = "hvac-floor3-unit247"
  name                  = "Floor 3 HVAC Unit 247"
  device_type           = "hvac"
  authentication_method = "certificate"
  tenant_id             = iam_tenant.floor_3.id
  resource_path         = "acme:hq:floor-3:hvac:unit-247"
  permissions = [
    "acme:hq:floor-3:hvac:unit-247:telemetry:write",
    "acme:hq:floor-3:hvac:unit-247:commands:read",
    "acme:hq:floor-3:hvac:unit-247:status:write",
  ]
  metadata = jsonencode({
    manufacturer = "Carrier"
    model        = "AquaEdge 23XRV"
    capacity_kw  = 500
  })
  tags = ["hvac", "floor-3", "high-capacity"]
}

resource "iam_device" "door_lock" {
  device_id             = "door-lock-server-room-3a"
  name                  = "Server Room 3A Door Lock"
  device_type           = "access"
  authentication_method = "hmac"
  tenant_id             = iam_tenant.server_room.id
  resource_path         = "acme:hq:floor-3:room-3a:access:door-lock"
  permissions = [
    "acme:hq:floor-3:room-3a:access:door-lock:status:write",
    "acme:hq:floor-3:room-3a:access:door-lock:commands:read",
  ]
  tags = ["access-control", "server-room", "critical"]
}

resource "iam_device" "camera" {
  device_id             = "camera-floor3-corridor"
  name                  = "Floor 3 Corridor Camera"
  device_type           = "camera"
  authentication_method = "certificate"
  tenant_id             = iam_tenant.floor_3.id
  resource_path         = "acme:hq:floor-3:camera:corridor-001"
  permissions = [
    "acme:hq:floor-3:camera:corridor-001:stream:write",
    "acme:hq:floor-3:camera:corridor-001:status:write",
  ]
  tags = ["camera", "security", "floor-3"]
}

# ---------------------------------------------------------------------------
# Policies - Access control with spatial inheritance
# ---------------------------------------------------------------------------

# Building managers can control all HVAC units in the entire building (inherits to all floors).
resource "iam_policy" "building_hvac_control" {
  name              = "Building HVAC Control"
  description       = "Building managers can control all HVAC units in all floors"
  tenant_id         = iam_tenant.hq.id
  role_id           = iam_role.building_manager.id
  effect            = "Allow"
  resource_path     = "HVAC"
  action            = "Control"
  priority          = 100
  inheritance_scope = "Descendants"
}

# Security officers can view all cameras across the building.
resource "iam_policy" "security_camera_view" {
  name              = "Security Camera Viewing"
  description       = "Security officers can view all cameras in the building"
  tenant_id         = iam_tenant.hq.id
  role_id           = iam_role.security_officer.id
  effect            = "Allow"
  resource_path     = "Camera"
  action            = "View"
  priority          = 100
  inheritance_scope = "Descendants"
}

# Server room access - restricted to security officers only, during business hours.
resource "iam_policy" "server_room_access" {
  name          = "Server Room Access Control"
  description   = "Only security officers can access the server room during business hours"
  tenant_id     = iam_tenant.server_room.id
  role_id       = iam_role.security_officer.id
  effect        = "Allow"
  resource_path = "Door"
  action        = "Unlock"
  priority      = 200
  time_constraints = jsonencode({
    start_time   = "08:00"
    end_time     = "18:00"
    days_of_week = [1, 2, 3, 4, 5]
    timezone     = "Europe/Amsterdam"
  })
}

# Deny everyone else access to the server room (explicit deny, higher priority).
resource "iam_policy" "server_room_deny" {
  name              = "Server Room Default Deny"
  description       = "Deny all access to server room by default"
  tenant_id         = iam_tenant.server_room.id
  effect            = "Deny"
  resource_path     = "Door"
  action            = "Unlock"
  priority          = 50
  inheritance_scope = "Self"
}

# Sensor telemetry policy - allow all sensors to write telemetry data.
resource "iam_policy" "sensor_telemetry" {
  name              = "Sensor Telemetry Write"
  description       = "All sensors can write telemetry data to their assigned paths"
  tenant_id         = iam_tenant.hq.id
  effect            = "Allow"
  resource_path     = "acme:hq:*:sensor:*"
  action            = "telemetry:write"
  priority          = 100
  inheritance_scope = "Descendants"
}

# Conditional policy - allow remote access only from trusted networks.
resource "iam_policy" "remote_access" {
  name          = "Remote Access - Trusted Networks Only"
  description   = "Allow remote access only from trusted IP ranges"
  tenant_id     = iam_tenant.hq.id
  effect        = "Allow"
  resource_path = "Building"
  action        = "RemoteAccess"
  priority      = 150
  conditions = jsonencode({
    ip_whitelist  = ["10.0.0.0/8", "172.16.0.0/12"]
    device_health = "trusted"
  })
}

# ---------------------------------------------------------------------------
# Data Sources - Look up existing resources
# ---------------------------------------------------------------------------

data "iam_tenant" "existing_acme" {
  id = iam_tenant.acme.id
}

data "iam_device" "lookup_sensor" {
  device_id = "temp-sensor-floor3-001"

  depends_on = [iam_device.temp_sensor]
}

# ---------------------------------------------------------------------------
# Outputs
# ---------------------------------------------------------------------------

output "acme_tenant_id" {
  description = "The ID of the Acme Corporation tenant"
  value       = iam_tenant.acme.id
}

output "hq_tenant_id" {
  description = "The ID of the Headquarters tenant"
  value       = iam_tenant.hq.id
}

output "door_lock_shared_secret" {
  description = "The HMAC shared secret for the door lock device (store securely!)"
  value       = iam_device.door_lock.shared_secret
  sensitive   = true
}

output "temp_sensor_id" {
  description = "The internal UUID of the temperature sensor"
  value       = iam_device.temp_sensor.id
}

output "building_manager_role_id" {
  description = "The ID of the Building Manager role"
  value       = iam_role.building_manager.id
}
