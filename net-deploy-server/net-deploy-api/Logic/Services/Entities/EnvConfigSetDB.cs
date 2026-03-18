using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NET.Deploy.Api.Logic.Services.Entities;

[BsonIgnoreExtraElements]
public class EnvConfigSetDB
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SourceFileName { get; set; } = string.Empty;

    public string TargetFileName { get; set; } = string.Empty;

    public List<EnvVariableDB> Variables { get; set; } = new();
}

[BsonIgnoreExtraElements]
public class EnvVariableDB
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

[BsonIgnoreExtraElements]
public class FileRenameDB
{
    public string SourceFileName { get; set; } = string.Empty;
    public string TargetFileName { get; set; } = string.Empty;
}
