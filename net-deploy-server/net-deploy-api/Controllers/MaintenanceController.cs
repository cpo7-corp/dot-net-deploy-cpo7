using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.Maintenance;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MaintenanceController(MaintenanceLogic logic) : ControllerBase
{
    [HttpGet("export")]
    public async Task<ActionResult<FullDatabaseExport>> Export() =>
        Ok(await logic.ExportAsync());

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] FullDatabaseExport data)
    {
        await logic.ImportAsync(data);
        return Ok(new { Message = "Imported successfully." });
    }
}
