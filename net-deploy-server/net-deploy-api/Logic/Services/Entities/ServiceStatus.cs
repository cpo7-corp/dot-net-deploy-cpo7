namespace NET.Deploy.Api.Logic.Services.Entities;

public class ServiceStatus : ServiceDefinitionDB
{
    /// <summary>Expected: Running, Stopped, Unknown</summary>
    public string Status { get; set; } = "Unknown";
}
