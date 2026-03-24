using MongoDB.Driver;
using NET.Deploy.Api.Data.Entities;

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
    public IMongoCollection<AppSettingsDB> Settings =>
        _database.GetCollection<AppSettingsDB>("settings");

    /// <summary>One document per deployable .NET service</summary>
    public IMongoCollection<ServiceDefinitionDB> Services =>
        _database.GetCollection<ServiceDefinitionDB>("services");

    public IMongoCollection<EnvConfigSetDB> EnvConfigSets =>
        _database.GetCollection<EnvConfigSetDB>("env_configs");

    /// <summary>Log lines written during deploy runs</summary>
    public IMongoCollection<DeployLogEntryDB> DeployLogs =>
        _database.GetCollection<DeployLogEntryDB>("deploy_logs");

    public IMongoCollection<DeploymentHistoryDB> DeployHistory =>
        _database.GetCollection<DeploymentHistoryDB>("deploy_history");
}
