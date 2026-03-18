using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.Services.Entities;
using NET.Deploy.Api.Logic.Settings.Entities;

namespace NET.Deploy.Api.Logic.Deploy;

public class DeployLogic(
    ILogger<DeployLogic> logger,
    GitLogic gitLogic,
    BuildManager buildManager,
    TransferManager transferManager,
    ProcessRunner processRunner)
{
    public async Task<bool> DeployServiceAsync(
        ServiceDefinition service,
        AppSettings settings,
        LogCallback log,
        string? branchOverride = null,
        VpsSettings? vpsOverride = null,
        bool forceClean = false,
        bool skipPull = false,
        bool skipBuildIfOutputExists = false)
    {
        var serviceId = service.Id;
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (attempt > 1)
            {
                await log("WARNING", $"🔄 Retry attempt {attempt}/{maxRetries} for {service.Name}...", serviceId);
                await Task.Delay(5000); 
            }

            try
            {
                var effectiveClean = attempt == 1 && forceClean;
                var success = await DeployServiceInternalAsync(service, settings, log, branchOverride, vpsOverride, effectiveClean, skipPull, skipBuildIfOutputExists || attempt > 1);
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
        var effectiveBranch = GetEffectiveBranch(service, branch, branchOverride);
        var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));

        await log("INFO", $"📥 [Prep] Pulling Git for {service.Name} ({effectiveBranch})...", service.Id);
        return await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean);
    }

    private async Task<bool> DeployServiceInternalAsync(
        ServiceDefinition service,
        AppSettings settings,
        LogCallback log,
        string? branchOverride,
        VpsSettings? vpsOverride,
        bool forceClean,
        bool skipPull,
        bool skipBuildIfOutputExists)
    {
        var serviceId = service.Id;
        var publishOutput = Path.Combine(Path.GetTempPath(), "net-deploy", service.Id ?? service.Name);
        var isWindowsService = service.ServiceType == "WindowsService";

        if (skipBuildIfOutputExists && Directory.Exists(publishOutput) && Directory.GetFileSystemEntries(publishOutput).Any())
        {
            await log("INFO", "⏭️ [Skip] Build output already exists and skip requested. Proceeding directly to transfer...", serviceId);
            return await ExecuteTransferPhaseAsync(service, publishOutput, vpsOverride, log, isWindowsService);
        }

        var (repoUrl, branch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
        var effectiveBranch = GetEffectiveBranch(service, branch, branchOverride);
        var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));

        await log("INFO", $"▶ Starting deploy for: {service.Name}", serviceId);

        if (!skipPull)
        {
            await log("INFO", $"📥 Pulling latest from Git ({repoUrl}, branch: {effectiveBranch})...", serviceId);
            if (!await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean))
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

        var repoLocalPath = gitLogic.GetRepoLocalPath(settings.Git, repoUrl, effectiveProjectPath);
        var projectFullPath = Path.Combine(repoLocalPath, effectiveProjectPath);

        if (Directory.Exists(publishOutput))
        {
            try 
            {
                FileHelper.DeleteDirectoryRecursively(publishOutput);
                await log("INFO", "🧹 Cleared old publish output folder.", serviceId);
            } 
            catch (Exception ex) 
            {
                await log("WARNING", $"Could not clear old publish folder: {ex.Message}");
            }
        }

        if (isWindowsService)
            await ManageWindowsServiceAsync(service.IisSiteName, "stop", log, serviceId);
        else if (service.ServiceType is "WebApi" or "Mvc")
            await ManageIisSiteAsync(service.IisSiteName, "stop", log, serviceId);

        await log("INFO", $"🔨 Building & publishing: {effectiveProjectPath}", serviceId);
        
        bool buildSuccess = await buildManager.BuildAsync(projectFullPath, publishOutput, service.ServiceType, service.CompileSingleFile, log, serviceId);

        if (!buildSuccess)
        {
            if (isWindowsService) 
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, serviceId);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, serviceId);
            
            await log("ERROR", "❌ Build/publish failed.", serviceId);
            return false;
        }
        
        await log("SUCCESS", "✅ Build successful.", serviceId);

        return await ExecuteTransferPhaseAsync(service, publishOutput, vpsOverride, log, isWindowsService);
    }

    private async Task<bool> ExecuteTransferPhaseAsync(ServiceDefinition service, string publishOutput, VpsSettings? vpsOverride, LogCallback log, bool isWindowsService)
    {
        var targetPath = service.DeployTargetPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            await log("ERROR", "❌ Missing DeployTargetPath for service.", service.Id);
            return false;
        }

        try
        {
            var transferred = await transferManager.TransferAsync(publishOutput, targetPath, vpsOverride, log, service.Id);
            if (!transferred) throw new Exception("Transfer manager reported failure.");
        }
        catch (Exception ex)
        {
            await log("WARNING", $"🔄 Transfer failed. Attempting to restart service/site to restore availability...", service.Id);
            if (isWindowsService) 
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id);
            
            await log("ERROR", $"❌ Transfer failed after all attempts: {ex.Message}", service.Id);
            logger.LogError(ex, "Failed to copy/upload files to {Target}", targetPath);
            return false;
        }

        if (isWindowsService)
            await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id);
        else if (service.ServiceType is "WebApi" or "Mvc")
            await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id);

        await log("SUCCESS", $"🚀 Deploy complete for: {service.Name}", service.Id);
        
        if (!string.IsNullOrWhiteSpace(service.HeartbeatUrl))
        {
            await CheckHeartbeatAsync(service.HeartbeatUrl, log, service.Id);
        }

        return true;
    }

    private async Task ManageWindowsServiceAsync(string? iisSiteName, string action, LogCallback log, string? serviceId) /* action => start / stop */
    {
        var actionIcon = action == "start" ? "🏁 Starting" : "🛑 Stopping";
        await log("INFO", $"{actionIcon} Windows Service: {iisSiteName}...", serviceId);
        await processRunner.RunAsync("sc.exe", $"{action} {iisSiteName}", ".", log, serviceId);
        if (action == "stop") await Task.Delay(2000); 
    }

    private async Task ManageIisSiteAsync(string? iisSiteName, string action, LogCallback log, string? serviceId)
    {
        var actionIcon = action == "start" ? "🏁 Starting" : "🛑 Stopping";
        await log("INFO", $"{actionIcon} IIS Site & AppPool: {iisSiteName}...", serviceId);
        var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"inetsrv\appcmd.exe");
        if (!File.Exists(appcmd)) appcmd = "appcmd.exe"; // Fallback to path

        // Stop/Start BOTH the Site and the App Pool. 
        // Stopping the App Pool is crucial as it's the w3wp.exe process that locks files.
        await processRunner.RunAsync(appcmd, $"{action} site \"{iisSiteName}\"", ".", log, serviceId);
        await processRunner.RunAsync(appcmd, $"{action} apppool \"{iisSiteName}\"", ".", log, serviceId);
        
        if (action == "stop") await Task.Delay(3000);
    }

    private async Task CheckHeartbeatAsync(string url, LogCallback log, string? serviceId)
    {
        await log("INFO", $"💓 Checking heartbeat: {url} ...", serviceId);
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                await log("SUCCESS", $"✅ Heartbeat OK! Status: {response.StatusCode}", serviceId);
            }
            else
            {
                await log("WARNING", $"⚠️ Heartbeat returned error: {response.StatusCode}", serviceId);
            }
        }
        catch (Exception ex)
        {
            await log("ERROR", $"❌ Heartbeat failed: {ex.Message}", serviceId);
        }
    }

    private static string GetEffectiveBranch(ServiceDefinition service, string parsedBranch, string? branchOverride)
    {
        return !string.IsNullOrWhiteSpace(branchOverride) ? branchOverride 
             : (!string.IsNullOrWhiteSpace(service.Branch) && service.Branch != "main" ? service.Branch : parsedBranch);
    }

    private static string GetEffectiveProjectPath(ServiceDefinition service, string parsedPath)
    {
        return string.IsNullOrWhiteSpace(service.ProjectPath) ? parsedPath : service.ProjectPath;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }
}
