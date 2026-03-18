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

    public record SessionSummary(string SessionId, DateTime Timestamp, bool HasErrors);

    public async Task<List<SessionSummary>> GetSessionsPagedAsync(int skip, int limit)
    {
        var pipeline = db.DeployLogs.Aggregate()
            .Group(
                e => e.SessionId,
                g => new { 
                    SessionId = g.Key, 
                    LastRun = g.Max(x => x.Timestamp),
                    ErrorCount = g.Count(x => x.Level == "ERROR")
                })
            .SortByDescending(g => g.LastRun)
            .Skip(skip)
            .Limit(limit);

        var result = await pipeline.ToListAsync();
        return result.Select(r => new SessionSummary(r.SessionId, r.LastRun, r.ErrorCount > 0)).ToList();
    }

    public async Task<List<string>> GetRecentSessionsAsync(int count = 10)
    {
        var summaries = await GetSessionsPagedAsync(0, count);
        return summaries.Select(s => s.SessionId).ToList();
    }
}
