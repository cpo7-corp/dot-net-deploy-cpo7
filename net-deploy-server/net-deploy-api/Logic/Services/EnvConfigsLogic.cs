using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Data.Entities;

namespace NET.Deploy.Api.Logic.Services;

public class EnvConfigsLogic(MongoDbContext db)
{
    public async Task<List<EnvConfigSetDB>> GetAllAsync() =>
        await db.EnvConfigSets.Find(_ => true).ToListAsync();

    public async Task<EnvConfigSetDB?> GetByIdAsync(string id) =>
        await db.EnvConfigSets.Find(Builders<EnvConfigSetDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id))).FirstOrDefaultAsync();

    public async Task<EnvConfigSetDB> CreateAsync(EnvConfigSetDB config)
    {
        Prepare(config);
        config.Id = null; 
        await db.EnvConfigSets.InsertOneAsync(config);
        return config;
    }

    public async Task<EnvConfigSetDB?> UpdateAsync(string id, EnvConfigSetDB updated)
    {
        Prepare(updated);
        updated.Id = id;
        var result = await db.EnvConfigSets.ReplaceOneAsync(
            Builders<EnvConfigSetDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            updated);

        return result.MatchedCount > 0 ? updated : null;
    }

    private void Prepare(EnvConfigSetDB config)
    {
        config.Name = config.Name?.Trim() ?? string.Empty;
        config.SourceFileName = config.SourceFileName?.Trim() ?? string.Empty;
        config.TargetFileName = config.TargetFileName?.Trim() ?? string.Empty;
        
        if (config.Variables != null)
        {
            config.Variables = config.Variables
                .Select(v => new EnvVariable { 
                    Key = v.Key?.Trim() ?? string.Empty, 
                    Value = v.Value?.Trim() ?? string.Empty 
                })
                .Where(v => !string.IsNullOrWhiteSpace(v.Key))
                .ToList();
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var result = await db.EnvConfigSets.DeleteOneAsync(
            Builders<EnvConfigSetDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)));

        return result.DeletedCount > 0;
    }
}
