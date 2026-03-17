using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.Settings.Entities;
using NET.Deploy.Api.Logic.Settings;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController(SettingsLogic settingsLogic) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AppSettings>> Get() =>
        Ok(await settingsLogic.GetAsync());

    [HttpPut]
    public async Task<ActionResult<AppSettings>> Save([FromBody] AppSettings settings) =>
        Ok(await settingsLogic.SaveAsync(settings));
}
