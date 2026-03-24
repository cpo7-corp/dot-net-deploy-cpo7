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
    public string LocalBaseDir { get; set; } = @"C:\deploy-temp";
}

public class VpsSettings
{
    public string Id { get; set; } = Guid.CreateVersion7().ToString();
    public string Name { get; set; } = "Default";
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public bool IsLocal { get; set; } = false;
    public string EnvironmentTag { get; set; } = string.Empty;

    public List<EnvVariable> SharedVariables { get; set; } = new();
    public List<FileRename> SharedFileRenames { get; set; } = new();
}
