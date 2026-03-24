using NET.Deploy.Api.Data.Entities;

namespace NET.Deploy.Api.Logic.Services.Entities;

public class ServiceStatus : ServiceDefinitionDB
{
    /// <summary>Expected: Running, Stopped, Unknown</summary>
    public string Status { get; set; } = "Unknown";
}

public class ServiceHeartbeatStatus
{
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Expected: Running, Stopped, Unknown</summary>
    public string Status { get; set; } = "Unknown";

    public int? HttpStatusCode { get; set; }
}
