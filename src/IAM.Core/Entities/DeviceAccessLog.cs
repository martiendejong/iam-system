namespace IAM.Core.Entities;

/// <summary>
/// Audit log for IoT device access (viewing, streaming, controlling)
/// </summary>
public class DeviceAccessLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DeviceId { get; set; }
    public IoTDevice? Device { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// What action was performed
    /// </summary>
    public DeviceAccessAction Action { get; set; }

    /// <summary>
    /// When the access occurred
    /// </summary>
    public DateTime AccessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Duration of access (for streaming sessions)
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Success or failure
    /// </summary>
    public bool Success { get; set; } = true;

    /// <summary>
    /// Error message if access failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// IP address of the user
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent / client information
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Additional context as JSON
    /// </summary>
    public string? Context { get; set; }
}

public enum DeviceAccessAction
{
    View,           // Viewed device information
    Stream,         // Started streaming from device
    StopStream,     // Stopped streaming
    Control,        // Sent control command to device
    Configure,      // Changed device configuration
    Reboot,         // Rebooted device
    Update,         // Updated firmware/software
    Delete          // Removed device
}
