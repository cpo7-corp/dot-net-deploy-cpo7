using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NET.Deploy.Api.Data.Entities;

[BsonIgnoreExtraElements]
public class ServiceDefinitionDB
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>Relative path to the .csproj inside the cloned repo</summary>
    public string ProjectPath { get; set; } = string.Empty;

    public string IisSiteName { get; set; } = string.Empty;

    /// <summary>WebApi | Mvc | Worker | Angular | React</summary>
    public string ServiceType { get; set; } = "WebApi";

    public bool CompileSingleFile { get; set; } = false;

    public List<ServiceEnvironmentConfig> Environments { get; set; } = new();

    public DateTime? LastDeployed { get; set; }

    public int Order { get; set; }
}

public class ServiceEnvironmentConfig
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string DeployTargetPath { get; set; } = string.Empty;
    public string HeartbeatUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";

    /// <summary>IDs of EnvConfigSetDB to apply</summary>
    public List<string> ConfigSetIds { get; set; } = new();

    public ProjectVersion? CurrentVersion { get; set; }
}

public class ProjectVersion
{
    public string CommitHash { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}
