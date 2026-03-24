using NET.Deploy.Api.Data.Entities;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace NET.Deploy.Api.Logic.Git;

public class GitLogic(ILogger<GitLogic> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new();
    private static SemaphoreSlim GetRepoLock(string path) => _repoLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Clones the repo if it does not exist locally, otherwise pulls latest changes.
    /// </summary>
    public async Task<bool> PullAsync(GitSettings git, string repoUrl, string branch, Deploy.LogCallback? log = null, string? sparsePath = null, bool forceClean = false)
    {
        var repoDirName = GetSafeRepoName(repoUrl);
        var repoPath = Path.Combine(git.LocalBaseDir, repoDirName);

        var @lock = GetRepoLock(repoPath);
        await @lock.WaitAsync();

        try
        {
            // Ensure base directory exists
            Directory.CreateDirectory(git.LocalBaseDir);

            var authUrl = BuildAuthUrl(git, repoUrl);

            // 1. Force Clean Logic
            if (Directory.Exists(repoPath) && forceClean)
            {
                try
                {
                    if (log != null) await log("INFO", $"🧹 [Clean] Deleting existing directory to ensure fresh clone...");
                    DeleteDirectoryRecursively(repoPath);
                }
                catch (Exception ex)
                {
                    if (log != null) await log("WARNING", $"Could not fully clear folder (files might be in use): {ex.Message}");
                    // If we can't clear it but want a fresh clone, we have a problem. 
                    // However, we'll try to let git handle it or fail later.
                }
            }

            // 2. Clone/Sparse-Checkout Logic (if .git is missing)
            if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            {
                if (Directory.Exists(repoPath))
                {
                    try
                    {
                        DeleteDirectoryRecursively(repoPath);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) await log("ERROR", "❌ Cannot clone: Target folder exists and is not a git repo, and cannot be deleted. Try manual cleanup.");
                        return false;
                    }
                }

                logger.LogInformation("Cloning repo {Url} into {Path}", repoUrl, repoPath);
                if (log != null) await log("INFO", $"📥 Starting fresh clone (branch: {branch})...");
                Directory.CreateDirectory(repoPath);

                var cloneArgs = $"clone -b {branch} {authUrl} .";

                if (!await RunGitAsync(repoPath, cloneArgs, "Cloning", log))
                    return false;

                return true;
            }

            // 3. Incremental Update Logic
            logger.LogInformation("Updating existing repo from branch {Branch} in {Repo}", branch, repoUrl);
            if (log != null) await log("INFO", $"♻️ Updating existing repo (incremental pull)...");

            // Clean any local locks/index issues if they exist
            var lockFile = Path.Combine(repoPath, ".git", "index.lock");
            if (File.Exists(lockFile)) File.Delete(lockFile);

            if (!await RunGitAsync(repoPath, "fetch --all", "Fetch", log))
                return false;

            // If branch looks like a commit hash (7-40 chars hex), reset directly to it. Otherwise use origin prefix.
            bool isHash = branch.Length >= 7 && branch.All(c => "0123456789abcdefABCDEF".Contains(c));
            string resetCmd = isHash ? $"reset --hard {branch}" : $"reset --hard origin/{branch}";

            return await RunGitAsync(repoPath, resetCmd, "Reset", log);
        }
        finally
        {
            @lock.Release();
        }
    }

    private void DeleteDirectoryRecursively(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }

    public string GetRepoLocalPath(GitSettings git, string repoUrl, string? sparsePath = null)
    {
        return Path.Combine(git.LocalBaseDir, GetSafeRepoName(repoUrl));
    }

    private string GetSafeRepoName(string repoUrl)
    {
        var uri = new Uri(repoUrl);
        var name = uri.AbsolutePath.Trim('/').Replace("/", "_").Replace(".git", "");
        return string.IsNullOrWhiteSpace(name) ? "unknown_repo" : name;
    }

    public (string RepoUrl, string Branch, string ProjectPath) ParseGitUrl(string fullUrl)
    {
        // Example: https://github.com/user/repo/blob/main/src/Proj/Proj.csproj
        if (fullUrl.Contains("/blob/") || fullUrl.Contains("/tree/"))
        {
            var delimiter = fullUrl.Contains("/blob/") ? "/blob/" : "/tree/";
            var parts = fullUrl.Split(delimiter);
            var repoPart = parts[0].TrimEnd('/');
            var subParts = parts[1].Split('/');
            var branch = subParts[0];
            var path = string.Join("/", subParts.Skip(1));

            var repoUrl = repoPart.EndsWith(".git") ? repoPart : repoPart + ".git";
            return (repoUrl, branch, path);
        }

        // Just a directory link?
        if (fullUrl.Contains("github.com") && !fullUrl.EndsWith(".git") && fullUrl.Count(c => c == '/') > 4)
        {
            // Possibly https://github.com/user/repo/src/Folder
            // Not a standard format we handle well yet, but let's try
        }

        return (fullUrl.EndsWith(".git") ? fullUrl : fullUrl + ".git", "main", "");
    }

    public Task<List<string>> ListProjectsAsync(string localPath)
    {
        if (!Directory.Exists(localPath))
            return Task.FromResult(new List<string>());

        var projects = Directory
            .GetFiles(localPath, "*.csproj", SearchOption.AllDirectories)
            .ToList();

        var packageJsons = Directory
            .GetFiles(localPath, "package.json", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/node_modules/"))
            .ToList();

        projects.AddRange(packageJsons);

        var result = projects
            .Select(f => Path.GetRelativePath(localPath, f).Replace('\\', '/'))
            .OrderBy(p => p)
            .ToList();

        return Task.FromResult(result);
    }

    public async Task<ProjectVersion?> GetCurrentCommitAsync(string repoLocalPath, string branch)
    {
        var result = await RunGitWithOutputAsync(repoLocalPath, $"log -1 --format=\"%H|%an|%at|%s\" {branch}");
        if (string.IsNullOrWhiteSpace(result)) return null;

        var parts = result.Split('|');
        if (parts.Length < 4) return null;

        if (!long.TryParse(parts[2], out var unixTime)) return null;

        return new ProjectVersion
        {
            CommitHash = parts[0],
            Author = parts[1],
            Updated = DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime,
            CommitMessage = string.Join("|", parts.Skip(3)),
            Branch = branch
        };
    }

    public async Task<List<ProjectVersion>> ListRecentCommitsAsync(string repoLocalPath, string branch, int skip = 0, int take = 20)
    {
        var result = await RunGitWithOutputAsync(repoLocalPath, $"log {branch} --skip={skip} -n {take} --format=\"%H|%an|%at|%s\"");
        if (string.IsNullOrWhiteSpace(result)) return new();

        var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<ProjectVersion>();

        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length < 4) continue;

            if (!long.TryParse(parts[2], out var unixTime)) continue;

            list.Add(new ProjectVersion
            {
                CommitHash = parts[0],
                Author = parts[1],
                Updated = DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime,
                CommitMessage = string.Join("|", parts.Skip(3)),
                Branch = branch
            });
        }
        return list;
    }

    private async Task<string> RunGitWithOutputAsync(string workingDir, string arguments)
    {
        if (!Directory.Exists(workingDir)) return "";

        var gitExe = Deploy.ExeResolver.Resolve("git");
        var psi = new ProcessStartInfo(gitExe, arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) return "";
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }

    // ─────────────────────────────── helpers ───────────────────────────────

    public async Task<List<string>> GetRepositoriesAsync(GitSettings git)
    {
        if (string.IsNullOrWhiteSpace(git.Token)) return new List<string>();

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "NET-Deploy-App");
        // GitHub recommends "token" or "Bearer". Let's try "token" which is classic for PATs.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"token {git.Token}");

        try
        {
            var url = "https://api.github.com/user/repos?per_page=100&sort=updated";
            var response = await client.GetAsync(url);

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("GitHub API error {Status}: {Content}", response.StatusCode, content);
                return new List<string>();
            }

            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                logger.LogWarning("GitHub API returned non-array: {Content}", content);
                return new List<string>();
            }

            var repos = doc.RootElement.EnumerateArray()
                .Select(r => r.GetProperty("clone_url").GetString() ?? "")
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();

            logger.LogInformation("Fetched {Count} repos from GitHub.", repos.Count);
            return repos;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch repositories from GitHub");
            return new List<string>();
        }
    }

    private static string BuildAuthUrl(GitSettings git, string repoUrl)
    {
        if (string.IsNullOrWhiteSpace(git.Token)) return repoUrl;

        var uri = new Uri(repoUrl);
        // Using x-access-token for GitHub App/Fine-grained PAT tokens
        return $"{uri.Scheme}://x-access-token:{git.Token}@{uri.Host}{uri.PathAndQuery}";
    }


    private async Task<bool> RunGitAsync(string workingDir, string arguments, string operation, Deploy.LogCallback? log = null)
    {
        Directory.CreateDirectory(workingDir);

        var gitExe = Deploy.ExeResolver.Resolve("git");
        // -c credential.helper= forces git to ignore any locally saved credentials on the machine
        var fullArgs = $"-c credential.helper= {arguments}";
        var psi = new ProcessStartInfo(gitExe, fullArgs)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Prevent git from hanging on auth prompts
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            logger.LogError("Git {Op} failed: {Error}", operation, error);
            if (log != null) await log("ERROR", $"Git {operation} failed (code {process.ExitCode}): {error.Trim()}");
            return false;
        }

        logger.LogDebug("Git {Op} output: {Output}", operation, output);
        if (log != null && !string.IsNullOrWhiteSpace(output)) await log("INFO", output.Trim());
        return true;
    }
}
