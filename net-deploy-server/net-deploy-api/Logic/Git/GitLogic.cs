using System.Diagnostics;
using NET.Deploy.Api.Logic.Settings.Entities;

namespace NET.Deploy.Api.Logic.Git;

public class GitLogic(ILogger<GitLogic> logger)
{
    /// <summary>
    /// Clones the repo if it does not exist locally, otherwise pulls latest changes.
    /// </summary>
    public async Task<bool> PullAsync(GitSettings git, string repoUrl, string branch, Deploy.LogCallback? log = null, string? sparsePath = null)
    {
        var repoDirName = GetSafeRepoName(repoUrl);
        var repoPath = Path.Combine(git.LocalBaseDir, repoDirName);
        var authUrl = BuildAuthUrl(git, repoUrl);

        // Always delete and start fresh as requested
        if (Directory.Exists(repoPath))
        {
            try {
                logger.LogInformation("Deleting existing directory to start fresh: {Path}", repoPath);
                if (log != null) await log("INFO", $"🧹 Deleting existing directory to ensure fresh clone...");
                Directory.Delete(repoPath, true);
            } catch (Exception ex) {
                if (log != null) await log("WARNING", $"Could not fully clear folder: {ex.Message}");
            }
        }

        logger.LogInformation("Cloning repo {Url} into {Path}", repoUrl, repoPath);
        if (log != null) await log("INFO", $"📥 Starting fresh clone (branch: {branch})...");
        Directory.CreateDirectory(repoPath);
        
        var cloneArgs = !string.IsNullOrWhiteSpace(sparsePath) 
            ? $"clone -b {branch} --filter=blob:none --sparse {authUrl} ." 
            : $"clone -b {branch} {authUrl} .";

        if (!await RunGitAsync(repoPath, cloneArgs, "Cloning", log))
            return false;

        if (!string.IsNullOrWhiteSpace(sparsePath))
        {
            if (log != null) await log("INFO", $"🎯 Setting sparse-checkout for: {sparsePath}");
            await RunGitAsync(repoPath, $"sparse-checkout set {sparsePath}", "Sparse-Checkout", log);
        }
        
        return true;
    }

    public string GetRepoLocalPath(GitSettings git, string repoUrl)
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
        // -c credential.helper= forces git to ignore any locally saved credentials on the machine
        var fullArgs = $"-c credential.helper= {arguments}";
        var psi = new ProcessStartInfo("git", fullArgs)
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
