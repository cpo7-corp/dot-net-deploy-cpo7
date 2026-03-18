using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.EnvConfigs.Entities;

namespace NET.Deploy.Api.Logic.EnvConfigs;

public class EnvConfigsLogic(MongoDbContext db)
{
    public async Task<List<EnvConfigSetDB>> GetAllAsync() =>
        await db.EnvConfigSets.Find(_ => true).ToListAsync();

    public async Task<EnvConfigSetDB?> GetByIdAsync(string id) =>
        await db.EnvConfigSets.Find(Builders<EnvConfigSetDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id))).FirstOrDefaultAsync();

    public async Task<EnvConfigSetDB> CreateAsync(EnvConfigSetDB configSet)
    {
        configSet.Id = null; // MongoDB generates the ID
        await db.EnvConfigSets.InsertOneAsync(configSet);
        return configSet;
    }

    public async Task<EnvConfigSetDB?> UpdateAsync(string id, EnvConfigSetDB updated)
    {
        updated.Id = id;
        var result = await db.EnvConfigSets.ReplaceOneAsync(
            Builders<EnvConfigSetDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            updated);

        return result.MatchedCount > 0 ? updated : null;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var result = await db.EnvConfigSets.DeleteOneAsync(
            Builders<EnvConfigSetDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)));

        return result.DeletedCount > 0;
    }

    public async Task<List<EnvConfigSetDB>> GetByIdsAsync(IEnumerable<string> ids)
    {
        var objectIds = ids.Select(id => MongoDB.Bson.ObjectId.Parse(id)).ToList();
        var filter = Builders<EnvConfigSetDB>.Filter.In("_id", objectIds);
        return await db.EnvConfigSets.Find(filter).ToListAsync();
    }
}
