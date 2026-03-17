using System.Diagnostics;
using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.Services.Entities;
using NET.Deploy.Api.Logic.Settings.Entities;
using Renci.SshNet;

namespace NET.Deploy.Api.Logic.Deploy;

public delegate Task LogCallback(string level, string message, string? serviceId = null);

public class DeployLogic(
    ILogger<DeployLogic> logger,
    GitLogic gitLogic)
{
    /// <summary>
    /// Full deploy pipeline for a single service:
    /// 1. git pull
    /// 2. dotnet publish
    /// 3. Copy output to VPS deploy path (local copy for now; SSH/WinSCP optional)
    /// </summary>
    public async Task<bool> DeployServiceAsync(
        ServiceDefinition service,
        AppSettings settings,
        LogCallback log,
        string? branchOverride = null,
        VpsSettings? vpsOverride = null,
        bool forceClean = false,
        bool skipPull = false)
    {
        var serviceId = service.Id;
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (attempt > 1)
            {
                await log("WARNING", $"🔄 Retry attempt {attempt}/{maxRetries} for {service.Name}...", serviceId);
                await Task.Delay(5000); // Wait 5s before retry
            }

            try
            {
                // Only use forceClean on the very first attempt of the batch.
                // Subsequent retries should be incremental to save time.
                var effectiveClean = attempt == 1 && forceClean;
                var success = await DeployServiceInternalAsync(service, settings, log, branchOverride, vpsOverride, effectiveClean, skipPull);
                if (success) return true;
            }
            catch (Exception ex)
            {
                await log("ERROR", $"💥 Unexpected error during deploy: {ex.Message}", serviceId);
            }

            if (attempt == maxRetries)
            {
                await log("ERROR", $"❌ Failed to deploy {service.Name} after {maxRetries} attempts.", serviceId);
            }
        }

        return false;
    }

    public async Task<bool> PrepGitOnlyAsync(
        ServiceDefinition service,
        AppSettings settings,
        LogCallback log,
        string? branchOverride = null,
        bool forceClean = false)
    {
        var (repoUrl, branch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
        var effectiveBranch = !string.IsNullOrWhiteSpace(branchOverride) ? branchOverride 
                           : (!string.IsNullOrWhiteSpace(service.Branch) && service.Branch != "main" ? service.Branch : branch);
        var effectiveProjectPath = string.IsNullOrWhiteSpace(service.ProjectPath) ? projectPath : service.ProjectPath;

        await log("INFO", $"📥 [Prep] Pulling Git for {service.Name} ({effectiveBranch})...", service.Id);
        return await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean);
    }

    private async Task<bool> DeployServiceInternalAsync(
        ServiceDefinition service,
        AppSettings settings,
        LogCallback log,
        string? branchOverride = null,
        VpsSettings? vpsOverride = null,
        bool forceClean = false,
        bool skipPull = false)
    {
        var serviceId = service.Id;

        // Automatically parse repo/branch/path if a full GitHub URL is provided
        var (repoUrl, branch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);

        // Branch Selection (Precedence: 1: Override from deploy request, 2: Service record, 3: parsed from URL)
        var effectiveBranch = !string.IsNullOrWhiteSpace(branchOverride) ? branchOverride 
                           : (!string.IsNullOrWhiteSpace(service.Branch) && service.Branch != "main" ? service.Branch : branch);
        var effectiveProjectPath = string.IsNullOrWhiteSpace(service.ProjectPath) ? projectPath : service.ProjectPath;

        await log("INFO", $"▶ Starting deploy for: {service.Name}", serviceId);

        // Step 1 – git pull
        if (!skipPull)
        {
            await log("INFO", $"📥 Pulling latest from Git ({repoUrl}, branch: {effectiveBranch})...", serviceId);
            var pulled = await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean);
            if (!pulled)
            {
                await log("ERROR", "❌ Git pull failed.", serviceId);
                return false;
            }
            await log("SUCCESS", "✅ Git pull successful.", serviceId);
        }
        else
        {
            await log("INFO", "⏭️ Skipping Git pull (already prepared).", serviceId);
        }

        // Step 2 – Build & Publish
        var repoLocalPath = gitLogic.GetRepoLocalPath(settings.Git, repoUrl, effectiveProjectPath);
        var projectFullPath = Path.Combine(repoLocalPath, effectiveProjectPath);

        var publishOutput = Path.Combine(
            Path.GetTempPath(),
            "net-deploy",
            service.Id ?? service.Name);

        if (Directory.Exists(publishOutput))
        {
            try {
                Directory.Delete(publishOutput, true);
                await log("INFO", "🧹 Cleared old publish output folder.", serviceId);
            } catch (Exception ex) {
                await log("WARNING", $"Could not clear old publish folder: {ex.Message}");
            }
        }

        var isNode = service.ServiceType is "Angular" or "React" || effectiveProjectPath.EndsWith("package.json", StringComparison.OrdinalIgnoreCase);
        var isWindowsService = service.ServiceType == "WindowsService";

        // Step 2 – Stop service if it's a Windows Service
        if (isWindowsService)
        {
            await log("INFO", $"🛑 Stopping Windows Service: {service.IisSiteName}...", serviceId);
            await RunProcessAsync("sc.exe", $"stop {service.IisSiteName}", ".", log, serviceId);
            await Task.Delay(2000); // Wait bit for stop
        }

        await log("INFO", $"🔨 Building & publishing: {service.ProjectPath}", serviceId);
        
        bool buildSuccess = false;
        if (isNode)
        {
            buildSuccess = await RunNpmBuildAsync(projectFullPath, publishOutput, log, serviceId, service.ServiceType);
        }
        else
        {
            buildSuccess = await RunDotnetPublishAsync(projectFullPath, publishOutput, log, serviceId);
        }

        if (!buildSuccess)
        {
            if (isWindowsService) await RunProcessAsync("sc.exe", $"start {service.IisSiteName}", ".", log, serviceId);
            await log("ERROR", "❌ Build/publish failed.", serviceId);
            return false;
        }
        await log("SUCCESS", "✅ Build successful.", serviceId);

        // Step 3 – Copy/Upload files
        var targetPath = service.DeployTargetPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            await log("ERROR", "❌ Missing DeployTargetPath for service.", serviceId);
            return false;
        }

        try
        {
            if (vpsOverride != null && !string.IsNullOrWhiteSpace(vpsOverride.Host) && vpsOverride.Host != "localhost" && vpsOverride.Host != "127.0.0.1")
            {
                await log("INFO", $"🚀 Uploading files to remote VPS: {vpsOverride.Host} at {targetPath}...", serviceId);
                await UploadDirectoryToRemoteAsync(publishOutput, targetPath, vpsOverride, log, serviceId);
                await log("SUCCESS", $"✅ Files uploaded to remote: {vpsOverride.Host}", serviceId);
            }
            else
            {
                await log("INFO", $"📂 Copying to: {targetPath} (local)", serviceId);
                CopyDirectory(publishOutput, targetPath);
                await log("SUCCESS", $"✅ Files copied locally to {targetPath}", serviceId);
            }
        }
        catch (Exception ex)
        {
            if (isWindowsService) await RunProcessAsync("sc.exe", $"start {service.IisSiteName}", ".", log, serviceId);
            await log("ERROR", $"❌ Transfer failed: {ex.Message}", serviceId);
            logger.LogError(ex, "Failed to copy/upload files to {Target}", targetPath);
            return false;
        }

        // Step 4 – Start service if it's a Windows Service
        if (isWindowsService)
        {
            await log("INFO", $"🏁 Starting Windows Service: {service.IisSiteName}...", serviceId);
            await RunProcessAsync("sc.exe", $"start {service.IisSiteName}", ".", log, serviceId);
        }

        await log("SUCCESS", $"🚀 Deploy complete for: {service.Name}", serviceId);
        return true;
    }

    // ─────────────────────────────── helpers ───────────────────────────────

    private async Task<bool> RunProcessAsync(string command, string arguments, string workingDir, LogCallback log, string? serviceId)
    {
        var psi = new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await outputTask;
        var stderr = await errorTask;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            await log("INFO", line.TrimEnd(), serviceId);

        if (process.ExitCode != 0)
        {
            foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                await log("ERROR", line.TrimEnd(), serviceId);
            return false;
        }

        return true;
    }

    private async Task<bool> RunNpmBuildAsync(string projectPath, string outputPath, LogCallback log, string? serviceId, string serviceType)
    {
        var projectDir = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath)!;
        var isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        
        // Use cmd /c on Windows to avoid issues with .cmd files and environment resolution
        string Run(string verb, string args) => isWindows ? "cmd.exe" : verb;
        string Args(string verb, string args) => isWindows ? $"/c \"{verb} {args}\"" : args;

        await log("INFO", $"📦 Running npm install in: {projectDir}", serviceId);
        if (!await RunProcessAsync(Run("npm", "install"), Args("npm", "install"), projectDir, log, serviceId))
            return false;

        if (serviceType == "Angular")
        {
            await log("INFO", "🏗️ Running ng build --configuration production...", serviceId);
            if (!await RunProcessAsync(Run("npx", "ng build --configuration production"), Args("npx", "ng build --configuration production"), projectDir, log, serviceId))
                return false;
        }
        else
        {
            await log("INFO", "🏗️ Running npm run build...", serviceId);
            if (!await RunProcessAsync(Run("npm", "run build"), Args("npm", "run build"), projectDir, log, serviceId))
                return false;
        }

        string? builtFolder = null;
        var distPath = Path.Combine(projectDir, "dist");
        var buildPath = Path.Combine(projectDir, "build");

        if (Directory.Exists(distPath))
            builtFolder = distPath;
        else if (Directory.Exists(buildPath))
            builtFolder = buildPath;

        if (builtFolder != null)
        {
            // Angular 17+ / React puts index.html inside a subdirectory sometimes
            var indexFiles = Directory.GetFiles(builtFolder, "index.html", SearchOption.AllDirectories);
            if (indexFiles.Length > 0)
            {
                builtFolder = Path.GetDirectoryName(indexFiles[0]); // directory containing index.html
            }
            
            CopyDirectory(builtFolder!, outputPath);
            return true;
        }

        await log("ERROR", "❌ Could not find 'dist' or 'build' output folder.", serviceId);
        return false;
    }

    private async Task<bool> RunDotnetPublishAsync(
        string projectPath,
        string outputPath,
        LogCallback log,
        string? serviceId)
    {
        var projectDir = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath)!;
        var args = $"publish \"{projectPath}\" -c Release -o \"{outputPath}\" --nologo";
        await log("INFO", $"🏗️ Running dotnet publish in: {projectDir}", serviceId);
        return await RunProcessAsync("dotnet", args, projectDir, log, serviceId);
    }

    private async Task UploadDirectoryToRemoteAsync(string localPath, string remotePath, VpsSettings vps, LogCallback log, string? serviceId)
    {
        using var client = new SftpClient(vps.Host, vps.Port > 0 ? vps.Port : 22, vps.Username, vps.Password);
        client.Connect();

        // Standardize remote path (Windows-style or Unix-style based on what the SFTP server expects)
        var root = remotePath.Replace("\\", "/");

        async Task Upload(string localDir, string remoteDir)
        {
            // Create remote dir if not exists
            if (!client.Exists(remoteDir))
            {
                EnsureRemoteDirectory(client, remoteDir);
            }

            foreach (var file in Directory.GetFiles(localDir))
            {
                using var fs = File.OpenRead(file);
                var dest = remoteDir + "/" + Path.GetFileName(file);
                client.UploadFile(fs, dest);
                // Optional: await log("INFO", $"  Uploaded {Path.GetFileName(file)}", serviceId);
            }

            foreach (var subDir in Directory.GetDirectories(localDir))
            {
                var dirName = Path.GetFileName(subDir);
                await Upload(subDir, remoteDir + "/" + dirName);
            }
        }

        await Upload(localPath, root);
        client.Disconnect();
    }

    private void EnsureRemoteDirectory(SftpClient client, string path)
    {
        if (client.Exists(path)) return;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        
        int startIndex = 0;
        // Handle absolute Windows paths like C:/ or /C:/ via SFTP
        if (path.Contains(":/"))
        {
            current = parts[0] + "/";
            startIndex = 1;
        }
        else if (path.StartsWith("/"))
        {
            current = "/";
        }

        for (int i = startIndex; i < parts.Length; i++)
        {
            current += (current.EndsWith("/") ? "" : "/") + parts[i];
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var subDest = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, subDest);
        }
    }
}
