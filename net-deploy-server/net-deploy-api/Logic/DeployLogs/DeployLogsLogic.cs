using MongoDB.Driver;
using NET.Deploy.Api.Data;
using NET.Deploy.Api.Logic.DeployLogs.Entities;

namespace NET.Deploy.Api.Logic.DeployLogs;

public class DeployLogsLogic(MongoDbContext db)
{
    public async Task AddAsync(DeployLogEntry entry) =>
        await db.DeployLogs.InsertOneAsync(entry);

    public async Task AddRangeAsync(IEnumerable<DeployLogEntry> entries) =>
        await db.DeployLogs.InsertManyAsync(entries);

    public async Task<List<DeployLogEntry>> GetBySessionAsync(string sessionId) =>
        await db.DeployLogs
            .Find(e => e.SessionId == sessionId)
            .SortBy(e => e.Timestamp)
            .ToListAsync();

    public async Task<List<string>> GetRecentSessionsAsync(int count = 10)
    {
        var pipeline = db.DeployLogs.Aggregate()
            .Group(
                e => e.SessionId,
                g => new { SessionId = g.Key, LastRun = g.Max(x => x.Timestamp) })
            .SortByDescending(g => g.LastRun)
            .Limit(count);

        var result = await pipeline.ToListAsync();
        return result.Select(r => r.SessionId).ToList();
    }
}
