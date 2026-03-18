using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NET.Deploy.Api.Logic.Services.Entities;

[BsonIgnoreExtraElements]
public class ServiceDefinitionDB
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>Relative path to the .csproj inside the cloned repo</summary>
    public string ProjectPath { get; set; } = string.Empty;

    public string IisSiteName { get; set; } = string.Empty;

    /// <summary>WebApi | Mvc | Worker</summary>
    public string ServiceType { get; set; } = "WebApi";

    /// <summary>Absolute path on the VPS where built output is deployed</summary>
    public string DeployTargetPath { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";
    public bool CompileSingleFile { get; set; } = false;

    public List<string> EnvConfigSetIds { get; set; } = new();
    public string HeartbeatUrl { get; set; } = string.Empty;
    public DateTime? LastDeployed { get; set; }
}
