using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace NET.Deploy.Api.Logic.DeployLogs.Entities;

[BsonIgnoreExtraElements]
public class DeployLogEntryDB
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Groups all log lines belonging to a single deploy run</summary>
    public string SessionId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>INFO | SUCCESS | ERROR | WARNING</summary>
    public string Level { get; set; } = "INFO";

    public string Message { get; set; } = string.Empty;

    /// <summary>MongoDB ObjectId of the related ServiceDefinition (optional)</summary>
    public string? ServiceId { get; set; }
}
