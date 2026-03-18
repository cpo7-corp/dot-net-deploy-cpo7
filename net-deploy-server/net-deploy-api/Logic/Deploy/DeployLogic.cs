using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Services.Entities;
using NET.Deploy.Api.Logic.Settings.Entities;
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
    public async Task<bool> PrepAndBuildServiceAsync(
        ServiceDefinitionDB service,
        AppSettingsDB settings,
        LogCallback log,
        string environmentId,
        string? branchOverride = null,
        bool forceClean = false,
        bool skipPull = false,
        bool skipBuildIfOutputExists = false)
    {
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        if (envConfig == null)
        {
            await log("ERROR", $"❌ [Prep] Environment configuration not found for EnvironmentId: {environmentId}", service.Id);
            return false;
        }

        var publishOutput = Path.Combine(Path.GetTempPath(), "net-deploy", service.Id ?? service.Name);
        if (skipBuildIfOutputExists && Directory.Exists(publishOutput) && Directory.GetFileSystemEntries(publishOutput).Any())
        {
            await log("INFO", "⏭️ [Prep] Build output already exists and skip requested. Applying configs...", service.Id);
            var vps = settings.VpsEnvironments.FirstOrDefault(e => e.Id == environmentId);
            await ApplyEnvironmentConfigsAsync(envConfig, vps, publishOutput, log, service.Id);
            return true;
        }

        var (repoUrl, gitBranch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
        var effectiveBranch = GetEffectiveBranch(service, envConfig, gitBranch, branchOverride);
        var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));

        if (!skipPull)
        {
            await log("INFO", $"📥 [Prep] Pulling latest for {service.Name} ({effectiveBranch})...", service.Id);
            if (!await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean))
                return false;
        }

        var repoLocalPath = gitLogic.GetRepoLocalPath(settings.Git, repoUrl, effectiveProjectPath);
        var projectFullPath = Path.Combine(repoLocalPath, effectiveProjectPath);

        if (Directory.Exists(publishOutput))
        {
            try { FileHelper.DeleteDirectoryRecursively(publishOutput); } catch { }
        }

        await log("INFO", $"🔨 [Prep] Building & publishing {service.Name}...", service.Id);
        bool buildSuccess = await buildManager.BuildAsync(projectFullPath, publishOutput, service.ServiceType, service.CompileSingleFile, log, service.Id);

        if (buildSuccess)
        {
            var vps = settings.VpsEnvironments.FirstOrDefault(e => e.Id == environmentId);
            await ApplyEnvironmentConfigsAsync(envConfig, vps, publishOutput, log, service.Id);
            await log("SUCCESS", $"✅ [Prep] Prepared and configured: {service.Name}", service.Id);
        }

        return buildSuccess;
    }

    public async Task<bool> PerformServiceActionAsync(ServiceDefinitionDB service, string environmentId, string action, AppSettingsDB settings, LogCallback log)
    {
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        if (envConfig == null) { await log("ERROR", "❌ [Action] Missing environment config.", service.Id); return false; }

        var vps = settings.VpsEnvironments.FirstOrDefault(e => e.Id == environmentId);
        bool isWin = service.ServiceType == "WindowsService";

        if (action == "restart")
        {
            if (isWin)
            {
                await ManageWindowsServiceAsync(service.IisSiteName, "stop", log, service.Id);
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id, envConfig.DeployTargetPath);
            }
            else
            {
                // For IIS, a recycle is often more robust than stop/start
                await ManageIisSiteAsync(service.IisSiteName, "recycle", log, service.Id);
                await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id); // Ensure site is also started
            }
        }
        else
        {
            if (isWin) await ManageWindowsServiceAsync(service.IisSiteName, action, log, service.Id, envConfig.DeployTargetPath);
            else await ManageIisSiteAsync(service.IisSiteName, action, log, service.Id);
        }

        return true;
    }


    public async Task<bool> DeployServiceAsync(
        ServiceDefinitionDB service,
        AppSettingsDB settings,
        LogCallback log,
        string environmentId,
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
                var success = await DeployServiceInternalAsync(service, settings, log, environmentId, branchOverride, vpsOverride, effectiveClean, skipPull, skipBuildIfOutputExists || attempt > 1);
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
        string environmentId,
        string? branchOverride = null,
        bool forceClean = false)
    {
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        var (repoUrl, gitBranch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
        var effectiveBranch = GetEffectiveBranch(service, envConfig, gitBranch, branchOverride);
        var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));

        await log("INFO", $"📥 [Prep] Pulling Git for {service.Name} ({effectiveBranch})...", service.Id);
        return await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean);
    }

    private async Task<bool> DeployServiceInternalAsync(
        ServiceDefinitionDB service,
        AppSettingsDB settings,
        LogCallback log,
        string environmentId,
        string? branchOverride,
        VpsSettings? vpsOverride,
        bool forceClean,
        bool skipPull,
        bool skipBuildIfOutputExists)
    {
        var serviceId = service.Id;
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        if (envConfig == null) return false;

        var publishOutput = Path.Combine(Path.GetTempPath(), "net-deploy", service.Id ?? service.Name);
        var isWindowsService = service.ServiceType == "WindowsService";

        // PHASE 1: PREPARATION (Pull, Build, Config)
        bool prepSuccess = await PrepAndBuildServiceAsync(service, settings, log, environmentId, branchOverride, forceClean, skipPull, skipBuildIfOutputExists);
        if (!prepSuccess) return false;

        // PHASE 2: STOP (Site/Service)
        if (isWindowsService)
            await ManageWindowsServiceAsync(service.IisSiteName, "stop", log, serviceId);
        else if (service.ServiceType is "WebApi" or "Mvc")
            await ManageIisSiteAsync(service.IisSiteName, "stop", log, serviceId);

        // PHASE 3: TRANSFER & START
        var success = await ExecuteTransferPhaseAsync(service, envConfig, vpsOverride, log, isWindowsService, publishOutput);

        if (!success)
        {
            // Restore service if transfer failed
            if (isWindowsService)
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, serviceId, envConfig.DeployTargetPath);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, serviceId);
        }

        return success;
    }

    private async Task<bool> ExecuteTransferPhaseAsync(ServiceDefinitionDB service, ServiceEnvironmentConfigDB envConfig, VpsSettings? vpsOverride, LogCallback log, bool isWindowsService, string publishOutput)
    {
        var targetPath = envConfig.DeployTargetPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            await log("ERROR", "❌ Missing DeployTargetPath for service environment.", service.Id);
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
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id, targetPath);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id);

            await log("ERROR", $"❌ Transfer failed after all attempts: {ex.Message}", service.Id);
            logger.LogError(ex, "Failed to copy/upload files to {Target}", targetPath);
            return false;
        }

        if (isWindowsService)
            await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id, targetPath);
        else if (service.ServiceType is "WebApi" or "Mvc")
            await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id);

        await log("SUCCESS", $"🚀 Deploy complete for: {service.Name}", service.Id);

        if (!string.IsNullOrWhiteSpace(envConfig.HeartbeatUrl))
        {
            await CheckHeartbeatAsync(envConfig.HeartbeatUrl, log, service.Id);
        }

        return true;
    }

    private async Task ApplyEnvironmentConfigsAsync(ServiceEnvironmentConfigDB envConfig, VpsSettings? vps, string publishOutput, LogCallback log, string? serviceId)
    {
        // 1. Auto-rename based on Environment Tag (e.g. *.prod.json -> *.json)
        var envTag = vps?.EnvironmentTag;
        if (!string.IsNullOrWhiteSpace(envTag))
        {
            await log("INFO", $"🔍 Auto-renaming files for environment tag: '{envTag}'...", serviceId);
            var pattern = $"*.{envTag}.json";
            var filesToRename = Directory.GetFiles(publishOutput, pattern, SearchOption.AllDirectories);

            foreach (var sourceFile in filesToRename)
            {
                var fileName = Path.GetFileName(sourceFile);
                var targetFileName = fileName.Replace($".{envTag}.json", ".json", StringComparison.OrdinalIgnoreCase);
                var targetFile = Path.Combine(Path.GetDirectoryName(sourceFile)!, targetFileName);

                try
                {
                    File.Copy(sourceFile, targetFile, overwrite: true);
                    await log("INFO", $"✨ Auto-rename: '{fileName}' -> '{targetFileName}'", serviceId);
                }
                catch (Exception ex)
                {
                    await log("WARNING", $"⚠️ Auto-rename failed for {fileName}: {ex.Message}", serviceId);
                }
            }
        }

        // 2. Shared File Overwrites (from VPS Settings - Global for environment)
        if (vps?.SharedFileRenames != null && vps.SharedFileRenames.Count > 0)
        {
            foreach (var rename in vps.SharedFileRenames)
            {
                var sourcePath = Path.Combine(publishOutput, rename.SourceFileName);
                var targetPath = Path.Combine(publishOutput, rename.TargetFileName);

                if (File.Exists(sourcePath))
                {
                    try
                    {
                        File.Copy(sourcePath, targetPath, overwrite: true);
                        await log("INFO", $"🔄 Shared Overwrite: '{rename.SourceFileName}' -> '{rename.TargetFileName}'", serviceId);
                    }
                    catch (Exception ex)
                    {
                        await log("WARNING", $"⚠️ Shared overwrite failed for {rename.SourceFileName}: {ex.Message}", serviceId);
                    }
                }
            }
        }

        // 3. Config Sets (Variable Sets) assigned to the service
        if (envConfig.ConfigSetIds != null && envConfig.ConfigSetIds.Count > 0)
        {
            foreach (var configId in envConfig.ConfigSetIds)
            {
                var configSet = await envConfigsLogic.GetByIdAsync(configId);
                if (configSet == null)
                {
                    await log("WARNING", $"⚠️ Config Set with ID {configId} not found.", serviceId);
                    continue;
                }

                await log("INFO", $"📦 Applying Config Set: {configSet.Name}", serviceId);

                // 1. Determine target file for variables and handle optional rename
                string targetFileForVariables = "appsettings.json";

                if (!string.IsNullOrWhiteSpace(configSet.SourceFileName))
                {
                    var sourcePath = Path.Combine(publishOutput, configSet.SourceFileName);
                    var targetName = string.IsNullOrWhiteSpace(configSet.TargetFileName) ? configSet.SourceFileName : configSet.TargetFileName;
                    var targetPath = Path.Combine(publishOutput, targetName);

                    targetFileForVariables = targetName;

                    // Only copy/rename if target is different from source
                    if (!string.Equals(configSet.SourceFileName, targetName, StringComparison.OrdinalIgnoreCase) && File.Exists(sourcePath))
                    {
                        try
                        {
                            if (File.Exists(targetPath)) File.Delete(targetPath);
                            File.Copy(sourcePath, targetPath, overwrite: true);
                            await log("INFO", $"✨ [Variables] File Rename: '{configSet.SourceFileName}' -> '{targetName}'", serviceId);
                        }
                        catch (Exception ex) { await log("WARNING", $"⚠️ Failed to rename {configSet.SourceFileName}: {ex.Message}", serviceId); }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(configSet.TargetFileName))
                {
                    targetFileForVariables = configSet.TargetFileName;
                }

                // 2. Apply Variables from Config Set to the target file
                if (configSet.Variables != null && configSet.Variables.Count > 0)
                {
                    var targetPath = Path.Combine(publishOutput, targetFileForVariables);
                    if (File.Exists(targetPath))
                    {
                        await ApplyJsonVariablesAsync(targetPath, configSet.Variables, log, serviceId, configSet.Name);
                    }
                    else
                    {
                        await log("WARNING", $"⚠️ [ConfigSet] Target file '{targetFileForVariables}' not found for variables.", serviceId);
                    }
                }
            }
        }

        // 4. Shared JSON Variable Overrides (from VPS Settings - Global)
        if (vps?.SharedVariables != null && vps.SharedVariables.Count > 0)
        {
            var targetFile = Path.Combine(publishOutput, "appsettings.json");
            if (File.Exists(targetFile))
            {
                await ApplyJsonVariablesAsync(targetFile, vps.SharedVariables, log, serviceId, "Shared-VPS");
            }
        }
    }

    private async Task ApplyJsonVariablesAsync(string targetFile, List<EnvVariableDB> variables, LogCallback log, string? serviceId, string type)
    {
        try
        {
            var jsonString = await File.ReadAllTextAsync(targetFile);

            var parseOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var jsonNode = JsonNode.Parse(jsonString, null, parseOptions);

            if (jsonNode != null)
            {
                int applied = 0;
                foreach (var variable in variables)
                {
                    if (string.IsNullOrWhiteSpace(variable.Key)) continue;

                    var keys = variable.Key.Split(new[] { ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
                    JsonNode? current = jsonNode;

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
                            else break;
                        }
                        else current = keyNode;
                    }

                    if (current != null && current is JsonObject currentObj)
                    {
                        var lastKey = keys.Last();
                        var val = variable.Value;

                        // Explicit String? (If user entered "1" with quotes in the UI)
                        if (val.Length >= 2 && val.StartsWith("\"") && val.EndsWith("\""))
                        {
                            var stringVal = val.Substring(1, val.Length - 2);
                            currentObj[lastKey] = JsonValue.Create(stringVal);
                        }
                        // Smart Value Parsing
                        else if (long.TryParse(val, out var l))
                            currentObj[lastKey] = JsonValue.Create(l);
                        else if (double.TryParse(val, out var d))
                            currentObj[lastKey] = JsonValue.Create(d);
                        else if (bool.TryParse(val, out var b))
                            currentObj[lastKey] = JsonValue.Create(b);
                        else
                        {
                            // Treat as literal string
                            currentObj[lastKey] = JsonValue.Create(val);
                        }

                        applied++;
                    }
                }

                if (applied > 0)
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var updatedJson = jsonNode.ToJsonString(options);
                    await File.WriteAllTextAsync(targetFile, updatedJson);
                    if (log != null) await log("INFO", $"✨ [{type}] Applied {applied} variables to {Path.GetFileName(targetFile)}", serviceId);
                }
            }
        }
        catch (Exception ex)
        {
            if (log != null) await log("ERROR", $"❌ Failed to apply {type} variables to {Path.GetFileName(targetFile)}: {ex.Message}", serviceId);
        }
    }

    private async Task ManageWindowsServiceAsync(string? iisSiteName, string action, LogCallback log, string? serviceId, string? targetPath = null)
    {
        var actionIcon = action == "start" ? "🏁 Starting" : "🛑 Stopping";
        await log("INFO", $"{actionIcon} Windows Service: {iisSiteName}...", serviceId);

        if (action == "start")
        {
            // Ensure service is set to Automatic and is enabled
            await processRunner.RunAsync("sc.exe", $"config \"{iisSiteName}\" start= auto", ".", log, serviceId);
        }

        var success = await processRunner.RunAsync("sc.exe", $"{action} \"{iisSiteName}\"", ".", log, serviceId);

        if (action == "start" && !success && !string.IsNullOrWhiteSpace(targetPath))
        {
            await log("INFO", $"🔍 Checking if service '{iisSiteName}' needs to be created...", serviceId);
            if (Directory.Exists(targetPath))
            {
                var files = Directory.GetFiles(targetPath, "*.exe");
                var exePath = files.FirstOrDefault(f => !f.EndsWith("apphost.exe", StringComparison.OrdinalIgnoreCase));

                if (exePath != null)
                {
                    await log("INFO", $"🏗️ Creating Windows Service '{iisSiteName}'...", serviceId);
                    var created = await processRunner.RunAsync("sc.exe", "create \"" + iisSiteName + "\" binPath= \"" + exePath + "\" start= auto", ".", log, serviceId);
                    if (created)
                    {
                        await log("SUCCESS", $"✅ Service created. Starting...", serviceId);
                        await processRunner.RunAsync("sc.exe", $"start \"{iisSiteName}\"", ".", log, serviceId);
                    }
                }
            }
        }
        if (action == "stop") await Task.Delay(2000);
    }

    private async Task ManageIisSiteAsync(string? iisSiteName, string action, LogCallback log, string? serviceId)
    {
        var actionIcon = action == "start" ? "🏁 Starting" : (action == "recycle" ? "♻️ Recycling" : "🛑 Stopping");
        await log("INFO", $"{actionIcon} IIS Site & AppPool: {iisSiteName}...", serviceId);

        var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"inetsrv\appcmd.exe");
        if (!File.Exists(appcmd)) appcmd = "appcmd.exe";

        if (action == "recycle")
        {
            await processRunner.RunAsync(appcmd, $"recycle apppool \"{iisSiteName}\"", ".", log, serviceId);
        }
        else
        {
            await processRunner.RunAsync(appcmd, $"{action} site \"{iisSiteName}\"", ".", log, serviceId);
            await processRunner.RunAsync(appcmd, $"{action} apppool \"{iisSiteName}\"", ".", log, serviceId);
        }

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
                await log("SUCCESS", $"✅ Heartbeat OK! Status: {response.StatusCode}", serviceId);
            else
                await log("WARNING", $"⚠️ Heartbeat returned error: {response.StatusCode}", serviceId);
        }
        catch (Exception ex)
        {
            await log("ERROR", $"❌ Heartbeat failed: {ex.Message}", serviceId);
        }
    }

    private static string GetEffectiveBranch(ServiceDefinitionDB service, ServiceEnvironmentConfigDB envConfig, string parsedBranch, string? branchOverride)
    {
        if (!string.IsNullOrWhiteSpace(branchOverride)) return branchOverride;
        if (!string.IsNullOrWhiteSpace(envConfig.DefaultBranch)) return envConfig.DefaultBranch;
        return parsedBranch;
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
}
