using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.Services;
using NET.Deploy.Api.Logic.Services.Entities;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/env-configs")]
public class EnvConfigsController(EnvConfigsLogic logic) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<EnvConfigSetDB>>> GetAll() =>
        Ok(await logic.GetAllAsync());

    [HttpGet("{id}")]
    public async Task<ActionResult<EnvConfigSetDB>> GetById(string id)
    {
        var result = await logic.GetByIdAsync(id);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<EnvConfigSetDB>> Create([FromBody] EnvConfigSetDB config)
    {
        var created = await logic.CreateAsync(config);
        return Ok(created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<EnvConfigSetDB>> Update(string id, [FromBody] EnvConfigSetDB config)
    {
        var updated = await logic.UpdateAsync(id, config);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var deleted = await logic.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
