using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Data.Entities;

namespace NET.Deploy.Api.Logic.DeployHistory;

public class DeployHistoryLogic(MongoDbContext db)
{
    public async Task AddAsync(DeploymentHistoryDB history)
    {
        await db.DeployHistory.InsertOneAsync(history);
    }

    public async Task<List<DeploymentHistoryDB>> GetByServiceAsync(string serviceId, string environmentId, int limit = 10)
    {
        return await db.DeployHistory
            .Find(h => h.ServiceId == serviceId && h.EnvironmentId == environmentId)
            .SortByDescending(h => h.Created)
            .Limit(limit)
            .ToListAsync();
    }
}
