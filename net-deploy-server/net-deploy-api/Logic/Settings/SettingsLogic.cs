using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.Settings.Entities;

namespace NET.Deploy.Api.Logic.Settings;

public class SettingsLogic(MongoDbContext db)
{
    public async Task<AppSettingsDB> GetAsync() =>
        await db.Settings.Find(_ => true).FirstOrDefaultAsync() ?? new AppSettingsDB();

    public async Task<AppSettingsDB> SaveAsync(AppSettingsDB settings)
    {
        var existing = await db.Settings.Find(_ => true).FirstOrDefaultAsync();

        if (existing is null)
        {
            await db.Settings.InsertOneAsync(settings);
        }
        else
        {
            settings.Id = existing.Id;
            await db.Settings.ReplaceOneAsync(
                Builders<AppSettingsDB>.Filter.Eq(s => s.Id, existing.Id),
                settings);
        }

        return settings;
    }
}
