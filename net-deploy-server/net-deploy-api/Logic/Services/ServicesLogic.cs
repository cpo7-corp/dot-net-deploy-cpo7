using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.Services.Entities;

namespace NET.Deploy.Api.Logic.Services;

public class ServicesLogic(MongoDbContext db)
{
    public async Task<List<ServiceDefinition>> GetAllAsync() =>
        await db.Services.Find(_ => true).ToListAsync();

    public async Task<ServiceDefinition?> GetByIdAsync(string id) =>
        await db.Services.Find(Builders<ServiceDefinition>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id))).FirstOrDefaultAsync();

    public async Task<ServiceDefinition> CreateAsync(ServiceDefinition service)
    {
        service.Id = null; // MongoDB generates the ID
        await db.Services.InsertOneAsync(service);
        return service;
    }

    public async Task<ServiceDefinition?> UpdateAsync(string id, ServiceDefinition updated)
    {
        updated.Id = id;
        var result = await db.Services.ReplaceOneAsync(
            Builders<ServiceDefinition>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            updated);

        return result.MatchedCount > 0 ? updated : null;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var result = await db.Services.DeleteOneAsync(
            Builders<ServiceDefinition>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)));

        return result.DeletedCount > 0;
    }

    public async Task MarkDeployedAsync(string id)
    {
        var update = Builders<ServiceDefinition>.Update
            .Set(s => s.LastDeployed, DateTime.UtcNow);

        await db.Services.UpdateOneAsync(
            Builders<ServiceDefinition>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            update);
    }
}
