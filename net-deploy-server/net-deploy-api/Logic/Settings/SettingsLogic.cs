using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.Settings.Entities;

namespace NET.Deploy.Api.Logic.Settings;

public class SettingsLogic(MongoDbContext db)
{
    public async Task<AppSettings> GetAsync() =>
        await db.Settings.Find(_ => true).FirstOrDefaultAsync() ?? new AppSettings();

    public async Task<AppSettings> SaveAsync(AppSettings settings)
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
                Builders<AppSettings>.Filter.Eq(s => s.Id, existing.Id),
                settings);
        }

        return settings;
    }
}
