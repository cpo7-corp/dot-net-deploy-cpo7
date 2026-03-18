using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.Services.Entities;
using NET.Deploy.Api.Logic.Settings.Entities;
using NET.Deploy.Api.Logic.EnvConfigs;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NET.Deploy.Api.Logic.Deploy;

public class DeployLogic(
    ILogger<DeployLogic> logger,
    GitLogic gitLogic,
    BuildManager buildManager,
    TransferManager transferManager,
    ProcessRunner processRunner,
    EnvConfigsLogic envConfigsLogic)
{
    public async Task<bool> DeployServiceAsync(
        ServiceDefinitionDB service,
        AppSettingsDB settings,
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
        ServiceDefinitionDB service,
        AppSettingsDB settings,
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
        ServiceDefinitionDB service,
        AppSettingsDB settings,
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
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, serviceId, service.DeployTargetPath);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, serviceId);
            
            await log("ERROR", "❌ Build/publish failed.", serviceId);
            return false;
        }
        
        await log("SUCCESS", "✅ Build successful.", serviceId);

        return await ExecuteTransferPhaseAsync(service, publishOutput, vpsOverride, log, isWindowsService);
    }

    private async Task<bool> ExecuteTransferPhaseAsync(ServiceDefinitionDB service, string publishOutput, VpsSettings? vpsOverride, LogCallback log, bool isWindowsService)
    {
        var targetPath = service.DeployTargetPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            await log("ERROR", "❌ Missing DeployTargetPath for service.", service.Id);
            return false;
        }

        await ApplyEnvironmentConfigsAsync(service, publishOutput, log, service.Id);

        try
        {
            var transferred = await transferManager.TransferAsync(publishOutput, targetPath, vpsOverride, log, service.Id);
            if (!transferred) throw new Exception("Transfer manager reported failure.");
        }
        catch (Exception ex)
        {
            await log("WARNING", $"🔄 Transfer failed. Attempting to restart service/site to restore availability...", service.Id);
            if (isWindowsService) 
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id, service.DeployTargetPath);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id);
            
            await log("ERROR", $"❌ Transfer failed after all attempts: {ex.Message}", service.Id);
            logger.LogError(ex, "Failed to copy/upload files to {Target}", targetPath);
            return false;
        }

        if (isWindowsService)
            await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id, service.DeployTargetPath);
        else if (service.ServiceType is "WebApi" or "Mvc")
            await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id);

        await log("SUCCESS", $"🚀 Deploy complete for: {service.Name}", service.Id);
        
        if (!string.IsNullOrWhiteSpace(service.HeartbeatUrl))
        {
            await CheckHeartbeatAsync(service.HeartbeatUrl, log, service.Id);
        }

        return true;
    }

    private async Task ManageWindowsServiceAsync(string? iisSiteName, string action, LogCallback log, string? serviceId, string? targetPath = null) /* action => start / stop */
    {
        var actionIcon = action == "start" ? "🏁 Starting" : "🛑 Stopping";
        await log("INFO", $"{actionIcon} Windows Service: {iisSiteName}...", serviceId);

        // Try the action
        var success = await processRunner.RunAsync("sc.exe", $"{action} {iisSiteName}", ".", log, serviceId);
        
        if (action == "start" && !success && !string.IsNullOrWhiteSpace(targetPath))
        {
            // If failed to start, check if it's because it doesn't exist
            await log("INFO", $"🔍 Service '{iisSiteName}' might be missing. Checking...", serviceId);
            
            // Search for primary EXE
            if (Directory.Exists(targetPath))
            {
                var files = Directory.GetFiles(targetPath, "*.exe");
                var exePath = files.FirstOrDefault(f => !f.EndsWith("apphost.exe", StringComparison.OrdinalIgnoreCase));
                
                if (exePath != null)
                {
                    await log("INFO", $"🏗️ Creating Windows Service '{iisSiteName}' (Auto-start)...", serviceId);
                    // CRITICAL: sc.exe requires a space AFTER the equals sign (e.g. binPath= "...")!
                    var created = await processRunner.RunAsync("sc.exe", "create \"" + iisSiteName + "\" binPath= \"" + exePath + "\" start= auto", ".", log, serviceId);
                    if (created)
                    {
                        await log("SUCCESS", $"✅ Service '{iisSiteName}' created. Starting now...", serviceId);
                        await processRunner.RunAsync("sc.exe", $"start {iisSiteName}", ".", log, serviceId);
                    }
                }
                else
                {
                    await log("WARNING", "⚠️ Could not find executable to create windows service.", serviceId);
                }
            }
        }

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

    private static string GetEffectiveBranch(ServiceDefinitionDB service, string parsedBranch, string? branchOverride)
    {
        return !string.IsNullOrWhiteSpace(branchOverride) ? branchOverride 
             : (!string.IsNullOrWhiteSpace(service.Branch) && service.Branch != "main" ? service.Branch : parsedBranch);
    }

    private static string GetEffectiveProjectPath(ServiceDefinitionDB service, string parsedPath)
    {
        return string.IsNullOrWhiteSpace(service.ProjectPath) ? parsedPath : service.ProjectPath;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private async Task ApplyEnvironmentConfigsAsync(ServiceDefinitionDB service, string publishOutput, LogCallback log, string? serviceId)
    {
        if (service.EnvConfigSetIds == null || service.EnvConfigSetIds.Count == 0) return;

        await log("INFO", $"⚙️ Applying Environment Configurations ({service.EnvConfigSetIds.Count} sets)...", serviceId);

        var configSets = await envConfigsLogic.GetByIdsAsync(service.EnvConfigSetIds);

        foreach (var configSet in configSets)
        {
            // 1. Handle File Renames (Source -> Target Overwrite)
            if (configSet.FileRenames != null && configSet.FileRenames.Count > 0)
            {
                foreach (var rename in configSet.FileRenames)
                {
                    var sourcePath = Path.Combine(publishOutput, rename.SourceFileName);
                    var targetPath = Path.Combine(publishOutput, rename.TargetFileName);
                    
                    if (File.Exists(sourcePath))
                    {
                        try
                        {
                            File.Copy(sourcePath, targetPath, overwrite: true);
                            await log("INFO", $"🔄 File Rename/Copy: '{rename.SourceFileName}' -> '{rename.TargetFileName}' (Set: {configSet.Name})", serviceId);
                        }
                        catch (Exception ex)
                        {
                            await log("WARNING", $"⚠️ Could not copy file {rename.SourceFileName}: {ex.Message}", serviceId);
                        }
                    }
                    else
                    {
                        await log("WARNING", $"⚠️ Source file '{rename.SourceFileName}' not found. Cannot perform rename/copy.", serviceId);
                    }
                }
            }

            // 2. Handle JSON Key Overrides
            var targetFile = Path.Combine(publishOutput, configSet.TargetFileName);
            if (!File.Exists(targetFile))
            {
                // If it's just meant for file renames, it's fine. 
                // But if it has variables, we should warning.
                if (configSet.Variables != null && configSet.Variables.Count > 0)
                    await log("WARNING", $"⚠️ Target config file '{configSet.TargetFileName}' not found in publish output. Skipping set '{configSet.Name}'.", serviceId);
                
                continue;
            }

            try
            {
                var jsonString = await File.ReadAllTextAsync(targetFile);
                var jsonNode = JsonNode.Parse(jsonString);

                if (jsonNode != null)
                {
                    int applied = 0;
                    foreach (var variable in configSet.Variables)
                    {
                        var keys = variable.Key.Split(new[] { ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
                        JsonNode? current = jsonNode;
                        
                        // traverse down to the parent of the target key
                        for (int i = 0; i < keys.Length - 1; i++)
                        {
                            var keyNode = current?[keys[i]];
                            if (keyNode == null)
                            {
                                if (current is JsonObject parentObj)
                                {
                                    parentObj[keys[i]] = new JsonObject();
                                    current = parentObj[keys[i]];
                                }
                                else
                                {
                                    break; // Type mismatch, not an object
                                }
                            }
                            else
                            {
                                current = keyNode;
                            }
                        }

                        // update the value
                        if (current != null && current is JsonObject currentObj)
                        {
                            currentObj[keys.Last()] = JsonValue.Create(variable.Value);
                            applied++;
                        }
                    }

                    var updatedJson = jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(targetFile, updatedJson);
                    await log("SUCCESS", $"✅ Successfully applied {applied} variables to {configSet.TargetFileName} (Set: {configSet.Name}).", serviceId);
                }
            }
            catch (Exception ex)
            {
                await log("ERROR", $"❌ Failed to apply config set '{configSet.Name}' to '{configSet.TargetFileName}': {ex.Message}", serviceId);
            }
        }
    }
}
