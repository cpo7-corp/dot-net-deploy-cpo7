using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Data.Entities;

namespace NET.Deploy.Api.Logic.Services;

public class ServicesLogic(MongoDbContext db)
{
    public async Task<List<ServiceDefinitionDB>> GetAllAsync() =>
        await db.Services.Find(_ => true).SortBy(s => s.Order).ToListAsync();

    public async Task<ServiceDefinitionDB?> GetByIdAsync(string id) =>
        await db.Services.Find(Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id))).FirstOrDefaultAsync();

    public async Task<ServiceDefinitionDB> CreateAsync(ServiceDefinitionDB service)
    {
        Prepare(service);
        service.Id = null; // MongoDB generates the ID
        await db.Services.InsertOneAsync(service);
        return service;
    }

    public async Task<ServiceDefinitionDB?> UpdateAsync(string id, ServiceDefinitionDB updated)
    {
        Prepare(updated);
        updated.Id = id;
        var result = await db.Services.ReplaceOneAsync(
            Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            updated);

        return result.MatchedCount > 0 ? updated : null;
    }

    private void Prepare(ServiceDefinitionDB service)
    {
        service.Name = service.Name?.Trim() ?? string.Empty;
        service.Group = service.Group?.Trim() ?? string.Empty;
        service.RepoUrl = service.RepoUrl?.Trim() ?? string.Empty;
        service.ProjectPath = service.ProjectPath?.Trim() ?? string.Empty;
        service.IisSiteName = service.IisSiteName?.Trim() ?? string.Empty;
        service.ServiceType = service.ServiceType?.Trim() ?? "WebApi";

        if (service.Environments != null)
        {
            foreach (var env in service.Environments)
            {
                env.DeployTargetPath = env.DeployTargetPath?.Trim() ?? string.Empty;
                env.HeartbeatUrl = env.HeartbeatUrl?.Trim() ?? string.Empty;
                env.DefaultBranch = env.DefaultBranch?.Trim() ?? "main";
                env.ConfigSetIds = env.ConfigSetIds?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .ToList() ?? new();
            }
        }
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
            .Set(s => s.Updated, DateTime.UtcNow);

        await db.Services.UpdateOneAsync(
            Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(id)),
            update);
    }

    public async Task UpdateVersionAsync(string serviceId, string environmentId, ProjectVersion version)
    {
        var filter = Builders<ServiceDefinitionDB>.Filter.And(
            Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(serviceId)),
            Builders<ServiceDefinitionDB>.Filter.ElemMatch(s => s.Environments, e => e.EnvironmentId == environmentId)
        );

        var update = Builders<ServiceDefinitionDB>.Update
            .Set("Environments.$.CurrentVersion", version)
            .Set(s => s.Updated, DateTime.UtcNow);

        await db.Services.UpdateOneAsync(filter, update);
    }

    public async Task UpdateOrderAsync(List<string> ids)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            var update = Builders<ServiceDefinitionDB>.Update.Set(s => s.Order, i);
            await db.Services.UpdateOneAsync(
                Builders<ServiceDefinitionDB>.Filter.Eq("_id", MongoDB.Bson.ObjectId.Parse(ids[i])),
                update);
        }
    }
}
