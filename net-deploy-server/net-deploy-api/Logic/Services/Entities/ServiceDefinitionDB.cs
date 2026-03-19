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

    /// <summary>WebApi | Mvc | Worker | Angular | React</summary>
    public string ServiceType { get; set; } = "WebApi";

    public bool CompileSingleFile { get; set; } = false;

    public List<ServiceEnvironmentConfigDB> Environments { get; set; } = new();

    public DateTime? LastDeployed { get; set; }
    
    public int Order { get; set; }
}

[BsonIgnoreExtraElements]
public class ServiceEnvironmentConfigDB
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string DeployTargetPath { get; set; } = string.Empty;
    public string HeartbeatUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    
    /// <summary>IDs of EnvConfigSetDB to apply</summary>
    public List<string> ConfigSetIds { get; set; } = new();
}
