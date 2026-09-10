using System.Collections.Concurrent;

namespace NET.Deploy.Api.Logic.Git;

public static class RepositoryLockManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim Get(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Locks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
    }
}
