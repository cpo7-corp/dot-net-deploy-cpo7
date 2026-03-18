using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NET.Deploy.Api.Logic.EnvConfigs.Entities;

[BsonIgnoreExtraElements]
public class EnvConfigSetDB
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string EnvironmentId { get; set; } = string.Empty;

    public string TargetFileName { get; set; } = "appsettings.json";

    public List<EnvVariable> Variables { get; set; } = new();

    public List<FileRename> FileRenames { get; set; } = new();
}

public class EnvVariable
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class FileRename
{
    public string SourceFileName { get; set; } = string.Empty;
    public string TargetFileName { get; set; } = string.Empty;
}
