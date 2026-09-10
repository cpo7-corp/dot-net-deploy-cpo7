using NET.Deploy.Api.Data.Entities;
using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

using NET.Deploy.Api.Logic.Docker;

namespace NET.Deploy.Api.Logic.Deploy;

public class DeployLogic(
    ILogger<DeployLogic> logger,
    GitLogic gitLogic,
    BuildManager buildManager,
    TransferManager transferManager,
    ProcessRunner processRunner,
    EnvConfigsLogic envConfigsLogic,
    NET.Deploy.Api.Logic.DeployHistory.DeployHistoryLogic deployHistoryLogic,
    ServicesLogic servicesLogic,
    DockerLogic dockerLogic)
{
    public (string RepoUrl, string Branch, string ProjectPath) ParseGitUrl(string fullUrl) => gitLogic.ParseGitUrl(fullUrl);

    public async Task<bool> PrepAndBuildServiceAsync(

        ServiceDefinitionDB service,
        AppSettingsDB settings,
        LogCallback log,
        string environmentId,
        string? branchOverride = null,
        bool forceClean = false,
        bool skipPull = false,
        bool skipBuildIfOutputExists = false,
        System.Threading.CancellationToken ct = default)
    {
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        if (envConfig == null)
        {
            await log("ERROR", $"❌ [Prep] Environment configuration not found for EnvironmentId: {environmentId}", service.Id);
            return false;
        }

        var vps = settings.VpsEnvironments.FirstOrDefault(e => e.Id == environmentId);
        var isDockerDeployment = IsDockerDeployment(service, vps);

        var publishOutput = Path.Combine(Path.GetTempPath(), "net-deploy", service.Id ?? service.Name);
        if (!isDockerDeployment && skipBuildIfOutputExists && Directory.Exists(publishOutput) && Directory.GetFileSystemEntries(publishOutput).Any())
        {
            await log("INFO", "⏭️ [Prep] Build output already exists and skip requested. Applying configs...", service.Id);
            await ApplyEnvironmentConfigsAsync(envConfig, vps, publishOutput, log, service.Id);
            return true;
        }

        var (repoUrl, gitBranch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
        var effectiveBranch = GetEffectiveBranch(service, envConfig, gitBranch, branchOverride);
        var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));

        var repoLocalPath = gitLogic.GetRepoLocalPath(settings.Git, repoUrl, effectiveProjectPath);
        var projectFullPath = Path.Combine(repoLocalPath, effectiveProjectPath);

        // LOCK START: Ensure only one service works on this repository folder at a time
        var @lock = RepositoryLockManager.Get(repoLocalPath);
        await @lock.WaitAsync(ct);

        try
        {
            if (!skipPull)
            {
                await log("INFO", $"📥 [Prep] Pulling latest for {service.Name} ({effectiveBranch})...", service.Id);
                if (!await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean, ct))
                    return false;
            }

            if (isDockerDeployment)
            {
                await log("INFO", $"🐳 [Docker] Repository ready at: {repoLocalPath}", service.Id);
                return true;
            }

            if (!File.Exists(projectFullPath) && !Directory.Exists(projectFullPath))
            {
                await log("ERROR", $"❌ [Prep] Project path not found: {projectFullPath}", service.Id);
                return false;
            }

            if (Directory.Exists(publishOutput))
            {
                try { FileHelper.DeleteDirectoryRecursively(publishOutput); } catch { }
            }

            await log("INFO", $"🔨 [Prep] Building & publishing {service.Name}...", service.Id);
            bool buildSuccess = await buildManager.BuildAsync(projectFullPath, publishOutput, service.ServiceType, service.CompileSingleFile, log, service.Id, ct);

            if (buildSuccess)
            {
                await ApplyEnvironmentConfigsAsync(envConfig, vps, publishOutput, log, service.Id);
                await log("SUCCESS", $"✅ [Prep] Prepared and configured: {service.Name}", service.Id);
            }

            return buildSuccess;
        }
        finally
        {
            @lock.Release();
        }
    }


    public async Task<bool> PerformServiceActionAsync(ServiceDefinitionDB service, string environmentId, string action, AppSettingsDB settings, LogCallback log)
    {
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        if (envConfig == null) { await log("ERROR", "❌ [Action] Missing environment config.", service.Id); return false; }

        var vps = settings.VpsEnvironments.FirstOrDefault(e => e.Id == environmentId);

        if (IsDockerDeployment(service, vps))
        {
            if (vps != null && !vps.IsLocal && vps.ServerType is not ("LinuxDocker" or "WindowsDocker"))
            {
                await log("ERROR", "❌ [Docker] The selected environment is not configured as a Docker host.", service.Id);
                return false;
            }
            var composeFile = GetDockerComposeFile(service);
            var targetPath = GetDockerTargetPath(service, envConfig, vps);
            var environmentComposeFile = GetDockerEnvironmentComposeFile(envConfig);
            var composeServiceName = service.DockerComposeServiceName;
            if (string.IsNullOrWhiteSpace(composeServiceName))
            {
                await log("ERROR", "❌ [Docker] Compose Service Name is required for a single-service action.", service.Id);
                return false;
            }
            return await dockerLogic.RunComposeAsync(
                targetPath,
                composeFile,
                environmentComposeFile,
                GetDockerOverrideFile(composeServiceName),
                composeServiceName,
                service.DockerComposeProjectName,
                action,
                vps,
                log,
                service.Id);
        }

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
                await ManageIisSiteAsync(service.IisSiteName, "recycle", log, service.Id, vps);
                await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id, vps); // Ensure site is also started
            }
        }
        else
        {
            if (isWin) await ManageWindowsServiceAsync(service.IisSiteName, action, log, service.Id, envConfig.DeployTargetPath);
            else await ManageIisSiteAsync(service.IisSiteName, action, log, service.Id, vps);
        }

        return true;
    }


    public async Task<(bool Success, bool? Heartbeat, double TransferSeconds, double HeartbeatSeconds)> DeployServiceAsync(
        ServiceDefinitionDB service,
        AppSettingsDB settings,
        LogCallback log,
        string environmentId,
        string? branchOverride = null,
        VpsSettings? vpsOverride = null,
        bool forceClean = false,
        bool skipPull = false,
        bool skipBuildIfOutputExists = false,
        bool skipHeartbeat = false,
        System.Threading.CancellationToken ct = default)
    {
        var serviceId = service.Id;
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (attempt > 1)
            {
                await log("WARNING", $"🔄 Retry attempt {attempt}/{maxRetries} for {service.Name}...", serviceId);
                await Task.Delay(5000, ct);
            }

            try
            {
                var effectiveClean = attempt == 1 && forceClean;
                var result = await DeployServiceInternalAsync(service, settings, log, environmentId, branchOverride, vpsOverride, effectiveClean, skipPull, skipBuildIfOutputExists || attempt > 1, skipHeartbeat, ct);
                if (result.Success) return result;
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

        return (false, null, 0, 0);
    }

    public async Task<bool> PrepGitOnlyAsync(
        ServiceDefinitionDB service,
        AppSettingsDB settings,
        LogCallback log,
        string environmentId,
        string? branchOverride = null,
        bool forceClean = false,
        System.Threading.CancellationToken ct = default)
    {
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        var (repoUrl, gitBranch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
        var effectiveBranch = GetEffectiveBranch(service, envConfig, gitBranch, branchOverride);
        var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));

        await log("INFO", $"📥 [Prep] Pulling Git for {service.Name} ({effectiveBranch})...", service.Id);
        return await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean, ct);
    }

    private async Task<(bool Success, bool? Heartbeat, double TransferSeconds, double HeartbeatSeconds)> DeployServiceInternalAsync(
        ServiceDefinitionDB service,
        AppSettingsDB settings,
        LogCallback log,
        string environmentId,
        string? branchOverride,
        VpsSettings? vpsOverride,
        bool forceClean,
        bool skipPull,
        bool skipBuildIfOutputExists,
        bool skipHeartbeat,
        System.Threading.CancellationToken ct)
    {
        var serviceId = service.Id;
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        if (envConfig == null) return (false, null, 0, 0);

        var publishOutput = Path.Combine(Path.GetTempPath(), "net-deploy", service.Id ?? service.Name);
        var isWindowsService = service.ServiceType == "WindowsService";
        var isIis = service.ServiceType is "WebApi" or "Mvc";
        var effectiveVps = vpsOverride ?? settings.VpsEnvironments.FirstOrDefault(e => e.Id == environmentId);

        if (IsDockerDeployment(service, effectiveVps))
        {
            return await DeployDockerServiceAsync(service, envConfig, effectiveVps, settings, log, branchOverride, forceClean, skipPull, skipHeartbeat, ct);
        }

        var targetPath = isIis && !string.IsNullOrWhiteSpace(envConfig.DeployTargetPath)
            ? IisDeployment.TargetPath(envConfig.DeployTargetPath, service.IisSiteName)
            : envConfig.DeployTargetPath;

        // PHASE 1: PREPARATION (Pull, Build, Config)
        bool prepSuccess = await PrepAndBuildServiceAsync(service, settings, log, environmentId, branchOverride, forceClean, skipPull, skipBuildIfOutputExists, ct);
        if (!prepSuccess) return (false, null, 0, 0);

        // NEW: Get current commit info
        ProjectVersion? currentVersion = null;
        try
        {
            var (repoUrl, gitBranch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
            var effectiveBranch = GetEffectiveBranch(service, envConfig, gitBranch, branchOverride);
            var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));
            var repoLocalPath = gitLogic.GetRepoLocalPath(settings.Git, repoUrl, effectiveProjectPath);
            currentVersion = await gitLogic.GetCurrentCommitAsync(repoLocalPath, effectiveBranch);
        }
        catch { }

        // PHASE 2: STOP (Site/Service)
        if (isIis)
            targetPath = (await IisDeployment.RunAsync(service.IisSiteName, "ensure", targetPath, effectiveVps, processRunner, log, serviceId, envConfig.IisPort))!;

        if (isWindowsService)
            await ManageWindowsServiceAsync(service.IisSiteName, "stop", log, serviceId);
        else if (service.ServiceType is "WebApi" or "Mvc")
            await ManageIisSiteAsync(service.IisSiteName, "stop", log, serviceId, effectiveVps);

        // EXTRA: Force kill any remaining processes holding files in target directory
        if (effectiveVps == null || effectiveVps.IsLocal || string.IsNullOrWhiteSpace(effectiveVps.Host) || effectiveVps.Host is "localhost" or "127.0.0.1")
            await processRunner.KillProcessesInDirectory(targetPath, log, serviceId);

        // PHASE 3: TRANSFER & START
        var result = await ExecuteTransferPhaseAsync(service, envConfig, effectiveVps, log, isWindowsService, publishOutput, targetPath, currentVersion, skipHeartbeat, ct);

        if (!result.Success)
        {
            // Restore service if transfer failed
            if (isWindowsService)
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, serviceId, envConfig.DeployTargetPath);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, serviceId, effectiveVps);
        }

        return result;
    }

    private async Task<(bool Success, bool? Heartbeat, double TransferSeconds, double HeartbeatSeconds)> DeployDockerServiceAsync(
        ServiceDefinitionDB service,
        ServiceEnvironmentConfig envConfig,
        VpsSettings? vps,
        AppSettingsDB settings,
        LogCallback log,
        string? branchOverride,
        bool forceClean,
        bool skipPull,
        bool skipHeartbeat,
        System.Threading.CancellationToken ct)
    {
        var serviceId = service.Id;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (vps != null && !vps.IsLocal && vps.ServerType is not ("LinuxDocker" or "WindowsDocker"))
        {
            await log("ERROR", "❌ [Docker] The selected environment is not configured as a Docker host.", serviceId);
            return (false, null, 0, 0);
        }

        var (repoUrl, gitBranch, projectPath) = gitLogic.ParseGitUrl(service.RepoUrl);
        var effectiveBranch = GetEffectiveBranch(service, envConfig, gitBranch, branchOverride);
        var effectiveProjectPath = NormalizePath(GetEffectiveProjectPath(service, projectPath));
        var repoLocalPath = gitLogic.GetRepoLocalPath(settings.Git, repoUrl, effectiveProjectPath);

        var @lock = RepositoryLockManager.Get(repoLocalPath);
        await @lock.WaitAsync(ct);

        try
        {
            if (!skipPull)
            {
                await log("INFO", $"📥 [Docker] Pulling repository ({effectiveBranch})...", serviceId);
                if (!await gitLogic.PullAsync(settings.Git, repoUrl, effectiveBranch, log, effectiveProjectPath, forceClean, ct))
                    return (false, null, 0, 0);
            }

            var currentVersion = await gitLogic.GetCurrentCommitAsync(repoLocalPath, effectiveBranch);

            // 1. Prepare Environment Variables & AppSettings overrides into .env
            var allVariables = new List<EnvVariable>();
            if (envConfig.ConfigSetIds != null)
            {
                foreach (var cid in envConfig.ConfigSetIds)
                {
                    var cs = await envConfigsLogic.GetByIdAsync(cid);
                    if (cs?.Variables != null) allVariables.AddRange(cs.Variables);
                }
            }
            if (vps?.SharedVariables != null)
            {
                allVariables.AddRange(vps.SharedVariables);
            }

            // 2. Locate or generate Compose files
            var composeFile = GetDockerComposeFile(service);
            var composeFullPath = Path.Combine(repoLocalPath, composeFile);
            var usesExistingCompose = File.Exists(composeFullPath);
            var composeServiceName = service.DockerComposeServiceName;
            var environmentComposeFile = GetDockerEnvironmentComposeFile(envConfig);
            string? overrideFile = null;

            if (!string.IsNullOrWhiteSpace(environmentComposeFile) &&
                !File.Exists(Path.Combine(repoLocalPath, environmentComposeFile)))
            {
                await log("ERROR", $"❌ [Docker] Environment Compose file not found: {environmentComposeFile}", serviceId);
                return (false, null, sw.Elapsed.TotalSeconds, 0);
            }

            if (usesExistingCompose)
            {
                if (string.IsNullOrWhiteSpace(composeServiceName))
                {
                    await log("ERROR", "❌ [Docker] Compose Service Name is required when deploying from an existing Compose file.", serviceId);
                    return (false, null, sw.Elapsed.TotalSeconds, 0);
                }

                if (!IsValidComposeName(composeServiceName) || !IsValidComposeName(service.DockerComposeProjectName))
                {
                    await log("ERROR", "❌ [Docker] Compose service/project names may contain only letters, numbers, dots, underscores and hyphens.", serviceId);
                    return (false, null, sw.Elapsed.TotalSeconds, 0);
                }

                var envFile = GetDockerEnvFile(composeServiceName);
                overrideFile = GetDockerOverrideFile(composeServiceName);
                await File.WriteAllTextAsync(Path.Combine(repoLocalPath, envFile), dockerLogic.GenerateEnvFileContent(allVariables), ct);
                await File.WriteAllTextAsync(Path.Combine(repoLocalPath, overrideFile), dockerLogic.GenerateComposeOverride(composeServiceName, envFile), ct);
                await log("INFO", $"⚙️ [Docker] Configured {allVariables.Count} environment variables for Compose service '{composeServiceName}'.", serviceId);
            }
            else
            {
                if (service.ServiceType == "DockerCompose")
                {
                    await log("ERROR", $"❌ [Docker] Compose file not found: {composeFullPath}", serviceId);
                    return (false, null, sw.Elapsed.TotalSeconds, 0);
                }

                await File.WriteAllTextAsync(
                    Path.Combine(repoLocalPath, ".env"),
                    dockerLogic.GenerateEnvFileContent(allVariables),
                    ct);

                var dockerfile = !string.IsNullOrWhiteSpace(service.DockerfilePath) ? service.DockerfilePath : "Dockerfile";
                var dockerfileFullPath = ResolveDockerfilePath(repoLocalPath, effectiveProjectPath, dockerfile);
                if (dockerfileFullPath == null)
                {
                    await log("ERROR", $"❌ [Docker] Dockerfile not found in the repository root or project directory: {dockerfile}", serviceId);
                    return (false, null, sw.Elapsed.TotalSeconds, 0);
                }

                dockerfile = Path.GetRelativePath(repoLocalPath, dockerfileFullPath).Replace('\\', '/');

                var port = envConfig.DockerPort ?? 8080;
                await File.WriteAllTextAsync(
                    composeFullPath,
                    dockerLogic.GenerateComposeWithLoadBalancer(
                        service.Name,
                        GetDockerContainerName(service),
                        dockerfile,
                        port,
                        port,
                        envConfig.DockerReplicas,
                        envConfig.DockerParallelism,
                        envConfig.DockerDrainSeconds),
                    ct);
                await File.WriteAllTextAsync(
                    Path.Combine(repoLocalPath, "nginx.conf"),
                    dockerLogic.GenerateNginxConf(service.Name, port, port),
                    ct);
            }

            // 3. Transfer to the target server and deploy via Docker Compose
            await log("INFO", $"🐳 [Docker] Deploying with Docker Compose ({composeFile})...", serviceId);
            var targetPath = GetDockerTargetPath(service, envConfig, vps);
            var isRemote = vps != null && !vps.IsLocal && !string.IsNullOrWhiteSpace(vps.Host) && vps.Host != "localhost" && vps.Host != "127.0.0.1";
            if (isRemote)
            {
                if (!await dockerLogic.PrepareRemoteDirectoryAsync(targetPath, vps!, log, serviceId))
                {
                    return (false, null, sw.Elapsed.TotalSeconds, 0);
                }
                if (!await transferManager.TransferAsync(repoLocalPath, targetPath, vps, log, serviceId, ct))
                {
                    return (false, null, sw.Elapsed.TotalSeconds, 0);
                }
            }
            else
            {
                targetPath = repoLocalPath;
            }

            var composeOk = await dockerLogic.RunComposeAsync(
                targetPath,
                composeFile,
                environmentComposeFile,
                overrideFile,
                composeServiceName,
                service.DockerComposeProjectName,
                "deploy",
                vps,
                log,
                serviceId,
                ct);

            if (!composeOk)
            {
                await log("ERROR", "❌ [Docker] Docker Compose deployment failed.", serviceId);
                return (false, null, sw.Elapsed.TotalSeconds, 0);
            }

            await log("SUCCESS", "✅ [Docker] Containers deployed and running successfully!", serviceId);

            // 4. Record history & version
            if (currentVersion != null)
            {
                try
                {
                    await servicesLogic.UpdateVersionAsync(service.Id!, envConfig.EnvironmentId, currentVersion);

                    await deployHistoryLogic.AddAsync(new DeploymentHistoryDB
                    {
                        ServiceId = service.Id!,
                        EnvironmentId = envConfig.EnvironmentId,
                        Version = currentVersion,
                        ConfigSetIds = envConfig.ConfigSetIds ?? [],
                        CommitHash = currentVersion.CommitHash,
                        Created = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to record deployment history for {Service}", service.Name);
                }
            }

            // 5. Heartbeat
            bool? hbSuccess = null;
            var hbSw = System.Diagnostics.Stopwatch.StartNew();
            if (!skipHeartbeat && !string.IsNullOrWhiteSpace(envConfig.HeartbeatUrl))
            {
                hbSuccess = await CheckHeartbeatAsync(envConfig.HeartbeatUrl, log, serviceId);
            }

            return (true, hbSuccess, sw.Elapsed.TotalSeconds, hbSw.Elapsed.TotalSeconds);
        }
        finally
        {
            @lock.Release();
        }
    }

    private static string GetDockerComposeFile(ServiceDefinitionDB service) =>
        !string.IsNullOrWhiteSpace(service.DockerComposePath)
            ? service.DockerComposePath
            : "docker-compose.yml";

    private static string? GetDockerEnvironmentComposeFile(ServiceEnvironmentConfig envConfig) =>
        envConfig.DockerEnvironmentComposePath;

    private static string GetDockerEnvFile(string composeServiceName) =>
        $".net-deploy.{composeServiceName}.env";

    private static string GetDockerOverrideFile(string composeServiceName) =>
        $".net-deploy.{composeServiceName}.override.yml";

    private static bool IsValidComposeName(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsDockerDeployment(ServiceDefinitionDB service, VpsSettings? vps) =>
        service.ServiceType is "Docker" or "DockerCompose" ||
        vps?.ServerType is "LinuxDocker" or "WindowsDocker";

    private static string? ResolveDockerfilePath(string repoLocalPath, string effectiveProjectPath, string dockerfile)
    {
        var rootCandidate = Path.GetFullPath(Path.Combine(repoLocalPath, dockerfile));
        if (File.Exists(rootCandidate)) return rootCandidate;

        if (string.IsNullOrWhiteSpace(effectiveProjectPath)) return null;

        var projectPath = Path.Combine(repoLocalPath, effectiveProjectPath);
        var projectDirectory = Path.HasExtension(projectPath)
            ? Path.GetDirectoryName(projectPath)
            : projectPath;
        if (string.IsNullOrWhiteSpace(projectDirectory)) return null;

        var projectCandidate = Path.GetFullPath(Path.Combine(projectDirectory, dockerfile));
        return File.Exists(projectCandidate) ? projectCandidate : null;
    }

    private static string GetDockerContainerName(ServiceDefinitionDB service) =>
        !string.IsNullOrWhiteSpace(service.DockerContainerName)
            ? service.DockerContainerName
            : service.Name.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");

    private static string GetDockerTargetPath(ServiceDefinitionDB service, ServiceEnvironmentConfig envConfig, VpsSettings? vps)
    {
        if (!string.IsNullOrWhiteSpace(envConfig.DeployTargetPath)) return envConfig.DeployTargetPath;

        var serviceFolder = service.Name.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");
        var basePath = !string.IsNullOrWhiteSpace(vps?.DefaultDockerBasePath)
            ? vps.DefaultDockerBasePath
            : vps?.ServerType == "WindowsDocker" ? @"C:\net-deploy" : "/opt/net-deploy";

        return vps?.ServerType == "WindowsDocker"
            ? Path.Combine(basePath, serviceFolder)
            : $"{basePath.TrimEnd('/')}/{serviceFolder}";
    }

    private async Task<(bool Success, bool? Heartbeat, double TransferSeconds, double HeartbeatSeconds)> ExecuteTransferPhaseAsync(ServiceDefinitionDB service, ServiceEnvironmentConfig envConfig, VpsSettings? vpsOverride, LogCallback log, bool isWindowsService, string publishOutput, string targetPath, ProjectVersion? currentVersion = null, bool skipHeartbeat = false, System.Threading.CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            await log("ERROR", "❌ Missing DeployTargetPath for service environment.", service.Id);
            return (false, null, 0, 0);
        }

        var transferStart = DateTime.UtcNow;

        try
        {
            var transferred = await transferManager.TransferAsync(publishOutput, targetPath, vpsOverride, log, service.Id, ct);
            if (!transferred) throw new Exception("Transfer manager reported failure.");
        }
        catch (Exception ex)
        {
            await log("WARNING", $"🔄 Transfer failed. Attempting to restart service/site to restore availability...", service.Id);
            if (isWindowsService)
                await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id, targetPath);
            else if (service.ServiceType is "WebApi" or "Mvc")
                await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id, vpsOverride);

            await log("ERROR", $"❌ Transfer failed after all attempts: {ex.Message}", service.Id);
            logger.LogError(ex, "Failed to copy/upload files to {Target}", targetPath);
            return (false, null, 0, 0);
        }

        var transferDuration = (DateTime.UtcNow - transferStart).TotalSeconds;

        if (isWindowsService)
            await ManageWindowsServiceAsync(service.IisSiteName, "start", log, service.Id, targetPath);
        else if (service.ServiceType is "WebApi" or "Mvc")
            await ManageIisSiteAsync(service.IisSiteName, "start", log, service.Id, vpsOverride);

        await log("SUCCESS", $"🚀 Deploy complete for: {service.Name}", service.Id);

        if (currentVersion != null)
        {
            await log("INFO", $"📝 Recording version: {currentVersion.CommitHash[..7]} ({currentVersion.Branch})", service.Id);
            await servicesLogic.UpdateVersionAsync(service.Id!, envConfig.EnvironmentId, currentVersion);

            await deployHistoryLogic.AddAsync(new DeploymentHistoryDB
            {
                ServiceId = service.Id!,
                EnvironmentId = envConfig.EnvironmentId,
                Version = currentVersion,
                ConfigSetIds = envConfig.ConfigSetIds,
                CommitHash = currentVersion.CommitHash,
                Created = DateTime.UtcNow
            });
        }

        await log("SUCCESS", $"🎉 {service.Name} finished deployment successfully!", service.Id);

        bool? heartbeatSuccess = null;
        var heartbeatStart = DateTime.UtcNow;
        if (!skipHeartbeat && !string.IsNullOrWhiteSpace(envConfig.HeartbeatUrl))
        {
            heartbeatSuccess = await CheckHeartbeatAsync(envConfig.HeartbeatUrl, log, service.Id);
        }
        var heartbeatDuration = (DateTime.UtcNow - heartbeatStart).TotalSeconds;

        return (true, heartbeatSuccess, transferDuration, heartbeatDuration);
    }

    private async Task ApplyEnvironmentConfigsAsync(ServiceEnvironmentConfig envConfig, VpsSettings? vps, string publishOutput, LogCallback log, string? serviceId)
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

    private async Task ApplyJsonVariablesAsync(string targetFile, List<EnvVariable> variables, LogCallback log, string? serviceId, string type)
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

    private async Task ManageIisSiteAsync(string? iisSiteName, string action, LogCallback log, string? serviceId, VpsSettings? vps = null)
    {
        var actionIcon = action == "start" ? "🏁 Starting" : (action == "recycle" ? "♻️ Recycling" : "🛑 Stopping");
        await log("INFO", $"{actionIcon} IIS Site & AppPool: {iisSiteName}...", serviceId);

        await IisDeployment.RunAsync(iisSiteName, action, null, vps, processRunner, log, serviceId);
    }

    public async Task<bool> CheckHeartbeatAsync(string url, LogCallback log, string? serviceId)
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
                return true;
            }
            else
            {
                await log("WARNING", $"⚠️ Heartbeat returned error: {response.StatusCode}", serviceId);
                return false;
            }
        }
        catch (Exception ex)
        {
            await log("ERROR", $"❌ Heartbeat failed: {ex.Message}", serviceId);
            return false;
        }
    }

    private static string GetEffectiveBranch(ServiceDefinitionDB service, ServiceEnvironmentConfig envConfig, string parsedBranch, string? branchOverride)
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
