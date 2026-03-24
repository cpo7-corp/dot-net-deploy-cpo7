using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NET.Deploy.Api.Data.Entities;

[BsonIgnoreExtraElements]
public class AppSettingsDB
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public GitSettings Git { get; set; } = new();

    public List<VpsSettings> VpsEnvironments { get; set; } = new();
}

public class GitSettings
{
    public string Token { get; set; } = string.Empty;
    public string LocalBaseDir { get; set; } = string.Empty;
}

public class VpsSettings
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public int Port { get; set; } = 22;
    public bool IsLocal { get; set; } = false;

    /// <summary>Optional prefix for automatic appsettings.json rename (e.g. appsettings.prod.json)</summary>
    public string EnvironmentTag { get; set; } = string.Empty;

    public List<EnvVariable>? SharedVariables { get; set; }
    public List<FileRename>? SharedFileRenames { get; set; }
}
