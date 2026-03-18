using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.Services.Entities;

namespace NET.Deploy.Api.Logic.Services;

public class ServicesLogic(MongoDbContext db)
{
    public async Task<List<ServiceDefinitionDB>> GetAllAsync() =>
        await db.Services.Find(_ => true).ToListAsync();

    public async Task<ServiceDefinitionDB?> GetByIdAsync(string id) =>
        await db.Services.Find(Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id))).FirstOrDefaultAsync();

    public async Task<ServiceDefinitionDB> CreateAsync(ServiceDefinitionDB service)
    {
        service.Id = null; // MongoDB generates the ID
        await db.Services.InsertOneAsync(service);
        return service;
    }

    public async Task<ServiceDefinitionDB?> UpdateAsync(string id, ServiceDefinitionDB updated)
    {
        updated.Id = id;
        var result = await db.Services.ReplaceOneAsync(
            Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            updated);

        return result.MatchedCount > 0 ? updated : null;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var result = await db.Services.DeleteOneAsync(
            Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)));

        return result.DeletedCount > 0;
    }

    public async Task MarkDeployedAsync(string id)
    {
        var update = Builders<ServiceDefinitionDB>.Update
            .Set(s => s.LastDeployed, DateTime.UtcNow);

        await db.Services.UpdateOneAsync(
            Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            update);
    }
}
