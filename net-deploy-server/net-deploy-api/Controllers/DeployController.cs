using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Data.Entities;
using NET.Deploy.Api.Logic.Deploy;
using NET.Deploy.Api.Logic.DeployLogs;
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
    bool Deploy = true);


[ApiController]
[Route("api/[controller]")]
public class DeployController(
    DeployLogic deployLogic,
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

        async Task Log(string level, string message, string? serviceId = null)
        {
            var entry = new DeployLogEntryDB { SessionId = sessionId, Level = level, Message = message, ServiceId = serviceId, Created = DateTime.UtcNow };
            lock (logEntries) { logEntries.Add(entry); }
            var line = $"data: {{\"level\":\"{level}\",\"message\":{System.Text.Json.JsonSerializer.Serialize(message)},\"serviceId\":\"{serviceId}\"}}\n\n";
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
            var servicePrepTasks = new ConcurrentDictionary<string, Task<bool>>();

            Task<bool> EnsureServicePrepared(ServiceDefinitionDB srv, string? branchOverride)
            {
                var envCfg = srv.Environments.FirstOrDefault(e => e.EnvironmentId == vpsSettings.Id);
                var defaultBranch = envCfg?.DefaultBranch ?? "main";
                var branchKey = branchOverride ?? defaultBranch;
                var prepKey = $"{srv.Id}|{branchKey}";

                return servicePrepTasks.GetOrAdd(prepKey, async _ =>
                {
                    var (repoUrl, gitBranch, _) = deployLogic.ParseGitUrl(srv.RepoUrl);
                    var effectiveRepoBranch = branchOverride ?? (string.IsNullOrWhiteSpace(envCfg?.DefaultBranch) ? gitBranch : envCfg.DefaultBranch);
                    var repoKey = $"{repoUrl}|{effectiveRepoBranch}";

                    var repoUpdated = !request.Pull || await repoUpdateTasks.GetOrAdd(repoKey, _ =>
                        deployLogic.PrepGitOnlyAsync(srv, settings, Log, vpsSettings.Id, branchOverride, request.ForceClean, cts.Token)
                    );

                    if (!repoUpdated) return false;
                    return await deployLogic.PrepAndBuildServiceAsync(srv, settings, Log, vpsSettings.Id, branchOverride, request.ForceClean, skipPull: true, skipBuildIfOutputExists: !request.Build, cts.Token);
                });
            }

            if (request.Pull)
            {
                await Log("INFO", "🔍 [PHASE 1] Preparing all services (Pull, Build, Config)...");
                foreach (var config in request.Services)
                {
                    if (cts.IsCancellationRequested) break;
                    pauseEvent.Wait(cts.Token);

                    var srv = await servicesLogic.GetByIdAsync(config.ServiceId);
                    if (srv != null) await EnsureServicePrepared(srv, config.Branch);
                }
                if (cts.IsCancellationRequested) { await Log("WARNING", "🛑 [PHASE 1] Deployment cancelled."); return; }
                await Log("INFO", "✅ [PHASE 1] All services prepared. Starting next phase...");
            }

            var results = new List<ServiceStatus>();
            foreach (var config in request.Services)
            {
                pauseEvent.Wait(cts.Token);

                var service = await servicesLogic.GetByIdAsync(config.ServiceId);
                if (service is null) { await Log("WARNING", $"⚠️ Service {config.ServiceId} not found."); continue; }

                var status = new ServiceStatus { Name = service.Name };
                results.Add(status);
                var startTime = DateTime.UtcNow;

                var prepSuccess = await EnsureServicePrepared(service, config.Branch);
                status.Built = prepSuccess;

                if (!prepSuccess) 
                { 
                    status.Duration = DateTime.UtcNow - startTime;
                    await Log("ERROR", $"❌ Preparation failed for {service.Name}."); 
                    continue; 
                }

                if (!request.Deploy) 
                { 
                    status.Duration = DateTime.UtcNow - startTime;
                    await Log("SUCCESS", $"✅ {service.Name} built (Deployment skipped)."); 
                    continue; 
                }

                var deployResult = await deployLogic.DeployServiceAsync(service, settings, Log, vpsSettings.Id, config.Branch, vpsSettings, request.ForceClean, skipPull: true, skipBuildIfOutputExists: true, cts.Token);
                status.Deployed = deployResult.Success;
                status.Heartbeat = deployResult.Heartbeat;
                status.Duration = DateTime.UtcNow - startTime;

                if (deployResult.Success) await servicesLogic.MarkDeployedAsync(service.Id!);
            }

            if (cts.IsCancellationRequested) await Log("WARNING", "🛑 Deployment stopped by user.");

            await Log("INFO", "──────────────────────────────────────────────────");
            await Log("INFO", "📊 DEPLOYMENT SUMMARY:");
            await Log("INFO", $"{"Service Name".PadRight(25)} | {"Build".PadRight(8)} | {"Deployed".PadRight(8)} | {"Heartbeat".PadRight(10)} | {"Time".PadRight(8)}");
            await Log("INFO", new string('─', 70));

            foreach (var s in results)
            {
                var builtStr = s.Built == true ? "✅ OK" : (s.Built == false ? "❌ FAIL" : "➖");
                var deployStr = s.Deployed == true ? "✅ OK" : (s.Deployed == false ? "❌ FAIL" : "➖");
                var heartStr = s.Heartbeat == true ? "💚 OK" : (s.Heartbeat == false ? "💔 FAIL" : "➖");
                var timeStr = $"{(int)s.Duration.TotalMinutes}m {s.Duration.Seconds}s";

                await Log("INFO", $"{s.Name.PadRight(25)} | {builtStr.PadRight(8)} | {deployStr.PadRight(8)} | {heartStr.PadRight(10)} | {timeStr.PadRight(8)}");
            }

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
        public bool? Deployed { get; set; }
        public bool? Heartbeat { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
