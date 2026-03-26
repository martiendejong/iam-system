# Building Management System - Complete Use Case

How the generic IAM System applies to a **real-world 5-floor office building** with 400 IoT devices.

---

## Building Profile

**Organization:** Acme Corporation Headquarters
**Location:** Amsterdam, Netherlands
**Size:** 5 floors, 5,000 m² per floor
**Occupancy:** 800 employees
**IoT Device Count:** 400 devices

### Device Inventory

| Device Type | Quantity | Authentication | Purpose |
|-------------|----------|----------------|---------|
| **HVAC Units** | 50 | X.509 Certificate | Thermostats, air handlers, dampers |
| **Lighting Zones** | 120 | X.509 Certificate | Smart bulbs, occupancy sensors, switches |
| **Access Control** | 30 | X.509 Certificate | Door locks, badge readers, turnstiles |
| **Environmental Sensors** | 200 | HMAC-SHA256 | Temperature, humidity, CO2, motion |

**Total:** 400 devices

---

## Resource Hierarchy

### Hierarchical Structure

```
acme:headquarters:floor-1:hvac:unit-001
acme:headquarters:floor-1:hvac:unit-002
acme:headquarters:floor-1:lighting:zone-001
acme:headquarters:floor-1:lighting:zone-002
acme:headquarters:floor-1:access:door-101
acme:headquarters:floor-1:sensor:temp-001
acme:headquarters:floor-1:sensor:temp-002
...

acme:headquarters:floor-2:hvac:unit-011
acme:headquarters:floor-2:hvac:unit-012
acme:headquarters:floor-2:lighting:zone-011
acme:headquarters:floor-2:lighting:zone-012
acme:headquarters:floor-2:access:door-201
acme:headquarters:floor-2:sensor:temp-011
...

(Floors 3, 4, 5 similar structure)
```

### Key Design Principle

The hierarchy is **domain-agnostic**. The IAM system doesn't know what "hvac" or "lighting" means—it's just a string in the resource path.

This same structure could be:
- **Factory:** `acme:factory:line-2:robot:arm-05`
- **Fleet:** `acme:fleet:warehouse-3:forklift:vehicle-12`
- **Hospital:** `acme:hospital:wing-a:monitor:patient-42`

---

## RBAC Roles

### Role 1: Building Manager (Full Access)

**Permissions:**
```json
{
  "role": "building-manager",
  "permissions": [
    "acme:headquarters:*:*:*:*:read",
    "acme:headquarters:*:*:*:*:write"
  ]
}
```

**Interpretation:** Can read AND write to ALL devices on ALL floors.

**User:** Jane Doe (Facilities Manager)

**Use Cases:**
- Override HVAC settings during events
- Lock/unlock any door remotely
- View all sensor data in real-time dashboard
- Emergency building shutdown

---

### Role 2: HVAC Technician (Floor-Agnostic)

**Permissions:**
```json
{
  "role": "hvac-technician",
  "permissions": [
    "acme:headquarters:*:hvac:*:*:read",
    "acme:headquarters:*:hvac:*:*:write"
  ]
}
```

**Interpretation:** Can read AND write to ALL HVAC devices on ALL floors, but NOT lighting/access/sensors.

**User:** John Smith (HVAC Specialist)

**Use Cases:**
- Respond to HVAC complaints on any floor
- Adjust thermostats and air handlers
- Perform maintenance and diagnostics
- Cannot access door locks or lighting

---

### Role 3: Floor 3 Security Guard

**Permissions:**
```json
{
  "role": "security-floor-3",
  "permissions": [
    "acme:headquarters:floor-3:access:*:*:read",
    "acme:headquarters:floor-3:access:*:*:write",
    "acme:headquarters:floor-3:sensor:*:telemetry:read"
  ]
}
```

**Interpretation:** Can control access (doors, badge readers) on floor 3 ONLY, and view sensor data for situational awareness.

**User:** Mike Johnson (Security Officer)

**Use Cases:**
- Unlock doors for visitors
- Monitor who entered/exited floor 3
- View motion sensor alerts
- Cannot access floors 1, 2, 4, or 5
- Cannot control HVAC or lighting

---

### Role 4: Lighting Control System (Service)

**Permissions:**
```json
{
  "role": "lighting-control-service",
  "permissions": [
    "acme:headquarters:*:lighting:*:telemetry:read",
    "acme:headquarters:*:lighting:*:commands:write",
    "acme:headquarters:*:sensor:*:telemetry:read"
  ]
}
```

**Interpretation:** Automated service that controls ALL lighting zones based on occupancy sensors.

**Service:** Building Management System (BMS) - Lighting Module

**Use Cases:**
- Read occupancy sensor data
- Dim/brighten lights automatically
- Turn off lights in unoccupied zones
- Cannot control HVAC or access doors

---

### Role 5: Specific HVAC Unit (Device)

**Permissions:**
```json
{
  "role": "hvac-unit-device",
  "permissions": [
    "acme:headquarters:floor-2:hvac:unit-042:telemetry:write",
    "acme:headquarters:floor-2:hvac:unit-042:commands:read"
  ]
}
```

**Interpretation:** Can ONLY publish telemetry to its own topic and receive commands on its own topic. **Least privilege.**

**Device:** Thermostat on Floor 2, Zone 4 (acme-hq-floor2-hvac-unit042)

**Use Cases:**
- Publish temperature/humidity readings every 30 seconds
- Receive setpoint changes from BMS
- **Cannot access any other device** (lateral movement prevention)

---

## MQTT Topic Structure

### Topic Naming Convention

```
{org}/{floor}/{device-type}/{device-id}/{message-type}
```

### Examples

**Telemetry (Device → System):**
```
acme/floor-1/hvac/unit-001/telemetry
acme/floor-1/sensor/temp-001/telemetry
acme/floor-2/lighting/zone-011/telemetry
acme/floor-3/access/door-301/telemetry
```

**Commands (System → Device):**
```
acme/floor-1/hvac/unit-001/commands
acme/floor-2/lighting/zone-011/commands
acme/floor-3/access/door-301/commands
```

**Status (Device Lifecycle):**
```
acme/floor-1/hvac/unit-001/status
```

---

## Real-World Scenarios

### Scenario 1: HVAC Complaint on Floor 3

**Problem:** Employee complains "Floor 3 is too cold"

**Workflow:**

1. **Building Manager (Jane)** opens dashboard:
   - WebSocket subscribes to: `acme/floor-3/hvac/*/telemetry`
   - Authorization check: `acme:headquarters:floor-3:hvac:*:telemetry:read` → **ALLOWED**

2. Dashboard shows:
   - Zone 3A: 18°C (target 21°C) ❄️ COLD
   - Zone 3B: 22°C (target 21°C) ✅ OK
   - Zone 3C: 20°C (target 21°C) ✅ OK

3. **Jane identifies** problem: Zone 3A thermostat (unit-047) not heating properly

4. **Jane sends command** via dashboard:
   - MQTT publish to: `acme/floor-3/hvac/unit-047/commands`
   - Payload: `{"action": "set_mode", "value": "heating_max"}`
   - Authorization check: `acme:headquarters:floor-3:hvac:unit-047:commands:write` → **ALLOWED**

5. **Thermostat receives** command:
   - Validates JWT token from edge gateway
   - Executes: Turn on heating, fan speed = MAX
   - Publishes status: `{"mode": "heating_max", "fan_speed": 5}`

6. **10 minutes later:**
   - Telemetry shows: 19°C → 20°C → 21°C ✅ RESOLVED
   - Jane marks ticket as closed

**Time to resolution:** 15 minutes (vs. 2 hours manually visiting floor 3)

---

### Scenario 2: After-Hours Security Lockdown

**Situation:** 10:00 PM - Building closing, security must lock all doors

**Workflow:**

1. **Security System** (automated service) triggers lockdown:
   - Service has role: `security-automation`
   - Permissions: `acme:headquarters:*:access:*:commands:write`

2. **Service publishes** to all door topics:
   ```
   acme/floor-1/access/door-101/commands → {"action": "lock"}
   acme/floor-1/access/door-102/commands → {"action": "lock"}
   acme/floor-1/access/door-103/commands → {"action": "lock"}
   ...
   acme/floor-5/access/door-501/commands → {"action": "lock"}
   ```

3. **Edge gateway authorizes** each command:
   - Checks service JWT token
   - Verifies permission: `acme:headquarters:*:access:*:commands:write` → **ALLOWED**
   - Forwards to MQTT broker

4. **30 doors lock** within 5 seconds:
   - Each door lock device receives command
   - Executes physical lock mechanism
   - Publishes status: `{"locked": true, "timestamp": "2026-03-24T22:00:05Z"}`

5. **Security dashboard** shows:
   - 30/30 doors locked ✅
   - All access points secured

**Time to lockdown:** 5 seconds (vs. 30 minutes manual patrol)

---

### Scenario 3: Energy Optimization (Lighting)

**Situation:** 7:00 AM - Most employees not yet in office, lights should auto-adjust based on occupancy

**Workflow:**

1. **200 occupancy sensors** continuously publish:
   ```
   acme/floor-1/sensor/motion-001/telemetry → {"motion": false}
   acme/floor-1/sensor/motion-002/telemetry → {"motion": true}
   acme/floor-2/sensor/motion-011/telemetry → {"motion": false}
   ...
   ```

2. **Lighting Control Service** (BMS module) subscribes:
   - Topic pattern: `acme/+/sensor/motion-+/telemetry`
   - Authorization: `acme:headquarters:*:sensor:*:telemetry:read` → **ALLOWED**

3. **Service processes** occupancy data:
   - Floor 1: 12% occupancy → Dim lights to 30%
   - Floor 2: 8% occupancy → Dim lights to 20%
   - Floor 3: 45% occupancy → Full brightness (executive floor)
   - Floor 4: 5% occupancy → Turn off most zones
   - Floor 5: 2% occupancy → Turn off all zones

4. **Service publishes** lighting commands:
   ```
   acme/floor-1/lighting/zone-001/commands → {"action": "dim", "value": 30}
   acme/floor-2/lighting/zone-011/commands → {"action": "dim", "value": 20}
   acme/floor-4/lighting/zone-041/commands → {"action": "off"}
   ...
   ```

5. **Authorization** at edge gateway:
   - Check service token
   - Verify: `acme:headquarters:*:lighting:*:commands:write` → **ALLOWED**
   - Forward to devices

6. **120 lighting zones** adjust within 1 second

7. **Energy savings:**
   - 7:00 AM - 9:00 AM: 70% lighting reduction
   - Annual savings: €15,000 in electricity costs

**ROI:** System pays for itself in energy savings alone within 2 years

---

### Scenario 4: Device Compromise Response

**Situation:** Security team detects abnormal behavior from thermostat unit-042 (possible malware)

**Threat:** Compromised device trying to access door locks (lateral movement attack)

**How IAM System Prevents This:**

1. **Compromised thermostat** (unit-042) tries to unlock door:
   - MQTT publish to: `acme/floor-2/access/door-201/commands`
   - Payload: `{"action": "unlock"}`

2. **Edge gateway authorization check:**
   - Device claims: `acme:headquarters:floor-2:hvac:unit-042:telemetry:write`
   - Target resource: `acme:headquarters:floor-2:access:door-201:commands`
   - **Permission match:** NO (device can only write to its own telemetry topic)

3. **Request DENIED** ❌
   - Edge gateway blocks the command
   - Logs security event: "Device unit-042 attempted unauthorized access to door-201"
   - Alert sent to security team

4. **Security response:**
   - Revoke certificate for unit-042 (cloud IAM)
   - Edge gateway receives CRL update within 5 minutes
   - Unit-042 disconnected from network
   - Physical investigation scheduled

5. **Impact contained:**
   - Door remains locked ✅
   - No other devices affected ✅
   - Attack stopped at edge gateway ✅

**Blast radius:** ZERO (hierarchical permissions prevented lateral movement)

---

## WebSocket Real-Time Dashboard

### Frontend (React + SignalR)

**User:** Building Manager viewing floor 3 in real-time

```typescript
// Dashboard Component
import * as signalR from "@microsoft/signalr";

const FloorDashboard = () => {
  const [devices, setDevices] = useState<Device[]>([]);
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);

  useEffect(() => {
    // Create SignalR connection to backend
    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl("/telemetry-hub", {
        accessTokenFactory: () => getJwtToken() // User JWT
      })
      .withAutomaticReconnect()
      .build();

    setConnection(newConnection);
  }, []);

  useEffect(() => {
    if (connection) {
      connection.start()
        .then(() => {
          console.log("Connected to telemetry hub");

          // Subscribe to all devices on floor 3
          connection.invoke("SubscribeToFloor", "floor-3");

          // Handle incoming telemetry
          connection.on("DeviceTelemetry", (data) => {
            updateDevice(data); // Update UI in real-time
          });
        })
        .catch(err => console.error("Connection error:", err));
    }

    return () => {
      if (connection) {
        connection.stop();
      }
    };
  }, [connection]);

  return (
    <div className="floor-dashboard">
      <h1>Floor 3 - Real-Time Monitoring</h1>

      <section className="hvac-zones">
        <h2>HVAC Status</h2>
        {devices.filter(d => d.type === 'hvac').map(device => (
          <HvacCard key={device.id} device={device} />
        ))}
      </section>

      <section className="lighting-zones">
        <h2>Lighting Zones</h2>
        {devices.filter(d => d.type === 'lighting').map(device => (
          <LightingCard key={device.id} device={device} />
        ))}
      </section>

      <section className="sensors">
        <h2>Environmental Sensors</h2>
        <TemperatureHeatmap floor={3} devices={devices} />
      </section>
    </div>
  );
};
```

### Backend (ASP.NET Core SignalR Hub)

```csharp
public class TelemetryHub : Hub
{
    private readonly IAuthorizationService _authz;
    private readonly IMqttSubscriber _mqtt;

    public async Task SubscribeToFloor(string floor)
    {
        // 1. Get user from JWT (automatic via SignalR)
        var userId = Context.User.FindFirst("sub")?.Value;
        var userClaims = await _authz.GetUserClaimsAsync(userId);

        // 2. Check permission
        var resource = $"acme:headquarters:{floor}:*:*:telemetry";
        if (!CheckHierarchicalPermission(userClaims, resource))
        {
            await Clients.Caller.SendAsync("Error", "Insufficient permissions");
            return;
        }

        // 3. Subscribe to MQTT on behalf of user
        var topics = new[]
        {
            $"acme/{floor}/hvac/+/telemetry",
            $"acme/{floor}/lighting/+/telemetry",
            $"acme/{floor}/sensor/+/telemetry",
            $"acme/{floor}/access/+/telemetry"
        };

        foreach (var topic in topics)
        {
            await _mqtt.SubscribeAsync(topic, async (mqttTopic, message) =>
            {
                // Forward to WebSocket client
                await Clients.Caller.SendAsync("DeviceTelemetry", new
                {
                    Topic = mqttTopic,
                    Payload = JsonSerializer.Deserialize<dynamic>(message),
                    Timestamp = DateTime.UtcNow
                });
            });
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, floor);
        await Clients.Caller.SendAsync("SubscriptionActive", floor);
    }
}
```

---

## Deployment Architecture

### Physical Hardware

```
┌─────────────────────────────────────────────────────────────┐
│                   ACME HEADQUARTERS                          │
│                   5 Floors, 400 Devices                      │
└───────────────────────────┬─────────────────────────────────┘
                            │
            ┌───────────────┼───────────────┐
            │               │               │
┌───────────▼─────┐ ┌──────▼──────┐ ┌──────▼──────────────┐
│  Edge Gateway   │ │  Cloud IAM  │ │   BMS Dashboard     │
│  (Raspberry Pi) │ │  (Azure VM) │ │   (React SPA)       │
│                 │ │             │ │                     │
│  - MQTT Broker  │ │  - CA Root  │ │  - SignalR Client   │
│  - Authz Proxy  │ │  - Policy   │ │  - Real-Time View   │
│  - Cert Cache   │ │  - Audit    │ │  - Control Panel    │
└─────────────────┘ └─────────────┘ └─────────────────────┘
          │                   │                   │
          └───────────────────┴───────────────────┘
                         HTTPS/WSS
```

### Network Topology

- **Edge Gateway:** 192.168.1.10 (building network)
- **MQTT Broker:** localhost:1883 (edge gateway internal)
- **Cloud IAM:** https://iam.acme.com (public internet)
- **BMS Dashboard:** https://bms.acme.com (internal web app)

### Certificate Hierarchy

```
Root CA (IAM System - Valid 10 years)
  │
  └── Intermediate CA (Acme Corp - Valid 5 years)
        │
        ├── Edge Gateway Certificate (Valid 1 year)
        │
        └── Device Certificates (Valid 90 days)
              ├── acme-hq-floor1-hvac-unit001.crt
              ├── acme-hq-floor1-hvac-unit002.crt
              ├── acme-hq-floor1-lighting-zone001.crt
              └── ... (400 device certificates)
```

---

## Cost Analysis

### Traditional Building Management System (BACnet)

**Hardware:**
- BACnet gateway: €5,000
- Proprietary controllers: €800 × 50 = €40,000
- Annual support: €12,000/year

**Total 5-year cost:** €105,000

### IAM System + Open Source IoT Stack

**Hardware:**
- Raspberry Pi 4 (edge gateway): €75
- Open-source MQTT broker: €0
- Generic IoT devices: €200 × 50 = €10,000
- Generic sensors: €25 × 200 = €5,000

**Software:**
- IAM System (self-hosted): €0/year
- MQTT broker (Mosquitto): €0/year
- BMS dashboard (open source): €0/year

**Annual IAM System authentication costs:**
- 400 devices × 100 auths/day × 365 days = 14,600,000 auths/year
- Free tier: 10,000
- Paid: 14,590,000 × $0.00001 = $145.90/year (€135/year)

**Total 5-year cost:** €15,750

**Savings:** €89,250 (85% cheaper than traditional BACnet system)

---

## Migration from BACnet

If you already have a BACnet system, you can integrate IAM System:

### Hybrid Architecture

```
┌─────────────────┐      ┌─────────────────┐
│  Legacy BACnet  │◄────►│  BACnet Gateway │
│   Controllers   │      │   (Protocol     │
│  (Proprietary)  │      │    Bridge)      │
└─────────────────┘      └────────┬────────┘
                                   │
                                   │ MQTT
                                   │
                         ┌─────────▼────────┐
                         │  IAM System      │
                         │  Edge Gateway    │
                         │                  │
                         │  - Authorization │
                         │  - Audit Logging │
                         └──────────────────┘
```

**Benefits:**
- Gradually migrate devices (no forklift upgrade)
- Unified authorization across old and new systems
- Audit trail for compliance (ISO 27001, GDPR)
- Eventually replace proprietary controllers with generic IoT devices

---

## Conclusion

IAM System transforms a **5-floor office building** into a **fully secured, real-time managed facility** with:

✅ **400 devices authenticated** (X.509 + HMAC)
✅ **Zero trust security** (hierarchical least privilege)
✅ **Real-time monitoring** (WebSocket streaming)
✅ **Energy optimization** (automated lighting control)
✅ **85% cost savings** vs traditional BACnet
✅ **Lateral movement prevention** (compromised device can't access others)

**And it's completely generic** - the same system works for:
- Manufacturing plants (robots, PLCs, sensors)
- Smart cities (traffic lights, parking meters, waste bins)
- Healthcare facilities (patient monitors, infusion pumps, HVAC)
- Data centers (servers, cooling systems, power distribution)

---

**Next Steps for Acme Corporation:**

1. **Pilot Phase (Month 1):**
   - Deploy edge gateway (Raspberry Pi 4)
   - Connect 10 devices (2 per floor)
   - Test real-time dashboard
   - Validate authorization rules

2. **Rollout Phase (Months 2-3):**
   - Enroll all 400 devices
   - Train facilities staff
   - Integrate with existing BMS (if applicable)
   - Go live with full monitoring

3. **Optimization Phase (Months 4-6):**
   - Fine-tune energy optimization rules
   - Add predictive maintenance alerts
   - Implement access control automation
   - Measure ROI (energy savings, incident response time)

**Expected ROI:** System pays for itself in 18 months through energy savings and operational efficiency.

---

**Author:** IAM System Architecture Team
**Date:** March 24, 2026
**Use Case:** Building Management for 5-Floor Office
**Device Count:** 400 IoT devices
**Annual Savings:** €17,850 vs traditional BACnet system
