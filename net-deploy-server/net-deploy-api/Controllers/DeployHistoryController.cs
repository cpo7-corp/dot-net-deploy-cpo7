using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.DeployHistory;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeployHistoryController(DeployHistoryLogic historyLogic) : ControllerBase
{
    [HttpGet("{serviceId}/{environmentId}")]
    public async Task<IActionResult> GetByService(string serviceId, string environmentId, [FromQuery] int limit = 20)
    {
        var history = await historyLogic.GetByServiceAsync(serviceId, environmentId, limit);
        return Ok(history);
    }
}
