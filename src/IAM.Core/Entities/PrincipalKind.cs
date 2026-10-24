using System.Text.Json.Serialization;

namespace IAM.Core.Entities;

/// <summary>
/// What kind of principal an identity represents (task 4057).
/// Separate from <see cref="ServiceAccountType"/>, which describes the technical
/// purpose of a service credential (Api/Service/Worker) rather than "who" it is.
/// Serialized as string ("Human"/"Agent"/"Service"); integer values also accepted.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrincipalKind
{
    /// <summary>A real person.</summary>
    Human = 0,

    /// <summary>An AI agent acting with its own identity.</summary>
    Agent = 1,

    /// <summary>A plain machine/service identity.</summary>
    Service = 2
}
