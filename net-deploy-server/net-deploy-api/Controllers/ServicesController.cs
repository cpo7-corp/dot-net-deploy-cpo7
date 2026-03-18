using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.IIS;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Services.Entities;

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

        var statuses = await Task.WhenAll(services.Select(async s => {
            string status = "Unknown";
            try {
                status = await iisLogic.GetStatusAsync(s.IisSiteName, s.ServiceType);
            } catch { /* log and silent */ }

            return new ServiceStatus
            {
                Id = s.Id!,
                Name = s.Name,
                ServiceType = s.ServiceType,
                IisSiteName = s.IisSiteName,
                RepoUrl = s.RepoUrl,
                ProjectPath = s.ProjectPath,
                LastDeployed = s.LastDeployed,
                CompileSingleFile = s.CompileSingleFile,
                Environments = s.Environments,
                Status = status
            };
        }));

        return Ok(statuses);
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
}
