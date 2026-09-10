using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Data.Entities;
using NET.Deploy.Api.Logic.Deploy;
using NET.Deploy.Api.Logic.DeployLogs;
using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Settings;
using System.Collections.Concurrent;



namespace NET.Deploy.Api.Controllers;

public record ServiceDeploymentConfig(string ServiceId, string? Branch);
public record ServiceActionRequest(string ServiceId, string EnvironmentId, string Action);
public record DeployRequest(
    List<ServiceDeploymentConfig> Services,
    string? EnvironmentId,
    bool ForceClean = false,
    bool Pull = true,
    bool Build = true,
    bool Deploy = true,
    bool WaitAllBuildsToDeploy = true);


[ApiController]
[Route("api/[controller]")]
public class DeployController(
    DeployLogic deployLogic,
    GitLogic gitLogic,
    ServicesLogic servicesLogic,
    SettingsLogic settingsLogic,
    DeployLogsLogic deployLogsLogic) : ControllerBase
{
    private static readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, ManualResetEventSlim PauseEvent)> _activeSessions = new();

    /// <summary>
    /// Stop an active deployment session.
    /// </summary>
    [HttpPost("stop/{sessionId}")]
    public IActionResult Stop(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.Cts.Cancel();
            session.PauseEvent.Set(); // Ensure it's not stuck in pause
            return Ok(new { message = "Stop request sent." });
        }
        return NotFound();
    }

    /// <summary>
    /// Pause an active deployment session.
    /// </summary>
    [HttpPost("pause/{sessionId}")]
    public IActionResult Pause(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.PauseEvent.Reset();
            return Ok(new { message = "Paused." });
        }
        return NotFound();
    }

    /// <summary>
    /// Resume a paused deployment session.
    /// </summary>
    [HttpPost("resume/{sessionId}")]
    public IActionResult Resume(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            session.PauseEvent.Set();
            return Ok(new { message = "Resumed." });
        }
        return NotFound();
    }
    [HttpPost]
    public async Task Deploy([FromBody] DeployRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var sessionId = Guid.NewGuid().ToString();
        var cts = new CancellationTokenSource();
        var pauseEvent = new ManualResetEventSlim(true);
        _activeSessions[sessionId] = (cts, pauseEvent);

        var logEntries = new List<DeployLogEntryDB>();
        var responseLock = new SemaphoreSlim(1, 1);
        var serviceNamesMap = new ConcurrentDictionary<string, string>();

        foreach (var config in request.Services)
        {
            var srv = await servicesLogic.GetByIdAsync(config.ServiceId);
            if (srv?.Id != null && !string.IsNullOrEmpty(srv.Name))
            {
                serviceNamesMap[srv.Id] = srv.Name;
            }
        }

        async Task Log(string level, string message, string? serviceId = null)
        {
            var formattedMessage = message;
            if (!string.IsNullOrEmpty(serviceId) && serviceNamesMap.TryGetValue(serviceId, out var srvName))
            {
                if (!formattedMessage.StartsWith($"[{srvName}]"))
                {
                    formattedMessage = $"[{srvName}] {formattedMessage}";
                }
            }

            var entry = new DeployLogEntryDB { SessionId = sessionId, Level = level, Message = formattedMessage, ServiceId = serviceId, Created = DateTime.UtcNow };
            lock (logEntries) { logEntries.Add(entry); }
            var line = $"data: {{\"level\":\"{level}\",\"message\":{System.Text.Json.JsonSerializer.Serialize(formattedMessage)},\"serviceId\":\"{serviceId}\"}}\n\n";
            await responseLock.WaitAsync();
            try { await Response.WriteAsync(line); await Response.Body.FlushAsync(); }
            finally { responseLock.Release(); }
        }

        try
        {
            await Log("SESSION_ID", sessionId); // UI uses this to call Stop/Pause
            var settings = await settingsLogic.GetAsync();

            var vpsSettings = settings.VpsEnvironments.FirstOrDefault(e => e.Id == request.EnvironmentId)
                              ?? settings.VpsEnvironments.FirstOrDefault()
                              ?? new VpsSettings();

            var repoUpdateTasks = new ConcurrentDictionary<string, Task<bool>>();
            var servicePrepTasks = new ConcurrentDictionary<string, Task<(bool Success, double BuildSeconds)>>();

            async Task<bool> DownloadAllRepositoriesAsync()
            {
                if (!request.Pull) return true;

                await Log("INFO", "📥 [PHASE 1] Downloading all repositories before builds...");
                var repoBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var downloadRequests = new List<(ServiceDefinitionDB Service, string? BranchOverride, string RepoKey)>();

                foreach (var config in request.Services)
                {
                    var service = await servicesLogic.GetByIdAsync(config.ServiceId);
                    if (service == null)
                    {
                        await Log("ERROR", $"❌ Service {config.ServiceId} not found.");
                        return false;
                    }

                    var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == vpsSettings.Id);
                    if (envConfig == null)
                    {
                        await Log("ERROR", $"❌ Environment configuration not found for {service.Name}.", service.Id);
                        return false;
                    }

                    var (repoUrl, parsedBranch, _) = deployLogic.ParseGitUrl(service.RepoUrl);
                    var branch = config.Branch ?? (string.IsNullOrWhiteSpace(envConfig.DefaultBranch) ? parsedBranch : envConfig.DefaultBranch);
                    var repoLocalPath = gitLogic.GetRepoLocalPath(settings.Git, repoUrl);

                    if (repoBranches.TryGetValue(repoLocalPath, out var existingBranch) &&
                        !string.Equals(existingBranch, branch, StringComparison.OrdinalIgnoreCase))
                    {
                        await Log("ERROR", $"❌ Repository {repoLocalPath} cannot use branches {existingBranch} and {branch} in the same deployment.");
                        return false;
                    }

                    repoBranches[repoLocalPath] = branch;
                    var repoKey = $"{repoLocalPath.ToLowerInvariant()}|{branch.ToLowerInvariant()}";
                    downloadRequests.Add((service, config.Branch, repoKey));
                }

                var downloadTasks = downloadRequests
                    .Select(item => repoUpdateTasks.GetOrAdd(
                        item.RepoKey,
                        _ => deployLogic.PrepGitOnlyAsync(
                            item.Service,
                            settings,
                            Log,
                            vpsSettings.Id,
                            item.BranchOverride,
                            request.ForceClean,
                            cts.Token)))
                    .ToList();

                var downloadResults = await Task.WhenAll(downloadTasks);
                if (downloadResults.Any(success => !success))
                {
                    await Log("ERROR", "❌ One or more repository downloads failed. Builds were not started.");
                    return false;
                }

                await Log("SUCCESS", "✅ [PHASE 1] All repositories are ready. Starting builds...");
                return true;
            }

            Task<(bool Success, double BuildSeconds)> EnsureServicePrepared(ServiceDefinitionDB srv, string? branchOverride)
            {
                var envCfg = srv.Environments.FirstOrDefault(e => e.EnvironmentId == vpsSettings.Id);
                var defaultBranch = envCfg?.DefaultBranch ?? "main";
                var branchKey = branchOverride ?? defaultBranch;
                var prepKey = $"{srv.Id}|{branchKey}";

                return servicePrepTasks.GetOrAdd(prepKey, async _ =>
                {
                    var prepStart = DateTime.UtcNow;
                    var success = await deployLogic.PrepAndBuildServiceAsync(srv, settings, Log, vpsSettings.Id, branchOverride, request.ForceClean, skipPull: true, skipBuildIfOutputExists: !request.Build, cts.Token);
                    return (success, (DateTime.UtcNow - prepStart).TotalSeconds);
                });
            }

            var sessionStartTime = DateTime.UtcNow;
            var results = new ConcurrentBag<ServiceStatus>();
            var deployedForHeartbeat = new ConcurrentBag<(ServiceStatus Status, ServiceDefinitionDB Service, ServiceEnvironmentConfig EnvCfg)>();

            if (!await DownloadAllRepositoriesAsync()) return;

            async Task ProcessServiceDeployment(ServiceDeploymentConfig config)
            {
                if (cts.IsCancellationRequested) return;
                pauseEvent.Wait(cts.Token);

                var service = await servicesLogic.GetByIdAsync(config.ServiceId);
                if (service is null) { await Log("WARNING", $"⚠️ Service {config.ServiceId} not found."); return; }

                var status = new ServiceStatus { Name = service.Name };
                results.Add(status);
                var startTime = DateTime.UtcNow;

                var (prepSuccess, buildSeconds) = await EnsureServicePrepared(service, config.Branch);
                status.BuildSeconds = buildSeconds;
                status.Built = prepSuccess;

                if (!prepSuccess) 
                { 
                    status.Duration = TimeSpan.FromSeconds(buildSeconds) + (DateTime.UtcNow - startTime);
                    await Log("ERROR", $"❌ Preparation failed for {service.Name}.", service.Id); 
                    return; 
                }

                if (!request.Deploy) 
                { 
                    status.Duration = TimeSpan.FromSeconds(buildSeconds) + (DateTime.UtcNow - startTime);
                    await Log("SUCCESS", $"✅ {service.Name} built (Deployment skipped).", service.Id); 
                    return; 
                }

                var stepStart = DateTime.UtcNow;
                var deployResult = await deployLogic.DeployServiceAsync(service, settings, Log, vpsSettings.Id, config.Branch, vpsSettings, request.ForceClean, skipPull: true, skipBuildIfOutputExists: true, skipHeartbeat: true, ct: cts.Token);
                status.DeploySeconds = deployResult.TransferSeconds;
                status.Deployed = deployResult.Success;
                status.Duration = TimeSpan.FromSeconds(buildSeconds) + (DateTime.UtcNow - startTime);

                if (deployResult.Success)
                {
                    await servicesLogic.MarkDeployedAsync(service.Id!);
                    var envCfg = service.Environments.FirstOrDefault(e => e.EnvironmentId == vpsSettings.Id);
                    if (envCfg != null && !string.IsNullOrWhiteSpace(envCfg.HeartbeatUrl))
                    {
                        deployedForHeartbeat.Add((status, service, envCfg));
                    }
                }
            }

            if (request.WaitAllBuildsToDeploy)
            {
                if (request.Pull || request.Build)
                {
                    await Log("INFO", "🔨 [PHASE 2] Building all services (waiting for all builds before deploy)...");
                    var prepTasks = request.Services.Select(async config =>
                    {
                        if (cts.IsCancellationRequested) return;
                        pauseEvent.Wait(cts.Token);
                        var srv = await servicesLogic.GetByIdAsync(config.ServiceId);
                        if (srv != null) await EnsureServicePrepared(srv, config.Branch);
                    });
                    await Task.WhenAll(prepTasks);
                    if (cts.IsCancellationRequested) { await Log("WARNING", "🛑 Deployment cancelled after preparation phase."); return; }
                    await Log("INFO", "✅ [PHASE 2] All service builds completed. Starting deployment phase...");
                }

                await Log("INFO", "🚀 [PHASE 3] Deploying all services in parallel...");
                await Task.WhenAll(request.Services.Select(config => ProcessServiceDeployment(config)));
            }
            else
            {
                await Log("INFO", "⚡ [STREAMING] Running independent end-to-end pipeline for each service concurrently...");
                await Task.WhenAll(request.Services.Select(config => ProcessServiceDeployment(config)));
            }

            if (request.Deploy && deployedForHeartbeat.Count > 0 && !cts.IsCancellationRequested)
            {
                await Log("INFO", "💓 [PHASE 3] Checking heartbeats for all deployed services concurrently...");
                var heartbeatTasks = deployedForHeartbeat.Select(async item =>
                {
                    var hbStart = DateTime.UtcNow;
                    var hbSuccess = await deployLogic.CheckHeartbeatAsync(item.EnvCfg.HeartbeatUrl, Log, item.Service.Id);
                    item.Status.Heartbeat = hbSuccess;
                    item.Status.HeartbeatSeconds = (DateTime.UtcNow - hbStart).TotalSeconds;
                    item.Status.Duration += TimeSpan.FromSeconds(item.Status.HeartbeatSeconds);
                });
                await Task.WhenAll(heartbeatTasks);
            }

            if (cts.IsCancellationRequested) await Log("WARNING", "🛑 Deployment stopped by user.");

            await Log("INFO", "──────────────────────────────────────────────────");
            await Log("INFO", "📊 DEPLOYMENT SUMMARY:");
            await Log("INFO", $"{"Service Name".PadRight(25)} | {"Build".PadRight(12)} | {"Deployed".PadRight(12)} | {"Heartbeat".PadRight(10)} | {"Time".PadRight(8)}");
            await Log("INFO", new string('─', 80));

            foreach (var s in results)
            {
                var builtStr = s.Built == true ? $"✅ OK ({(int)s.BuildSeconds}s)" : (s.Built == false ? "❌ FAIL" : "➖");
                var deployStr = s.Deployed == true ? $"✅ OK ({(int)s.DeploySeconds}s)" : (s.Deployed == false ? "❌ FAIL" : "➖");
                var heartStr = s.Heartbeat == true ? $"💚 OK ({(int)s.HeartbeatSeconds}s)" : (s.Heartbeat == false ? "💔 FAIL" : "➖");
                var timeStr = $"{(int)s.Duration.TotalSeconds}s";

                await Log("INFO", $"{s.Name.PadRight(25)} | {builtStr.PadRight(12)} | {deployStr.PadRight(12)} | {heartStr.PadRight(10)} | {timeStr.PadRight(8)}");
            }

            var totalDuration = DateTime.UtcNow - sessionStartTime;
            await Log("INFO", new string('─', 80));
            await Log("INFO", $"TOTAL TIME: {totalDuration.TotalSeconds:F1}s");
            await Log("INFO", new string('─', 80));

            var successCount = results.Count(r => r.Deployed == true || (r.Deployed == null && r.Built == true));
            var level = successCount == results.Count ? "SUCCESS" : (successCount == 0 ? "ERROR" : "WARNING");
            await Log(level, $"🏁 Final Result: {successCount}/{results.Count} services processed.");
            await Log("INFO", "──────────────────────────────────────────────────");

            await deployLogsLogic.AddRangeAsync(logEntries);
            await Response.WriteAsync("data: {\"level\":\"DONE\",\"message\":\"done\"}\n\n");
            await Response.Body.FlushAsync();
        }
        finally
        {
            _activeSessions.TryRemove(sessionId, out _);
            cts.Dispose();
        }
    }

    [HttpPost("service-action")]
    public async Task ServiceAction([FromBody] ServiceActionRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var sessionId = Guid.NewGuid().ToString();
        var settings = await settingsLogic.GetAsync();
        var responseLock = new SemaphoreSlim(1, 1);
        var logEntries = new List<DeployLogEntryDB>();

        async Task Log(string level, string message, string? serviceId = null)
        {
            var entry = new DeployLogEntryDB { SessionId = sessionId, Level = level, Message = message, ServiceId = serviceId, Created = DateTime.UtcNow };
            lock (logEntries) { logEntries.Add(entry); }
            var line = $"data: {{\"level\":\"{level}\",\"message\":{System.Text.Json.JsonSerializer.Serialize(message)},\"serviceId\":\"{serviceId}\"}}\n\n";
            await responseLock.WaitAsync();
            try { await Response.WriteAsync(line); await Response.Body.FlushAsync(); }
            finally { responseLock.Release(); }
        }

        var service = await servicesLogic.GetByIdAsync(request.ServiceId);
        if (service != null)
        {
            await deployLogic.PerformServiceActionAsync(service, request.EnvironmentId, request.Action, settings, Log);
        }

        await deployLogsLogic.AddRangeAsync(logEntries);
        await Response.WriteAsync("data: {\"level\":\"DONE\",\"message\":\"done\"}\n\n");
    }

    /// <summary>Returns the log entries of a previous deploy session.</summary>
    [HttpGet("logs/{sessionId}")]
    public async Task<ActionResult<List<DeployLogEntryDB>>> GetLogs(string sessionId) =>
        Ok(await deployLogsLogic.GetBySessionAsync(sessionId));

    /// <summary>Returns IDs of the most recent deploy sessions.</summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<List<string>>> GetRecentSessions([FromQuery] int count = 10) =>
        Ok(await deployLogsLogic.GetRecentSessionsAsync(count));

    /// <summary>Returns paged session summaries for Deployment History.</summary>
    [HttpGet("sessions-paged")]
    public async Task<ActionResult<List<DeployLogsLogic.SessionSummary>>> GetSessionsPaged([FromQuery] int skip = 0, [FromQuery] int limit = 20) =>
        Ok(await deployLogsLogic.GetSessionsPagedAsync(skip, limit));

    public class ServiceStatus
    {
        public string Name { get; set; } = "";
        public bool? Built { get; set; }
        public double BuildSeconds { get; set; }
        public bool? Deployed { get; set; }
        public double DeploySeconds { get; set; }
        public bool? Heartbeat { get; set; }
        public double HeartbeatSeconds { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
