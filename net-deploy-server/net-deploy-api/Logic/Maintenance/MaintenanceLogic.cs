using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.Settings.Entities;
using NET.Deploy.Api.Logic.Services.Entities;

namespace NET.Deploy.Api.Logic.Maintenance;

public class FullDatabaseExport
{
    public AppSettingsDB? Settings { get; set; }
    public List<ServiceDefinitionDB> Services { get; set; } = new();
    public List<EnvConfigSetDB> EnvConfigs { get; set; } = new();
}

public class MaintenanceLogic(MongoDbContext db)
{
    public async Task<FullDatabaseExport> ExportAsync()
    {
        return new FullDatabaseExport
        {
            Settings = await db.Settings.Find(_ => true).FirstOrDefaultAsync(),
            Services = await db.Services.Find(_ => true).ToListAsync(),
            EnvConfigs = await db.EnvConfigSets.Find(_ => true).ToListAsync()
        };
    }

    public async Task ImportAsync(FullDatabaseExport data)
    {
        // 1. Settings (replace existing)
        if (data.Settings != null)
        {
            await db.Settings.DeleteManyAsync(_ => true);
            data.Settings.Id = null; // Settings singleton ID is not critical
            await db.Settings.InsertOneAsync(data.Settings);
        }

        // 2. Services
        await db.Services.DeleteManyAsync(_ => true);
        if (data.Services.Any())
        {
            // Do NOT clear IDs to preserve links in ConfigSetIds
            await db.Services.InsertManyAsync(data.Services);
        }

        // 3. EnvConfigs
        await db.EnvConfigSets.DeleteManyAsync(_ => true);
        if (data.EnvConfigs.Any())
        {
            // Do NOT clear IDs to preserve links in ServiceDefinitionDB.ConfigSetIds
            await db.EnvConfigSets.InsertManyAsync(data.EnvConfigs);
        }
    }
}
