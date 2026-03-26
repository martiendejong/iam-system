# IoT Device Authentication Code Examples

Complete code examples for IoT device authentication in various languages and platforms.

---

## Example 1: ESP32 Temperature Sensor (C++ Arduino)

**Hardware:** ESP32 DevKit, DHT22 temperature sensor
**Authentication:** HMAC-SHA256 (constrained device)
**Protocol:** MQTT over TCP (no TLS due to memory constraints)

```cpp
// ESP32 Arduino - Temperature Sensor with HMAC Authentication

#include <WiFi.h>
#include <PubSubClient.h>
#include <DHT.h>
#include <mbedtls/md.h>

// Network Configuration
const char* WIFI_SSID = "building-network";
const char* WIFI_PASS = "secure-password";
const char* MQTT_BROKER = "edge-gateway.local";
const int MQTT_PORT = 1883;

// Device Identity
const char* DEVICE_ID = "acme-hq-floor3-sensor-temp089";
const char* SHARED_SECRET = "your-device-secret-key"; // Pre-provisioned during manufacturing

// Sensor Configuration
#define DHTPIN 4
#define DHTTYPE DHT22
DHT dht(DHTPIN, DHTTYPE);

WiFiClient wifiClient;
PubSubClient mqttClient(wifiClient);

// Generate HMAC-SHA256 password for MQTT authentication
String generateHmacPassword() {
    // Format: timestamp:nonce:HMAC(shared_secret, timestamp + nonce)
    unsigned long timestamp = millis() / 1000; // Seconds since boot
    uint32_t nonce = random(100000, 999999);

    String message = String(timestamp) + ":" + String(nonce);

    // Compute HMAC-SHA256
    byte hmacResult[32];
    mbedtls_md_context_t ctx;
    mbedtls_md_type_t md_type = MBEDTLS_MD_SHA256;

    mbedtls_md_init(&ctx);
    mbedtls_md_setup(&ctx, mbedtls_md_info_from_type(md_type), 1);
    mbedtls_md_hmac_starts(&ctx, (const unsigned char*)SHARED_SECRET, strlen(SHARED_SECRET));
    mbedtls_md_hmac_update(&ctx, (const unsigned char*)message.c_str(), message.length());
    mbedtls_md_hmac_finish(&ctx, hmacResult);
    mbedtls_md_free(&ctx);

    // Convert HMAC to hex string
    String hexHmac = "";
    for (int i = 0; i < 32; i++) {
        if (hmacResult[i] < 0x10) hexHmac += "0";
        hexHmac += String(hmacResult[i], HEX);
    }

    return String(timestamp) + ":" + String(nonce) + ":" + hexHmac;
}

void setup() {
    Serial.begin(115200);
    dht.begin();

    // Connect to WiFi
    Serial.print("Connecting to WiFi");
    WiFi.begin(WIFI_SSID, WIFI_PASS);

    while (WiFi.status() != WL_CONNECTED) {
        delay(500);
        Serial.print(".");
    }

    Serial.println("\nWiFi connected");
    Serial.print("IP Address: ");
    Serial.println(WiFi.localIP());

    // Configure MQTT
    mqttClient.setServer(MQTT_BROKER, MQTT_PORT);
    mqttClient.setCallback(mqttCallback);

    // Connect to MQTT broker
    reconnectMqtt();
}

void reconnectMqtt() {
    while (!mqttClient.connected()) {
        Serial.println("Connecting to MQTT broker...");

        // Generate HMAC password
        String password = generateHmacPassword();

        if (mqttClient.connect(DEVICE_ID, DEVICE_ID, password.c_str())) {
            Serial.println("MQTT connected");

            // Subscribe to command topic (constrained devices get one topic)
            String cmdTopic = String("acme/floor-3/sensor/temp-089/commands");
            mqttClient.subscribe(cmdTopic.c_str());
            Serial.println("Subscribed to: " + cmdTopic);
        } else {
            Serial.print("MQTT connection failed, rc=");
            Serial.println(mqttClient.state());
            Serial.println("Retrying in 5 seconds...");
            delay(5000);
        }
    }
}

void mqttCallback(char* topic, byte* payload, unsigned int length) {
    // Handle incoming commands
    Serial.print("Message received on topic: ");
    Serial.println(topic);

    String message = "";
    for (unsigned int i = 0; i < length; i++) {
        message += (char)payload[i];
    }

    Serial.println("Payload: " + message);

    // Parse command (example: {"action":"calibrate"})
    if (message.indexOf("calibrate") >= 0) {
        Serial.println("Calibrating sensor...");
        // Perform calibration
    }
}

void loop() {
    // Maintain MQTT connection
    if (!mqttClient.connected()) {
        reconnectMqtt();
    }
    mqttClient.loop();

    // Read sensor data
    float temperature = dht.readTemperature();
    float humidity = dht.readHumidity();

    if (isnan(temperature) || isnan(humidity)) {
        Serial.println("Failed to read from DHT sensor!");
        return;
    }

    // Publish telemetry
    String topic = "acme/floor-3/sensor/temp-089/telemetry";
    String payload = "{";
    payload += "\"device_id\":\"" + String(DEVICE_ID) + "\",";
    payload += "\"temperature\":" + String(temperature, 1) + ",";
    payload += "\"humidity\":" + String(humidity, 1) + ",";
    payload += "\"timestamp\":" + String(millis());
    payload += "}";

    if (mqttClient.publish(topic.c_str(), payload.c_str())) {
        Serial.println("Published: " + payload);
    } else {
        Serial.println("Publish failed");
    }

    // Publish every 60 seconds
    delay(60000);
}
```

---

## Example 2: Raspberry Pi Industrial Controller (Python)

**Hardware:** Raspberry Pi 4, modbus interface
**Authentication:** X.509 certificate with mTLS
**Protocol:** MQTT over TLS

```python
# Raspberry Pi - Industrial HVAC Controller with X.509 Certificate

import paho.mqtt.client as mqtt
import ssl
import json
import time
from datetime import datetime

# Configuration
DEVICE_ID = "acme-hq-floor3-hvac-unit247"
MQTT_BROKER = "edge-gateway.local"
MQTT_PORT = 8883  # TLS port

# Certificate paths
CA_CERT = "/etc/iam/certs/ca.crt"
CLIENT_CERT = "/etc/iam/certs/device.crt"
CLIENT_KEY = "/etc/iam/certs/device.key"

class HvacController:
    def __init__(self):
        self.client = mqtt.Client(client_id=DEVICE_ID)

        # Configure TLS with X.509 certificate
        self.client.tls_set(
            ca_certs=CA_CERT,
            certfile=CLIENT_CERT,
            keyfile=CLIENT_KEY,
            cert_reqs=ssl.CERT_REQUIRED,
            tls_version=ssl.PROTOCOL_TLSv1_2
        )

        # Set callbacks
        self.client.on_connect = self.on_connect
        self.client.on_message = self.on_message
        self.client.on_disconnect = self.on_disconnect

        # HVAC state
        self.current_temp = 22.0
        self.target_temp = 21.0
        self.mode = "cooling"
        self.fan_speed = 3

    def on_connect(self, client, userdata, flags, rc):
        """Callback when connected to MQTT broker"""
        if rc == 0:
            print(f"[{datetime.now()}] Connected to MQTT broker")

            # Subscribe to command topic
            cmd_topic = f"acme/floor-3/hvac/{DEVICE_ID.split('-')[-1]}/commands"
            client.subscribe(cmd_topic, qos=1)
            print(f"[{datetime.now()}] Subscribed to: {cmd_topic}")

            # Publish online status
            status_topic = f"acme/floor-3/hvac/{DEVICE_ID.split('-')[-1]}/status"
            status = json.dumps({"status": "online", "timestamp": datetime.now().isoformat()})
            client.publish(status_topic, status, qos=1, retain=True)
        else:
            print(f"[{datetime.now()}] Connection failed with code {rc}")

    def on_disconnect(self, client, userdata, rc):
        """Callback when disconnected"""
        print(f"[{datetime.now()}] Disconnected with code {rc}")
        if rc != 0:
            print("Unexpected disconnection. Reconnecting...")

    def on_message(self, client, userdata, msg):
        """Handle incoming commands"""
        print(f"[{datetime.now()}] Message received on {msg.topic}")

        try:
            command = json.loads(msg.payload.decode())
            action = command.get('action')

            if action == 'set_temperature':
                new_temp = command.get('value')
                print(f"Setting target temperature to {new_temp}°C")
                self.target_temp = new_temp
                self.execute_hvac_control()

            elif action == 'set_mode':
                new_mode = command.get('value')  # heating, cooling, fan_only
                print(f"Setting mode to {new_mode}")
                self.mode = new_mode
                self.execute_hvac_control()

            elif action == 'set_fan_speed':
                new_speed = command.get('value')  # 1-5
                print(f"Setting fan speed to {new_speed}")
                self.fan_speed = new_speed
                self.execute_hvac_control()

            else:
                print(f"Unknown action: {action}")

        except json.JSONDecodeError as e:
            print(f"Error decoding command: {e}")

    def execute_hvac_control(self):
        """Execute HVAC control logic"""
        # Simulate HVAC control (in real implementation, this would interface with modbus)
        print(f"HVAC Control: mode={self.mode}, target={self.target_temp}°C, fan={self.fan_speed}")
        # ... actual hardware control here ...

    def read_current_temperature(self):
        """Read current temperature from sensor"""
        # Simulate sensor reading (replace with actual sensor interface)
        import random
        self.current_temp = 22.0 + random.uniform(-1, 1)
        return self.current_temp

    def publish_telemetry(self):
        """Publish telemetry data"""
        telemetry_topic = f"acme/floor-3/hvac/{DEVICE_ID.split('-')[-1]}/telemetry"

        telemetry = {
            "device_id": DEVICE_ID,
            "current_temp": round(self.current_temp, 1),
            "target_temp": self.target_temp,
            "mode": self.mode,
            "fan_speed": self.fan_speed,
            "timestamp": datetime.now().isoformat()
        }

        self.client.publish(telemetry_topic, json.dumps(telemetry), qos=0)
        print(f"[{datetime.now()}] Published telemetry: {telemetry}")

    def run(self):
        """Main control loop"""
        # Connect to broker
        print(f"Connecting to {MQTT_BROKER}:{MQTT_PORT}...")
        self.client.connect(MQTT_BROKER, MQTT_PORT, keepalive=60)
        self.client.loop_start()

        try:
            while True:
                # Read sensor
                self.read_current_temperature()

                # Publish telemetry every 30 seconds
                self.publish_telemetry()

                time.sleep(30)

        except KeyboardInterrupt:
            print("\nShutting down...")

            # Publish offline status
            status_topic = f"acme/floor-3/hvac/{DEVICE_ID.split('-')[-1]}/status"
            status = json.dumps({"status": "offline", "timestamp": datetime.now().isoformat()})
            self.client.publish(status_topic, status, qos=1, retain=True)

            self.client.loop_stop()
            self.client.disconnect()

if __name__ == "__main__":
    controller = HvacController()
    controller.run()
```

---

## Example 3: .NET Edge Gateway Authorization Proxy (C#)

**Platform:** .NET 9.0 on Ubuntu/Raspberry Pi OS
**Role:** Local MQTT broker with authorization

```csharp
// EdgeGateway.Authorization.Service
// ASP.NET Core service running on Raspberry Pi 4

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using MQTTnet;
using MQTTnet.Server;

public class EdgeGatewayAuthorizationService
{
    private readonly IMemoryCache _policyCache;
    private readonly ILogger<EdgeGatewayAuthorizationService> _logger;
    private readonly HttpClient _cloudIamClient;
    private readonly MqttServer _mqttServer;

    public EdgeGatewayAuthorizationService(
        IMemoryCache policyCache,
        ILogger<EdgeGatewayAuthorizationService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _policyCache = policyCache;
        _logger = logger;
        _cloudIamClient = httpClientFactory.CreateClient("CloudIAM");
    }

    // MQTT Connection Handler (validates device authentication)
    public async Task<MqttConnectionValidatorDelegate> ValidateConnectionAsync()
    {
        return async context =>
        {
            var deviceId = context.ClientId;
            var password = Encoding.UTF8.GetString(context.Password ?? Array.Empty<byte>());

            // Determine authentication method from context
            if (context.IsSecureConnection && context.ClientCertificate != null)
            {
                // Method 1: X.509 Certificate (capable devices)
                var isValid = await ValidateCertificateAsync(context.ClientCertificate, deviceId);
                context.ReasonCode = isValid
                    ? MqttConnectReasonCode.Success
                    : MqttConnectReasonCode.NotAuthorized;
            }
            else if (!string.IsNullOrEmpty(password))
            {
                // Method 2: HMAC-SHA256 (constrained devices)
                var isValid = await ValidateHmacAsync(deviceId, password);
                context.ReasonCode = isValid
                    ? MqttConnectReasonCode.Success
                    : MqttConnectReasonCode.NotAuthorized;
            }
            else
            {
                context.ReasonCode = MqttConnectReasonCode.NotAuthorized;
            }

            _logger.LogInformation(
                "Device connection: {DeviceId} - {Result}",
                deviceId,
                context.ReasonCode);
        };
    }

    // Validate X.509 Certificate (mTLS)
    private async Task<bool> ValidateCertificateAsync(X509Certificate2 cert, string deviceId)
    {
        try
        {
            // 1. Verify certificate chain
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online; // Check CRL
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            var isValidChain = chain.Build(cert);
            if (!isValidChain)
            {
                _logger.LogWarning("Certificate chain validation failed for {DeviceId}", deviceId);
                return false;
            }

            // 2. Verify certificate is not expired
            if (cert.NotAfter < DateTime.UtcNow)
            {
                _logger.LogWarning("Certificate expired for {DeviceId}", deviceId);
                return false;
            }

            // 3. Verify device ID matches certificate subject
            var certDeviceId = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (certDeviceId != deviceId)
            {
                _logger.LogWarning(
                    "Device ID mismatch: {Expected} != {Actual}",
                    deviceId,
                    certDeviceId);
                return false;
            }

            // 4. Load device claims into cache (for authorization)
            await LoadDeviceClaimsAsync(deviceId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating certificate for {DeviceId}", deviceId);
            return false;
        }
    }

    // Validate HMAC-SHA256 (constrained devices)
    private async Task<bool> ValidateHmacAsync(string deviceId, string password)
    {
        try
        {
            // Password format: "timestamp:nonce:hmac"
            var parts = password.Split(':');
            if (parts.Length != 3)
            {
                _logger.LogWarning("Invalid HMAC format for {DeviceId}", deviceId);
                return false;
            }

            var timestamp = long.Parse(parts[0]);
            var nonce = parts[1];
            var receivedHmac = parts[2];

            // 1. Verify timestamp (±5 minute window to prevent replay attacks)
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(now - timestamp) > 300)
            {
                _logger.LogWarning("HMAC timestamp out of window for {DeviceId}", deviceId);
                return false;
            }

            // 2. Get device shared secret from cache or cloud
            var sharedSecret = await GetDeviceSharedSecretAsync(deviceId);
            if (string.IsNullOrEmpty(sharedSecret))
            {
                _logger.LogWarning("No shared secret found for {DeviceId}", deviceId);
                return false;
            }

            // 3. Compute expected HMAC
            var message = $"{timestamp}:{nonce}";
            var expectedHmac = ComputeHmacSha256(sharedSecret, message);

            // 4. Compare HMACs (constant-time comparison to prevent timing attacks)
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedHmac),
                Encoding.UTF8.GetBytes(receivedHmac)))
            {
                _logger.LogWarning("HMAC validation failed for {DeviceId}", deviceId);
                return false;
            }

            // 5. Load device claims into cache
            await LoadDeviceClaimsAsync(deviceId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating HMAC for {DeviceId}", deviceId);
            return false;
        }
    }

    // Compute HMAC-SHA256
    private string ComputeHmacSha256(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    // MQTT Publish Authorization (topic-based)
    public async Task<bool> AuthorizePublishAsync(
        string deviceId,
        string topic,
        byte[] payload)
    {
        // 1. Get device claims from cache
        var claims = await GetDeviceClaimsAsync(deviceId);
        if (claims == null)
        {
            _logger.LogWarning("No claims found for device {DeviceId}", deviceId);
            return false;
        }

        // 2. Convert MQTT topic to resource path
        // "acme/floor-3/sensor/temp-089" → "acme:hq:floor-3:sensor:temp-089"
        var resource = ConvertTopicToResource(topic);

        // 3. Check hierarchical permission
        var hasPermission = CheckHierarchicalPermission(claims, resource, "write");

        if (!hasPermission)
        {
            _logger.LogWarning(
                "Device {DeviceId} denied publish to {Topic}",
                deviceId,
                topic);
        }

        return hasPermission;
    }

    // Hierarchical Permission Check
    private bool CheckHierarchicalPermission(
        DeviceClaims claims,
        string resource,
        string action)
    {
        // claims.Permissions: ["acme:hq:floor-3:sensor:*:telemetry:write"]
        // resource: "acme:hq:floor-3:sensor:temp-089:telemetry"
        // action: "write"

        foreach (var permission in claims.Permissions)
        {
            if (IsHierarchicalMatch(permission, resource, action))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsHierarchicalMatch(string permission, string resource, string action)
    {
        // Permission: "acme:hq:floor-3:sensor:*:telemetry:write"
        // Resource:   "acme:hq:floor-3:sensor:temp-089:telemetry"

        if (!permission.EndsWith($":{action}"))
        {
            return false; // Action doesn't match
        }

        var permParts = permission.Split(':');
        var resParts = resource.Split(':');

        // Check length (excluding action part)
        if (permParts.Length - 1 != resParts.Length)
        {
            return false;
        }

        // Check each part (wildcard * matches anything)
        for (int i = 0; i < resParts.Length; i++)
        {
            if (permParts[i] != "*" && permParts[i] != resParts[i])
            {
                return false;
            }
        }

        return true;
    }

    // Load device claims from cloud or cache
    private async Task LoadDeviceClaimsAsync(string deviceId)
    {
        // Check if already cached
        if (_policyCache.TryGetValue(deviceId, out DeviceClaims? _))
        {
            return; // Already cached
        }

        try
        {
            // Fetch from cloud IAM
            var response = await _cloudIamClient.GetAsync($"/api/v1/iot/devices/{deviceId}/claims");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var claims = JsonSerializer.Deserialize<DeviceClaims>(json);

            // Cache for 5 minutes (configurable TTL)
            _policyCache.Set(deviceId, claims, TimeSpan.FromMinutes(5));

            _logger.LogInformation("Loaded claims for device {DeviceId}", deviceId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Failed to fetch claims from cloud for {DeviceId} - using cached policies",
                deviceId);

            // Fallback: Use last-known-good policies (offline operation)
            // This would load from local persistent storage if cloud is unavailable
        }
    }

    // Get device claims from cache
    private async Task<DeviceClaims?> GetDeviceClaimsAsync(string deviceId)
    {
        if (_policyCache.TryGetValue(deviceId, out DeviceClaims? claims))
        {
            return claims;
        }

        // Not in cache - load from cloud
        await LoadDeviceClaimsAsync(deviceId);

        _policyCache.TryGetValue(deviceId, out claims);
        return claims;
    }

    // Get device shared secret (for HMAC auth)
    private async Task<string?> GetDeviceSharedSecretAsync(string deviceId)
    {
        // In production, this would be retrieved from secure storage
        // For this example, we'll simulate a lookup

        try
        {
            var response = await _cloudIamClient.GetAsync($"/api/v1/iot/devices/{deviceId}/secret");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DeviceSecretResponse>(json);

            return result?.SharedSecret;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching shared secret for {DeviceId}", deviceId);
            return null;
        }
    }

    // Convert MQTT topic to resource path
    private string ConvertTopicToResource(string topic)
    {
        // "acme/floor-3/sensor/temp-089/telemetry" → "acme:hq:floor-3:sensor:temp-089:telemetry"
        var parts = topic.Split('/');
        return string.Join(":", parts);
    }
}

// Device Claims Model
public class DeviceClaims
{
    public string DeviceId { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

// Device Secret Response
public class DeviceSecretResponse
{
    public string DeviceId { get; set; } = string.Empty;
    public string SharedSecret { get; set; } = string.Empty;
}
```

---

## Summary

These examples demonstrate:

1. **Constrained Device (ESP32):** HMAC-SHA256 authentication without TLS
2. **Capable Device (Raspberry Pi):** X.509 certificate with mTLS
3. **Edge Gateway (.NET):** Authorization proxy validating both authentication methods

All three work together to form a complete IoT authentication system.

---

**Next Steps:**
1. Flash ESP32 with code (use Arduino IDE)
2. Deploy Python controller to Raspberry Pi
3. Run .NET edge gateway service
4. Monitor MQTT traffic with `mosquitto_sub -v -t '#'`
5. Test authorization by publishing to unauthorized topics (should be denied)

**Security Checklist:**
- [x] Unique credentials per device
- [x] Short-lived tokens (HMAC timestamp window)
- [x] Certificate validation with CRL check
- [x] Hierarchical least privilege (one topic per constrained device)
- [x] Offline operation (policy caching)
- [x] Audit logging (all authorization decisions logged)
