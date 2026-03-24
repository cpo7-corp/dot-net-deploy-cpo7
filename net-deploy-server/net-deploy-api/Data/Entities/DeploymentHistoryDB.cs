using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NET.Deploy.Api.Data.Entities;

[BsonIgnoreExtraElements]
public class DeploymentHistoryDB
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string ServiceId { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public ProjectVersion Version { get; set; } = new();

    /// <summary>IDs of ConfigSetDB used at deploy time</summary>
    public List<string> ConfigSetIds { get; set; } = new();

    public string? CommitHash { get; set; } // Shortcut
}
