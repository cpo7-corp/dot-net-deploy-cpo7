using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.Deploy;
using NET.Deploy.Api.Logic.DeployLogs;
using NET.Deploy.Api.Logic.DeployLogs.Entities;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Services.Entities;
using NET.Deploy.Api.Logic.Settings;
using NET.Deploy.Api.Logic.Settings.Entities;
using System.Collections.Concurrent;
using System.Threading;



namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeployController(
    DeployLogic deployLogic,
    ServicesLogic servicesLogic,
    SettingsLogic settingsLogic,
    DeployLogsLogic deployLogsLogic) : ControllerBase
{
    /// <summary>
    /// Deploy selected services.
    /// Returns Server-Sent Events (SSE) stream so the UI can show live log output.
    /// </summary>
    [HttpPost]
    public async Task Deploy([FromBody] DeployRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var sessionId = Guid.NewGuid().ToString();
        var settings = await settingsLogic.GetAsync();
        var logEntries = new List<DeployLogEntryDB>();

        var responseLock = new SemaphoreSlim(1, 1);

        async Task Log(string level, string message, string? serviceId = null)
        {
            var entry = new DeployLogEntryDB
            {
                SessionId = sessionId,
                Level = level,
                Message = message,
                ServiceId = serviceId,
                Timestamp = DateTime.UtcNow
            };

            lock (logEntries)
            {
                logEntries.Add(entry);
            }

            var line = $"data: {{\"level\":\"{level}\",\"message\":{System.Text.Json.JsonSerializer.Serialize(message)},\"serviceId\":\"{serviceId}\"}}\n\n";

            await responseLock.WaitAsync();
            try
            {
                await Response.WriteAsync(line);
                await Response.Body.FlushAsync();
            }
            finally
            {
                responseLock.Release();
            }
        }

        var vpsSettings = settings.VpsEnvironments.FirstOrDefault(e => e.Id == request.EnvironmentId)
                          ?? settings.VpsEnvironments.FirstOrDefault()
                          ?? new VpsSettings();

        // A session-level dictionary to store repository preparation tasks.
        // This ensures that for every (ServiceId, Branch) combination, we ONLY build once per request.
        var servicePrepTasks = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>>();

        // Helper to get or start a prep task for a specific service's repository + build
        Task<bool> EnsureServicePrepared(ServiceDefinitionDB srv, string? branchOverride)
        {
            var envCfg = srv.Environments.FirstOrDefault(e => e.EnvironmentId == vpsSettings.Id);
            var defaultBranch = envCfg?.DefaultBranch ?? "main";
            var branchKey = branchOverride ?? defaultBranch;
            var prepKey = $"{srv.Id}|{branchKey}";

            return servicePrepTasks.GetOrAdd(prepKey, _ => 
                deployLogic.PrepAndBuildServiceAsync(srv, settings, Log, vpsSettings.Id, branchOverride, request.ForceClean, skipPull: false, request.SkipBuildIfOutputExists)
            );
        }

        // PHASE 1: Full Preparation (Clone + Build + Config all first)
        if (request.CloneAllFirst)
        {
            await Log("INFO", "🔍 [PHASE 1] Preparing all services (Clone, Build, Config)...");
            foreach (var config in request.Services)
            {
                var srv = await servicesLogic.GetByIdAsync(config.ServiceId);
                if (srv != null)
                {
                    // Trigger the prep task and wait for it. 
                    await EnsureServicePrepared(srv, config.Branch);
                }
            }
            await Log("INFO", "✅ [PHASE 1] All services prepared (Builds & Configs ready). Starting transfers...");
        }

        var deployTasks = request.Services.Select(async config =>
        {
            var serviceId = config.ServiceId;
            var service = await servicesLogic.GetByIdAsync(serviceId);
            if (service is null)
            {
                await Log("WARNING", $"⚠️ Service {serviceId} not found – skipping.");
                return (Name: $"Unknown ({serviceId})", Success: false);
            }

            // Always ensure the service is prepared (Clone + Build + Config)
            // If PHASE 1 already ran, EnsureServicePrepared will return the already-completed task.
            var prepSuccess = await EnsureServicePrepared(service, config.Branch);
            if (!prepSuccess)
            {
                await Log("ERROR", $"❌ Preparation failed for {service.Name} – skipping remaining steps.");
                return (Name: service.Name, Success: false);
            }

            // Proceed to deploy (phase 2: transfer & restart)
            // We set skipBuildIfOutputExists to true because we just prepared it.
            var success = await deployLogic.DeployServiceAsync(service, settings, Log, vpsSettings.Id, config.Branch, vpsSettings, request.ForceClean, skipPull: true, skipBuildIfOutputExists: true);

            if (success)
                await servicesLogic.MarkDeployedAsync(serviceId);

            return (Name: service.Name, Success: success);
        });

        var resultsArray = await Task.WhenAll(deployTasks);
        var results = resultsArray.ToList();

        await Log("INFO", "──────────────────────────────────────────────────");
        await Log("INFO", "📊 DEPLOYMENT SUMMARY:");
        foreach (var res in results)
        {
            var status = res.Success ? "✅ SUCCESS" : "❌ FAILED";
            await Log("INFO", $"  • {res.Name.PadRight(20)} : {status}");
        }

        var totalCount = results.Count;
        var successCount = results.Count(r => r.Success);
        var level = successCount == totalCount ? "SUCCESS" : (successCount == 0 ? "ERROR" : "WARNING");

        await Log(level, $"🏁 Final Result: {successCount}/{totalCount} services deployed successfully.");
        await Log("INFO", "──────────────────────────────────────────────────");

        // Persist the whole session log to MongoDB
        await deployLogsLogic.AddRangeAsync(logEntries);

        await Response.WriteAsync("data: {\"level\":\"DONE\",\"message\":\"done\"}\n\n");
        await Response.Body.FlushAsync();
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
            var entry = new DeployLogEntryDB { SessionId = sessionId, Level = level, Message = message, ServiceId = serviceId, Timestamp = DateTime.UtcNow };
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

    public record ServiceDeploymentConfig(string ServiceId, string? Branch);
    public record ServiceActionRequest(string ServiceId, string EnvironmentId, string Action);
    public record DeployRequest(List<ServiceDeploymentConfig> Services, string? EnvironmentId, bool ForceClean = false, bool CloneAllFirst = false, bool SkipBuildIfOutputExists = false);
}
