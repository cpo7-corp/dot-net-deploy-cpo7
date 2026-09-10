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

    /// <summary>Relative path to the .csproj or dockerfile inside the cloned repo</summary>
    public string ProjectPath { get; set; } = string.Empty;

    public string IisSiteName { get; set; } = string.Empty;

    /// <summary>WebApi | Mvc | Worker | Angular | React | Docker | DockerCompose</summary>
    public string ServiceType { get; set; } = "WebApi";

    public bool CompileSingleFile { get; set; } = false;

    // Docker Specific Settings
    public string? DockerfilePath { get; set; }
    public string? DockerComposePath { get; set; }
    public string? DockerContainerName { get; set; }
    public string? DockerComposeServiceName { get; set; }
    public string? DockerComposeProjectName { get; set; }

    public List<ServiceEnvironmentConfig> Environments { get; set; } = new();

    public DateTime? LastDeployed { get; set; }

    public int Order { get; set; }
}

public class ServiceEnvironmentConfig
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string DeployTargetPath { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Range(1, 65535)]
    public int? IisPort { get; set; }

    // Docker Environment Settings
    public int? DockerPort { get; set; }
    public int DockerReplicas { get; set; } = 2;
    public int DockerParallelism { get; set; } = 1;
    public int DockerDrainSeconds { get; set; } = 10;
    public bool EnableZeroDowntime { get; set; } = true;
    public string? DockerEnvironmentComposePath { get; set; }

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
