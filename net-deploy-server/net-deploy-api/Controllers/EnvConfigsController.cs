using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.EnvConfigs;
using NET.Deploy.Api.Logic.EnvConfigs.Entities;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EnvConfigsController(EnvConfigsLogic logic, ILogger<EnvConfigsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<EnvConfigSetDB>>> GetAll()
    {
        var configs = await logic.GetAllAsync();
        return Ok(configs);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EnvConfigSetDB>> GetById(string id)
    {
        var config = await logic.GetByIdAsync(id);
        return config is null ? NotFound() : Ok(config);
    }

    [HttpPost]
    public async Task<ActionResult<EnvConfigSetDB>> Create([FromBody] EnvConfigSetDB envConfigSet)
    {
        logger.LogInformation("Creating config set: {Name}", envConfigSet.Name);
        var created = await logic.CreateAsync(envConfigSet);
        return Ok(created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<EnvConfigSetDB>> Update(string id, [FromBody] EnvConfigSetDB envConfigSet)
    {
        var updated = await logic.UpdateAsync(id, envConfigSet);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var deleted = await logic.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
