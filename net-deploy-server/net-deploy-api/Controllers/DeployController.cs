using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.Deploy;
using NET.Deploy.Api.Logic.DeployLogs;
using NET.Deploy.Api.Logic.DeployLogs.Entities;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Settings;
using NET.Deploy.Api.Logic.Settings.Entities;

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
        var logEntries = new List<DeployLogEntry>();

        async Task Log(string level, string message, string? serviceId = null)
        {
            var entry = new DeployLogEntry
            {
                SessionId = sessionId,
                Level = level,
                Message = message,
                ServiceId = serviceId,
                Timestamp = DateTime.UtcNow
            };
            logEntries.Add(entry);

            // SSE format: data: <json>\n\n
            var line = $"data: {{\"level\":\"{level}\",\"message\":{System.Text.Json.JsonSerializer.Serialize(message)},\"serviceId\":\"{serviceId}\"}}\n\n";
            await Response.WriteAsync(line);
            await Response.Body.FlushAsync();
        }

        var results = new List<(string Name, bool Success)>();

        var vpsSettings = settings.VpsEnvironments.FirstOrDefault(e => e.Id == request.EnvironmentId)
                          ?? settings.VpsEnvironments.FirstOrDefault()
                          ?? new VpsSettings();

        foreach (var config in request.Services)
        {
            var serviceId = config.ServiceId;
            var service = await servicesLogic.GetByIdAsync(serviceId);
            if (service is null)
            {
                await Log("WARNING", $"⚠️ Service {serviceId} not found – skipping.");
                results.Add(($"Unknown ({serviceId})", false));
                continue;
            }

            var success = await deployLogic.DeployServiceAsync(service, settings, Log, config.Branch, vpsSettings, request.ForceClean);
            results.Add((service.Name, success));

            if (success)
                await servicesLogic.MarkDeployedAsync(serviceId);
        }

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

    /// <summary>Returns the log entries of a previous deploy session.</summary>
    [HttpGet("logs/{sessionId}")]
    public async Task<ActionResult<List<DeployLogEntry>>> GetLogs(string sessionId) =>
        Ok(await deployLogsLogic.GetBySessionAsync(sessionId));

    /// <summary>Returns IDs of the most recent deploy sessions.</summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<List<string>>> GetRecentSessions([FromQuery] int count = 10) =>
        Ok(await deployLogsLogic.GetRecentSessionsAsync(count));

    public record ServiceDeploymentConfig(string ServiceId, string? Branch);
    public record DeployRequest(List<ServiceDeploymentConfig> Services, string? EnvironmentId, bool ForceClean = false);
}
