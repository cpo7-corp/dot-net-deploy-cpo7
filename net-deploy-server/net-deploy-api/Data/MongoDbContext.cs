using MongoDB.Driver;
using NET.Deploy.Api.Logic.DeployLogs.Entities;
using NET.Deploy.Api.Logic.Services.Entities;
using NET.Deploy.Api.Logic.Settings.Entities;

namespace NET.Deploy.Api.Data;

public class MongoDbContext
{
    private readonly IMongoDatabase _database;

    public MongoDbContext(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB:ConnectionString is not configured.");
        var databaseName = configuration["MongoDB:DatabaseName"]
            ?? throw new InvalidOperationException("MongoDB:DatabaseName is not configured.");

        var client = new MongoClient(connectionString);
        _database = client.GetDatabase(databaseName);
    }

    /// <summary>Singleton document – application settings (Git + VPS)</summary>
    public IMongoCollection<AppSettings> Settings =>
        _database.GetCollection<AppSettings>("settings");

    /// <summary>One document per deployable .NET service</summary>
    public IMongoCollection<ServiceDefinition> Services =>
        _database.GetCollection<ServiceDefinition>("services");

    /// <summary>Log lines written during deploy runs</summary>
    public IMongoCollection<DeployLogEntry> DeployLogs =>
        _database.GetCollection<DeployLogEntry>("deploy_logs");
}
