using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Data.Entities;
using NET.Deploy.Api.Logic.IIS;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Services.Entities;
using System.Net;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServicesController(ServicesLogic servicesLogic, IISLogic iisLogic, ILogger<ServicesController> logger) : ControllerBase
{
    /// <summary>Returns all services with their live IIS status.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ServiceStatus>>> GetAll()
    {
        var services = await servicesLogic.GetAllAsync();

        var statuses = await Task.WhenAll(services.Select(async s =>
        {
            string status = "Unknown";
            try
            {
                status = await iisLogic.GetStatusAsync(s.IisSiteName, s.ServiceType);
            }
            catch { /* log and silent */ }

            return new ServiceStatus
            {
                Id = s.Id!,
                Name = s.Name,
                Group = s.Group,
                ServiceType = s.ServiceType,
                IisSiteName = s.IisSiteName,
                RepoUrl = s.RepoUrl,
                ProjectPath = s.ProjectPath,
                Updated = s.Updated,
                CompileSingleFile = s.CompileSingleFile,
                Environments = s.Environments,
                Status = status
            };
        }));

        return Ok(statuses);
    }

    [HttpGet("heartbeats")]
    public async Task<ActionResult<List<ServiceHeartbeatStatus>>> GetHeartbeats([FromQuery] string environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            return BadRequest("environmentId is required.");

        var services = await servicesLogic.GetAllAsync();
        var results = await Task.WhenAll(services.Select(s => CheckHeartbeatAsync(s, environmentId)));
        return Ok(results.Where(r => !string.IsNullOrWhiteSpace(r.ServiceId)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<ServiceDefinitionDB>> Create([FromBody] ServiceDefinitionDB service)
    {
        logger.LogInformation("Creating service: {Name}", service.Name);
        var created = await servicesLogic.CreateAsync(service);
        return Ok(created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ServiceDefinitionDB>> Update(string id, [FromBody] ServiceDefinitionDB service)
    {
        var updated = await servicesLogic.UpdateAsync(id, service);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var deleted = await servicesLogic.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("reorder")]
    public async Task<IActionResult> Reorder([FromBody] List<string> ids)
    {
        await servicesLogic.UpdateOrderAsync(ids);
        return Ok();
    }

    private static async Task<ServiceHeartbeatStatus> CheckHeartbeatAsync(ServiceDefinitionDB service, string environmentId)
    {
        var envConfig = service.Environments.FirstOrDefault(e => e.EnvironmentId == environmentId);
        var heartbeatUrl = envConfig?.HeartbeatUrl?.Trim();

        if (string.IsNullOrWhiteSpace(service.Id) || string.IsNullOrWhiteSpace(heartbeatUrl))
        {
            return new ServiceHeartbeatStatus
            {
                ServiceId = service.Id ?? string.Empty,
                Status = "Unknown"
            };
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(heartbeatUrl);

            return new ServiceHeartbeatStatus
            {
                ServiceId = service.Id,
                Status = response.IsSuccessStatusCode ? "Running" : "Stopped",
                HttpStatusCode = (int)response.StatusCode
            };
        }
        catch
        {
            return new ServiceHeartbeatStatus
            {
                ServiceId = service.Id,
                Status = "Stopped",
                HttpStatusCode = (int)HttpStatusCode.ServiceUnavailable
            };
        }
    }
}
